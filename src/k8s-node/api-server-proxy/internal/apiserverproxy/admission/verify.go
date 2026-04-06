package admission

import (
	"crypto"
	"crypto/ecdsa"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/json"
	"encoding/pem"
	"fmt"
	"log"
	"os"
	"regexp"
	"strings"
)

const (
	// PolicyAnnotation is the annotation key for the signed policy (base64-encoded JSON)
	PolicyAnnotation = "api-server-proxy.io/policy"

	// SignatureAnnotation is the annotation key for the policy signature
	SignatureAnnotation = "api-server-proxy.io/signature"
)

// ContainerPolicy defines the policy for a single container
type ContainerPolicy struct {
	// Name is the container name
	Name string `json:"name"`

	// Properties contains the container policy properties
	Properties ContainerProperties `json:"properties"`
}

// ContainerProperties defines the properties within a container policy
type ContainerProperties struct {
	// Image is the allowed container image (must match exactly)
	Image string `json:"image,omitempty"`

	// Command is the entrypoint array (overrides container ENTRYPOINT)
	Command []string `json:"command,omitempty"`

	// EnvironmentVariables lists environment variables for the container
	EnvironmentVariables []EnvVar `json:"environmentVariables,omitempty"`

	// VolumeMounts lists volume mounts for the container
	VolumeMounts []VolumeMount `json:"volumeMounts,omitempty"`

	// Privileged indicates whether the container can run as privileged
	Privileged bool `json:"privileged,omitempty"`

	// Capabilities lists allowed Linux capabilities to add
	Capabilities []string `json:"capabilities,omitempty"`
}

// EnvVar represents an environment variable with optional regex matching
type EnvVar struct {
	Name  string `json:"name"`
	Value string `json:"value,omitempty"`
	Regex bool   `json:"regex,omitempty"`
}

// VolumeMount represents a volume mount
type VolumeMount struct {
	Name      string `json:"name"`
	MountPath string `json:"mountPath"`
	MountType string `json:"mountType,omitempty"`
	ReadOnly  bool   `json:"readOnly,omitempty"`
}

// Policy is an array of container policies
// The policy is signed and verified at admission time
type Policy []ContainerPolicy

// PolicyVerificationController verifies pod policy signatures
type PolicyVerificationController struct {
	publicKey crypto.PublicKey
	certPath  string
	logger    *log.Logger
}

// NewPolicyVerificationController creates a new pod policy verification controller
func NewPolicyVerificationController(certPath string) (*PolicyVerificationController, error) {
	logger := log.New(os.Stdout, "[policy-verification] ", log.LstdFlags|log.Lmicroseconds)

	publicKey, err := loadPublicKey(certPath)
	if err != nil {
		return nil, fmt.Errorf("failed to load public key from %s: %w", certPath, err)
	}

	logger.Printf("Loaded public key from %s", certPath)

	return &PolicyVerificationController{
		publicKey: publicKey,
		certPath:  certPath,
		logger:    logger,
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

	// Check for special "allowall" policy - bypasses all validation
	// Policy must be exactly: ["allowall"]
	var allowAllCheck []string
	if err := json.Unmarshal(policyBytes, &allowAllCheck); err == nil {
		if len(allowAllCheck) == 1 && allowAllCheck[0] == "allowall" {
			// Verify the signature on the allowall policy first
			// Hash the decoded policy bytes (not the base64 string)
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

			c.logger.Printf("Pod %s/%s has signed allowall policy, bypassing validation", req.Namespace, req.Name)
			return Allow("pod has signed allowall policy")
		}
	}

	var policy Policy
	if err := json.Unmarshal(policyBytes, &policy); err != nil {
		c.logger.Printf("Invalid policy JSON for %s/%s: %v", req.Namespace, req.Name, err)
		return Deny(fmt.Sprintf("invalid policy JSON: %v", err))
	}

	// Verify the signature on the decoded policy bytes
	// Hash the decoded policy bytes (not the base64 string)
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

	// Check if the pod matches the policy
	if err := c.checkPodAgainstPolicy(req.Pod, policy); err != nil {
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

// checkPodAgainstPolicy verifies the pod spec matches the signed policy
func (c *PolicyVerificationController) checkPodAgainstPolicy(pod map[string]interface{}, policy Policy) error {
	spec, ok := pod["spec"].(map[string]interface{})
	if !ok {
		return fmt.Errorf("pod has no spec")
	}

	// Build a map of container policies by name for easy lookup
	containerPolicies := make(map[string]*ContainerProperties)
	for i := range policy {
		containerPolicies[policy[i].Name] = &policy[i].Properties
	}

	// Check containers against policy
	if err := c.checkContainers(spec, "containers", containerPolicies); err != nil {
		return err
	}

	// Check init containers against policy (they should also be in the policy array)
	if err := c.checkContainers(spec, "initContainers", containerPolicies); err != nil {
		return err
	}

	return nil
}

// checkContainers verifies all containers match their policies by name
func (c *PolicyVerificationController) checkContainers(spec map[string]interface{}, containerType string, containerPolicies map[string]*ContainerProperties) error {
	containers, ok := spec[containerType].([]interface{})
	if !ok {
		// No containers of this type in spec - that's OK
		return nil
	}

	// Check each container in the spec against its policy
	for _, container := range containers {
		containerMap, ok := container.(map[string]interface{})
		if !ok {
			continue
		}

		name, _ := containerMap["name"].(string)
		if name == "" {
			return fmt.Errorf("%s has container without name", containerType)
		}

		policy, exists := containerPolicies[name]
		if !exists {
			return fmt.Errorf("%s '%s' not found in policy", containerType, name)
		}

		// Check image (exact match required)
		image, _ := containerMap["image"].(string)
		if image != policy.Image {
			return fmt.Errorf("%s '%s': image '%s' does not match policy image '%s'", containerType, name, image, policy.Image)
		}

		// Check command
		if err := c.checkCommand(containerMap, name, containerType, policy); err != nil {
			return err
		}

		// Check environment variables
		if err := c.checkEnvVars(containerMap, name, containerType, policy); err != nil {
			return err
		}

		// Check volume mounts
		if err := c.checkVolumeMounts(containerMap, name, containerType, policy); err != nil {
			return err
		}

		// Check security context
		if err := c.checkSecurityContext(containerMap, name, containerType, policy); err != nil {
			return err
		}
	}

	return nil
}

// checkCommand verifies the container's command matches the policy
func (c *PolicyVerificationController) checkCommand(container map[string]interface{}, name, containerType string, policy *ContainerProperties) error {
	var podCommand []string
	if cmd, ok := container["command"].([]interface{}); ok {
		for _, c := range cmd {
			if s, ok := c.(string); ok {
				podCommand = append(podCommand, s)
			}
		}
	}

	// If policy command is nil or empty, any command is allowed
	if len(policy.Command) == 0 {
		return nil
	}

	if !c.stringSlicesEqual(podCommand, policy.Command) {
		return fmt.Errorf("%s '%s': command %v does not match policy command %v", containerType, name, podCommand, policy.Command)
	}
	return nil
}

// checkEnvVars verifies the container's environment variables match the policy
func (c *PolicyVerificationController) checkEnvVars(container map[string]interface{}, name, containerType string, policy *ContainerProperties) error {
	// Build map of pod env vars (only direct values, not valueFrom)
	podEnvMap := make(map[string]string)
	if env, ok := container["env"].([]interface{}); ok {
		for _, e := range env {
			if envMap, ok := e.(map[string]interface{}); ok {
				envName, _ := envMap["name"].(string)
				envValue, _ := envMap["value"].(string)
				// Only include env vars with direct values (not valueFrom)
				if envName != "" && envMap["valueFrom"] == nil {
					podEnvMap[envName] = envValue
				}
			}
		}
	}

	// If policy has no env vars specified, any env is allowed
	if len(policy.EnvironmentVariables) == 0 {
		return nil
	}

	// Check each policy env var against the pod
	for _, policyEnv := range policy.EnvironmentVariables {
		podValue, exists := podEnvMap[policyEnv.Name]
		if !exists {
			return fmt.Errorf("%s '%s': env var '%s' required by policy but not found in pod", containerType, name, policyEnv.Name)
		}

		// Check value - supports regex matching if enabled
		if policyEnv.Regex {
			matched, err := regexp.MatchString("^"+policyEnv.Value+"$", podValue)
			if err != nil || !matched {
				return fmt.Errorf("%s '%s': env var '%s' value '%s' does not match policy regex '%s'", containerType, name, policyEnv.Name, podValue, policyEnv.Value)
			}
		} else {
			// Exact match required
			if podValue != policyEnv.Value {
				return fmt.Errorf("%s '%s': env var '%s' value '%s' does not match policy value '%s'", containerType, name, policyEnv.Name, podValue, policyEnv.Value)
			}
		}
	}

	return nil
}

// checkVolumeMounts verifies the container's volume mounts match the policy
func (c *PolicyVerificationController) checkVolumeMounts(container map[string]interface{}, name, containerType string, policy *ContainerProperties) error {
	// Build map of pod volume mounts
	podMountsMap := make(map[string]VolumeMount)
	if mounts, ok := container["volumeMounts"].([]interface{}); ok {
		for _, m := range mounts {
			if mountMap, ok := m.(map[string]interface{}); ok {
				mount := VolumeMount{}
				if n, ok := mountMap["name"].(string); ok {
					mount.Name = n
				}
				if p, ok := mountMap["mountPath"].(string); ok {
					mount.MountPath = p
				}
				if r, ok := mountMap["readOnly"].(bool); ok {
					mount.ReadOnly = r
				}
				// Skip Kubernetes auto-injected service account token mounts
				if strings.HasPrefix(mount.Name, "kube-api-access-") ||
					mount.MountPath == "/var/run/secrets/kubernetes.io/serviceaccount" {
					continue
				}
				podMountsMap[mount.Name] = mount
			}
		}
	}

	// If policy has no volume mounts specified, any mounts are allowed
	if len(policy.VolumeMounts) == 0 {
		return nil
	}

	// Check each policy volume mount against the pod
	for _, policyMount := range policy.VolumeMounts {
		podMount, exists := podMountsMap[policyMount.Name]
		if !exists {
			return fmt.Errorf("%s '%s': volume mount '%s' required by policy but not found in pod", containerType, name, policyMount.Name)
		}

		if podMount.MountPath != policyMount.MountPath {
			return fmt.Errorf("%s '%s': volume mount '%s' mountPath '%s' does not match policy '%s'", containerType, name, policyMount.Name, podMount.MountPath, policyMount.MountPath)
		}

		if podMount.ReadOnly != policyMount.ReadOnly {
			return fmt.Errorf("%s '%s': volume mount '%s' readOnly=%v does not match policy readOnly=%v", containerType, name, policyMount.Name, podMount.ReadOnly, policyMount.ReadOnly)
		}
	}

	return nil
}

// checkSecurityContext checks a container's security context against its policy
func (c *PolicyVerificationController) checkSecurityContext(container map[string]interface{}, name, containerType string, policy *ContainerProperties) error {
	securityContext, _ := container["securityContext"].(map[string]interface{})

	// Check privileged
	privileged := false
	if securityContext != nil {
		privileged, _ = securityContext["privileged"].(bool)
	}
	if privileged != policy.Privileged {
		return fmt.Errorf("%s '%s': privileged=%v does not match policy privileged=%v", containerType, name, privileged, policy.Privileged)
	}

	// Check capabilities
	var podCaps []string
	if securityContext != nil {
		if capabilities, ok := securityContext["capabilities"].(map[string]interface{}); ok {
			if add, ok := capabilities["add"].([]interface{}); ok {
				for _, cap := range add {
					if capStr, ok := cap.(string); ok {
						podCaps = append(podCaps, capStr)
					}
				}
			}
		}
	}

	// Capabilities must match exactly (same set)
	if !c.capabilitySetsEqual(podCaps, policy.Capabilities) {
		return fmt.Errorf("%s '%s': capabilities %v do not match policy capabilities %v", containerType, name, podCaps, policy.Capabilities)
	}

	return nil
}

// stringSlicesEqual checks if two string slices are equal (order matters)
func (c *PolicyVerificationController) stringSlicesEqual(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

// capabilitySetsEqual checks if two capability slices contain the same elements
func (c *PolicyVerificationController) capabilitySetsEqual(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}

	// Create maps for comparison (case-insensitive)
	aMap := make(map[string]bool)
	for _, cap := range a {
		aMap[strings.ToUpper(cap)] = true
	}

	for _, cap := range b {
		if !aMap[strings.ToUpper(cap)] {
			return false
		}
	}

	return true
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
