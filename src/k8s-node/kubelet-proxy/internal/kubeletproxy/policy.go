package kubeletproxy

import (
	"context"
	_ "embed"
	"encoding/json"
	"fmt"
	"log"
	"os"

	"github.com/open-policy-agent/opa/v1/rego"
	"github.com/open-policy-agent/opa/v1/storage"
	"github.com/open-policy-agent/opa/v1/storage/inmem"
)

//go:embed policy.rego
var policyModule string

// PolicyEngine evaluates kubelet API requests against a Rego policy
// with allowed API data loaded from a JSON file.
type PolicyEngine struct {
	query  rego.PreparedEvalQuery
	logger *log.Logger
}

// apiPolicyData is the expected JSON structure of the policy file.
type apiPolicyData struct {
	AllowedAPIs []string `json:"allowed_apis"`
}

// NewPolicyEngine creates a PolicyEngine from the given policy file.
// The policyFilePath is required and must point to a valid JSON file
// with an allowed_apis array.
func NewPolicyEngine(
	ctx context.Context,
	policyFilePath string,
	logger *log.Logger,
) (*PolicyEngine, error) {
	if policyFilePath == "" {
		return nil, fmt.Errorf("--api-policy is required: no policy file specified")
	}

	fileBytes, err := os.ReadFile(policyFilePath)
	if err != nil {
		return nil, fmt.Errorf("failed to read API policy file %q: %w", policyFilePath, err)
	}

	var policyData apiPolicyData
	if err = json.Unmarshal(fileBytes, &policyData); err != nil {
		return nil, fmt.Errorf("failed to parse API policy file %q: %w", policyFilePath, err)
	}

	if len(policyData.AllowedAPIs) == 0 {
		return nil, fmt.Errorf("API policy file %q has no allowed_apis entries", policyFilePath)
	}

	// Convert to interface{} for OPA store.
	apis := make([]interface{}, len(policyData.AllowedAPIs))
	for i, api := range policyData.AllowedAPIs {
		apis[i] = api
	}
	data := map[string]interface{}{"allowed_apis": apis}

	logger.Printf("Loaded %d allowed APIs from %s", len(policyData.AllowedAPIs), policyFilePath)

	store := inmem.NewFromObject(data)
	txn, err := store.NewTransaction(ctx, storage.WriteParams)
	if err != nil {
		return nil, fmt.Errorf("failed to create OPA store transaction: %w", err)
	}

	preparedQuery, err := rego.New(
		rego.Query("data.kubeletproxy.policy.allowed"),
		rego.Module("policy.rego", policyModule),
		rego.Store(store),
		rego.Transaction(txn),
	).PrepareForEval(ctx)
	if err != nil {
		return nil, fmt.Errorf("failed to prepare Rego policy: %w", err)
	}

	if err := store.Commit(ctx, txn); err != nil {
		return nil, fmt.Errorf("failed to commit OPA store: %w", err)
	}

	return &PolicyEngine{
		query:  preparedQuery,
		logger: logger,
	}, nil
}

// Evaluate checks whether the given request URI is allowed by the policy.
func (p *PolicyEngine) Evaluate(ctx context.Context, requestURI string) (bool, error) {
	input := map[string]interface{}{
		"uri": requestURI,
	}

	resultSet, err := p.query.Eval(ctx, rego.EvalInput(input))
	if err != nil {
		return false, fmt.Errorf("failed to evaluate policy: %w", err)
	}

	if len(resultSet) == 0 {
		return false, nil
	}

	allowed, ok := resultSet[0].Expressions[0].Value.(bool)
	if !ok {
		return false, fmt.Errorf("unexpected policy result type: %T", resultSet[0].Expressions[0].Value)
	}

	return allowed, nil
}
