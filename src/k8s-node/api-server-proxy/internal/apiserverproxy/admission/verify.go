package admission

import (
	"context"
	"crypto"
	"crypto/ecdsa"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/x509"
	_ "embed"
	"encoding/base64"
	"encoding/json"
	"encoding/pem"
	"fmt"
	"log"
	"os"
	"strings"

	"github.com/open-policy-agent/opa/v1/rego"
)

//go:embed policy_engine.rego
var policyEngineRego string

const (
	// PolicyAnnotation is the annotation key for the signed policy (base64-encoded JSON)
	PolicyAnnotation = "api-server-proxy.io/policy"

	// SignatureAnnotation is the annotation key for the policy signature
	SignatureAnnotation = "api-server-proxy.io/signature"
)

// PolicyVerificationController verifies pod policy signatures
type PolicyVerificationController struct {
	publicKey   crypto.PublicKey
	certPath    string
	policyQuery rego.PreparedEvalQuery
	logger      *log.Logger
}

// NewPolicyVerificationController creates a new pod policy verification controller
func NewPolicyVerificationController(certPath string) (*PolicyVerificationController, error) {
	logger := log.New(os.Stdout, "[policy-verification] ", log.LstdFlags|log.Lmicroseconds)

	publicKey, err := loadPublicKey(certPath)
	if err != nil {
		return nil, fmt.Errorf("failed to load public key from %s: %w", certPath, err)
	}

	logger.Printf("Loaded public key from %s", certPath)

	policyQuery, err := preparePolicyEval()
	if err != nil {
		return nil, fmt.Errorf("failed to prepare rego policy engine: %w", err)
	}

	logger.Printf("Compiled rego policy engine")

	return &PolicyVerificationController{
		publicKey:   publicKey,
		certPath:    certPath,
		policyQuery: policyQuery,
		logger:      logger,
	}, nil
}

// Name returns the name of the controller
func (c *PolicyVerificationController) Name() string {
	return "policy-verification"
}

// Admit verifies the pod policy signature and checks if the pod matches the policy
func (c *PolicyVerificationController) Admit(req *Request) *Decision {
	// Get the policy and signature from annotations
	policyStr, hasPolicy := c.getAnnotation(req.Pod, PolicyAnnotation)
	if !hasPolicy {
		c.logger.Printf("Pod %s/%s has no policy annotation", req.Namespace, req.Name)
		return Deny("pod policy required but not found (missing annotation: " + PolicyAnnotation + ")")
	}

	signature, hasSignature := c.getAnnotation(req.Pod, SignatureAnnotation)
	if !hasSignature {
		c.logger.Printf("Pod %s/%s has no signature annotation", req.Namespace, req.Name)
		return Deny("pod signature required but not found (missing annotation: " + SignatureAnnotation + ")")
	}

	// Decode the policy from base64
	policyBytes, err := base64.StdEncoding.DecodeString(policyStr)
	if err != nil {
		c.logger.Printf("Invalid policy encoding for %s/%s: %v", req.Namespace, req.Name, err)
		return Deny("invalid policy encoding: must be base64")
	}

	// Verify the signature on the decoded policy bytes
	policyHash := sha256.Sum256(policyBytes)
	signatureBytes, err := base64.StdEncoding.DecodeString(signature)
	if err != nil {
		c.logger.Printf("Invalid signature encoding for %s/%s: %v", req.Namespace, req.Name, err)
		return Deny("invalid signature encoding: must be base64")
	}

	if err := c.verifySignature(policyHash[:], signatureBytes); err != nil {
		c.logger.Printf("Policy signature verification failed for %s/%s: %v", req.Namespace, req.Name, err)
		return Deny(fmt.Sprintf("policy signature verification failed: %v", err))
	}

	c.logger.Printf("Policy signature verified for pod %s/%s", req.Namespace, req.Name)

	// Evaluate the Rego policy against the pod spec
	if err := c.evaluateRegoPolicy(policyBytes, req.Pod); err != nil {
		c.logger.Printf("Pod %s/%s does not match policy: %v", req.Namespace, req.Name, err)
		return Deny(fmt.Sprintf("pod does not match policy: %v", err))
	}

	c.logger.Printf("Pod %s/%s matches signed policy, allowing", req.Namespace, req.Name)
	return Allow("pod matches signed policy")
}

// getAnnotation extracts an annotation from pod metadata
func (c *PolicyVerificationController) getAnnotation(pod map[string]interface{}, key string) (string, bool) {
	metadata, ok := pod["metadata"].(map[string]interface{})
	if !ok {
		return "", false
	}

	annotations, ok := metadata["annotations"].(map[string]interface{})
	if !ok {
		return "", false
	}

	value, ok := annotations[key].(string)
	return value, ok
}

// preparePolicyEval compiles the embedded rego module and prepares the policy
// query for evaluation. The returned PreparedEvalQuery is reused for every
// admission check — only the input changes per request.
func preparePolicyEval() (rego.PreparedEvalQuery, error) {
	query := `{"allow": data.kubeletproxy.admission.allow, ` +
		`"reasons": data.kubeletproxy.admission.reasons}`

	opts := []func(*rego.Rego){
		rego.Query(query),
		rego.Module("policy_engine.rego", policyEngineRego),
		rego.EnablePrintStatements(true),
		rego.Trace(true),
	}

	ctx := context.Background()
	preparedQuery, err := rego.New(opts...).PrepareForEval(ctx)
	if err != nil {
		return rego.PreparedEvalQuery{}, fmt.Errorf(
			"failed to PrepareForEval: %w", err,
		)
	}

	return preparedQuery, nil
}

// evaluateRegoPolicy evaluates the pre-compiled Rego policy engine against the pod,
// using the JSON policy data from the annotation as input.policy.
// The policyBytes must be valid JSON matching the container policy schema.
func (c *PolicyVerificationController) evaluateRegoPolicy(policyBytes []byte, pod map[string]interface{}) error {
	ctx := context.Background()

	// Parse and validate the policy against the expected schema.
	var policyEntries []ContainerPolicyEntry
	if err := json.Unmarshal(policyBytes, &policyEntries); err != nil {
		return fmt.Errorf("invalid policy: %w", err)
	}

	if len(policyEntries) == 0 {
		return fmt.Errorf("invalid policy: policy array must not be empty")
	}

	for _, entry := range policyEntries {
		if entry.Name == "" {
			return fmt.Errorf("invalid policy: each entry must have a non-empty \"name\"")
		}
		if entry.Properties.Image == "" &&
			len(entry.Properties.Command) == 0 &&
			len(entry.Properties.Args) == 0 &&
			len(entry.Properties.EnvironmentVariables) == 0 &&
			len(entry.Properties.VolumeMounts) == 0 {
			return fmt.Errorf(
				"invalid policy: entry %q must have non-empty \"properties\"", entry.Name)
		}
	}

	// Deserialize again into an untyped structure for Rego evaluation.
	var policyData interface{}
	if err := json.Unmarshal(policyBytes, &policyData); err != nil {
		return fmt.Errorf("invalid policy JSON: %w", err)
	}

	// Build the combined input: pod fields + policy data.
	input := map[string]interface{}{
		"policy": policyData,
	}
	if spec, ok := pod["spec"]; ok {
		input["spec"] = spec
	}
	if metadata, ok := pod["metadata"]; ok {
		input["metadata"] = metadata
	}

	// Evaluate the prepared query with the combined input.
	results, err := c.policyQuery.Eval(ctx, rego.EvalInput(input))
	if err != nil {
		return fmt.Errorf("failed to evaluate rego policy: %w", err)
	}

	if len(results) == 0 || len(results[0].Expressions) == 0 {
		return fmt.Errorf("policy evaluation returned no results")
	}

	resultMap, ok := results[0].Expressions[0].Value.(map[string]interface{})
	if !ok {
		return fmt.Errorf("unexpected policy evaluation result type")
	}

	// Check if allow evaluated to true.
	if allowed, ok := resultMap["allow"].(bool); ok && allowed {
		return nil
	}

	// Pod not allowed — collect denial reasons.
	var reasons []string
	if reasonSet, ok := resultMap["reasons"].([]interface{}); ok {
		for _, r := range reasonSet {
			if s, ok := r.(string); ok {
				reasons = append(reasons, s)
			}
		}
	}

	if len(reasons) == 0 {
		return fmt.Errorf("policy denied the pod")
	}

	return fmt.Errorf("%s", strings.Join(reasons, "; "))
}

// verifySignature verifies the signature against the hash
func (c *PolicyVerificationController) verifySignature(hash, signature []byte) error {
	switch key := c.publicKey.(type) {
	case *rsa.PublicKey:
		// Use RSA-PSS with SHA-256
		pssOpts := &rsa.PSSOptions{
			SaltLength: rsa.PSSSaltLengthEqualsHash,
			Hash:       crypto.SHA256,
		}
		return rsa.VerifyPSS(key, crypto.SHA256, hash, signature, pssOpts)

	case *ecdsa.PublicKey:
		if !ecdsa.VerifyASN1(key, hash, signature) {
			return fmt.Errorf("ECDSA signature verification failed")
		}
		return nil

	default:
		return fmt.Errorf("unsupported key type: %T", c.publicKey)
	}
}

// loadPublicKey loads a public key from a certificate or public key PEM file
func loadPublicKey(certPath string) (crypto.PublicKey, error) {
	data, err := os.ReadFile(certPath)
	if err != nil {
		return nil, fmt.Errorf("failed to read file: %w", err)
	}

	block, _ := pem.Decode(data)
	if block == nil {
		return nil, fmt.Errorf("failed to parse PEM block")
	}

	switch block.Type {
	case "CERTIFICATE":
		cert, err := x509.ParseCertificate(block.Bytes)
		if err != nil {
			return nil, fmt.Errorf("failed to parse certificate: %w", err)
		}
		return cert.PublicKey, nil

	case "PUBLIC KEY":
		key, err := x509.ParsePKIXPublicKey(block.Bytes)
		if err != nil {
			return nil, fmt.Errorf("failed to parse public key: %w", err)
		}
		return key, nil

	case "RSA PUBLIC KEY":
		key, err := x509.ParsePKCS1PublicKey(block.Bytes)
		if err != nil {
			return nil, fmt.Errorf("failed to parse RSA public key: %w", err)
		}
		return key, nil

	default:
		return nil, fmt.Errorf("unsupported PEM type: %s", block.Type)
	}
}
