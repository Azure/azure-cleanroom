// Package client provides HTTP clients for cleanroom provider APIs.
package client

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"time"

	"go.opentelemetry.io/contrib/instrumentation/net/http/otelhttp"
)

// ErrDocumentNotFound is returned when a user document
// does not exist in CGS.
var ErrDocumentNotFound = errors.New(
	"user document not found",
)

// CgsClient wraps the CGS (Clean Room Governance Service) client
// REST API used to activate members and manage governance state.
type CgsClient struct {
	httpClient *http.Client
}

// NewCgsClient creates a new CGS client.
func NewCgsClient() *CgsClient {
	return &CgsClient{
		httpClient: &http.Client{
			Timeout: 2 * time.Minute,
			Transport: otelhttp.NewTransport(
				http.DefaultTransport,
			),
		},
	}
}

// WaitForReady polls GET /ready on the CGS client until it
// returns 200 or the context is cancelled.
func (c *CgsClient) WaitForReady(
	ctx context.Context,
	endpoint string,
) error {
	url := fmt.Sprintf("%s/ready", endpoint)
	ticker := time.NewTicker(5 * time.Second)
	defer ticker.Stop()

	for {
		select {
		case <-ctx.Done():
			return fmt.Errorf(
				"waiting for CGS client ready: %w",
				ctx.Err(),
			)
		case <-ticker.C:
			req, err := http.NewRequestWithContext(
				ctx, http.MethodGet, url, nil,
			)
			if err != nil {
				continue
			}
			resp, err := c.httpClient.Do(req)
			if err != nil {
				continue
			}
			resp.Body.Close()
			if resp.StatusCode == http.StatusOK {
				return nil
			}
		}
	}
}

// ContractResponse is the response from contract operations.
type ContractResponse struct {
	ID         string `json:"id"`
	State      string `json:"state"`
	Version    string `json:"version"`
	Data       string `json:"data"`
	ProposalID string `json:"proposalId"`
}

// ProposalResponse is the response from proposal operations.
type ProposalResponse struct {
	ProposalID    string `json:"proposalId"`
	ProposalState string `json:"proposalState"`
}

// CreateContract calls PUT /contracts/{contractId}.
// The CGS API returns 200 with no response body on success.
func (c *CgsClient) CreateContract(
	ctx context.Context,
	endpoint string,
	contractId string,
	data string,
) error {
	body, err := json.Marshal(map[string]string{
		"version": "",
		"data":    data,
	})
	if err != nil {
		return fmt.Errorf(
			"marshaling contract data: %w", err,
		)
	}

	url := fmt.Sprintf(
		"%s/contracts/%s", endpoint, contractId,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPut, url, bytes.NewReader(body),
	)
	if err != nil {
		return fmt.Errorf(
			"creating contract request: %w", err,
		)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return fmt.Errorf(
			"calling create contract API: %w", err,
		)
	}
	defer resp.Body.Close()

	if resp.StatusCode == http.StatusConflict {
		// 409 means the contract already exists;
		// treat as success for idempotency.
		return nil
	}

	if resp.StatusCode != http.StatusOK {
		respBody, _ := io.ReadAll(resp.Body)
		return fmt.Errorf(
			"create contract returned %d: %s",
			resp.StatusCode, string(respBody),
		)
	}

	return nil
}

// GetContract calls GET /contracts/{contractId}.
func (c *CgsClient) GetContract(
	ctx context.Context,
	endpoint string,
	contractId string,
) (*ContractResponse, error) {
	url := fmt.Sprintf(
		"%s/contracts/%s", endpoint, contractId,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodGet, url, nil,
	)
	if err != nil {
		return nil, fmt.Errorf(
			"creating get contract request: %w", err,
		)
	}

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf(
			"calling get contract API: %w", err,
		)
	}
	defer resp.Body.Close()

	respBody, _ := io.ReadAll(resp.Body)
	if resp.StatusCode == http.StatusNotFound {
		return nil, nil
	}
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf(
			"get contract returned %d: %s",
			resp.StatusCode, string(respBody),
		)
	}

	var result ContractResponse
	if err := json.Unmarshal(respBody, &result); err != nil {
		return nil, fmt.Errorf(
			"parsing contract response: %w", err,
		)
	}
	return &result, nil
}

// ProposeContract calls POST /contracts/{contractId}/propose.
func (c *CgsClient) ProposeContract(
	ctx context.Context,
	endpoint string,
	contractId string,
	version string,
) (*ProposalResponse, error) {
	body, err := json.Marshal(map[string]string{
		"version": version,
	})
	if err != nil {
		return nil, fmt.Errorf(
			"marshaling propose input: %w", err,
		)
	}

	url := fmt.Sprintf(
		"%s/contracts/%s/propose", endpoint, contractId,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url, bytes.NewReader(body),
	)
	if err != nil {
		return nil, fmt.Errorf(
			"creating propose request: %w", err,
		)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf(
			"calling propose contract API: %w", err,
		)
	}
	defer resp.Body.Close()

	respBody, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf(
			"propose contract returned %d: %s",
			resp.StatusCode, string(respBody),
		)
	}

	var result ProposalResponse
	if err := json.Unmarshal(respBody, &result); err != nil {
		return nil, fmt.Errorf(
			"parsing propose response: %w", err,
		)
	}
	return &result, nil
}

// VoteAcceptContract calls
// POST /contracts/{contractId}/vote_accept.
func (c *CgsClient) VoteAcceptContract(
	ctx context.Context,
	endpoint string,
	contractId string,
	proposalId string,
) error {
	body, err := json.Marshal(map[string]string{
		"proposalId": proposalId,
	})
	if err != nil {
		return fmt.Errorf(
			"marshaling vote input: %w", err,
		)
	}

	url := fmt.Sprintf(
		"%s/contracts/%s/vote_accept",
		endpoint, contractId,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url, bytes.NewReader(body),
	)
	if err != nil {
		return fmt.Errorf(
			"creating vote request: %w", err,
		)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return fmt.Errorf(
			"calling vote accept API: %w", err,
		)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		respBody, _ := io.ReadAll(resp.Body)
		return fmt.Errorf(
			"vote accept returned %d: %s",
			resp.StatusCode, string(respBody),
		)
	}
	return nil
}

// ProposeDeploymentSpec calls
// POST /contracts/{contractId}/deploymentspec/propose.
func (c *CgsClient) ProposeDeploymentSpec(
	ctx context.Context,
	endpoint string,
	contractId string,
	spec []byte,
) (*ProposalResponse, error) {
	url := fmt.Sprintf(
		"%s/contracts/%s/deploymentspec/propose",
		endpoint, contractId,
	)
	return c.postForProposal(ctx, url, spec)
}

// ProposeCleanRoomPolicy calls
// POST /contracts/{contractId}/cleanroompolicy/propose.
func (c *CgsClient) ProposeCleanRoomPolicy(
	ctx context.Context,
	endpoint string,
	contractId string,
	policy []byte,
) (*ProposalResponse, error) {
	url := fmt.Sprintf(
		"%s/contracts/%s/cleanroompolicy/propose",
		endpoint, contractId,
	)
	return c.postForProposal(ctx, url, policy)
}

// ProposeEnableLogging calls
// POST /contracts/{contractId}/logging/propose-enable.
func (c *CgsClient) ProposeEnableLogging(
	ctx context.Context,
	endpoint string,
	contractId string,
) (*ProposalResponse, error) {
	url := fmt.Sprintf(
		"%s/contracts/%s/logging/propose-enable",
		endpoint, contractId,
	)
	return c.postForProposal(ctx, url, nil)
}

// ProposeEnableTelemetry calls
// POST /contracts/{contractId}/telemetry/propose-enable.
func (c *CgsClient) ProposeEnableTelemetry(
	ctx context.Context,
	endpoint string,
	contractId string,
) (*ProposalResponse, error) {
	url := fmt.Sprintf(
		"%s/contracts/%s/telemetry/propose-enable",
		endpoint, contractId,
	)
	return c.postForProposal(ctx, url, nil)
}

// ProposeEnableCA calls POST /proposals/create with the
// enable_ca action.
func (c *CgsClient) ProposeEnableCA(
	ctx context.Context,
	endpoint string,
	contractId string,
) (*ProposalResponse, error) {
	payload := map[string]interface{}{
		"actions": []map[string]interface{}{
			{
				"name": "enable_ca",
				"args": map[string]string{
					"contractId": contractId,
				},
			},
		},
	}
	body, err := json.Marshal(payload)
	if err != nil {
		return nil, fmt.Errorf(
			"marshaling enable CA input: %w", err,
		)
	}

	url := fmt.Sprintf(
		"%s/proposals/create", endpoint,
	)
	return c.postForProposal(ctx, url, body)
}

// VoteAcceptProposal calls
// POST /proposals/{proposalId}/ballots/vote_accept.
func (c *CgsClient) VoteAcceptProposal(
	ctx context.Context,
	endpoint string,
	proposalId string,
) error {
	url := fmt.Sprintf(
		"%s/proposals/%s/ballots/vote_accept",
		endpoint, proposalId,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url, nil,
	)
	if err != nil {
		return fmt.Errorf(
			"creating vote proposal request: %w", err,
		)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return fmt.Errorf(
			"calling vote accept proposal API: %w", err,
		)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		respBody, _ := io.ReadAll(resp.Body)
		return fmt.Errorf(
			"vote accept proposal returned %d: %s",
			resp.StatusCode, string(respBody),
		)
	}
	return nil
}

// GenerateCASigningKey calls
// POST /contracts/{contractId}/ca/generateSigningKey.
func (c *CgsClient) GenerateCASigningKey(
	ctx context.Context,
	endpoint string,
	contractId string,
) error {
	url := fmt.Sprintf(
		"%s/contracts/%s/ca/generateSigningKey",
		endpoint, contractId,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url, nil,
	)
	if err != nil {
		return fmt.Errorf(
			"creating CA key gen request: %w", err,
		)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return fmt.Errorf(
			"calling CA key gen API: %w", err,
		)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		respBody, _ := io.ReadAll(resp.Body)
		return fmt.Errorf(
			"CA key gen returned %d: %s",
			resp.StatusCode, string(respBody),
		)
	}
	return nil
}

// CAInfoResponse holds the response from the CA info API.
type CAInfoResponse struct {
	Enabled   bool   `json:"enabled"`
	CaCert    string `json:"caCert,omitempty"`
	PublicKey string `json:"publicKey,omitempty"`
}

// GetCAInfo calls GET /contracts/{contractId}/ca/info.
func (c *CgsClient) GetCAInfo(
	ctx context.Context,
	endpoint string,
	contractId string,
) (*CAInfoResponse, error) {
	url := fmt.Sprintf(
		"%s/contracts/%s/ca/info",
		endpoint, contractId,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodGet, url, nil,
	)
	if err != nil {
		return nil, fmt.Errorf(
			"creating CA info request: %w", err,
		)
	}

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf(
			"calling CA info API: %w", err,
		)
	}
	defer resp.Body.Close()

	respBody, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf(
			"CA info returned %d: %s",
			resp.StatusCode, string(respBody),
		)
	}

	var result CAInfoResponse
	if err := json.Unmarshal(
		respBody, &result,
	); err != nil {
		return nil, fmt.Errorf(
			"parsing CA info response: %w", err,
		)
	}
	return &result, nil
}

// postForProposal is a helper that POSTs to a URL and
// parses a ProposalResponse.
func (c *CgsClient) postForProposal(
	ctx context.Context,
	url string,
	body []byte,
) (*ProposalResponse, error) {
	var reader io.Reader
	if body != nil {
		reader = bytes.NewReader(body)
	}

	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url, reader,
	)
	if err != nil {
		return nil, fmt.Errorf(
			"creating proposal request: %w", err,
		)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf(
			"calling proposal API %s: %w", url, err,
		)
	}
	defer resp.Body.Close()

	respBody, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf(
			"proposal API %s returned %d: %s",
			url, resp.StatusCode, string(respBody),
		)
	}

	var result ProposalResponse
	if err := json.Unmarshal(
		respBody, &result,
	); err != nil {
		return nil, fmt.Errorf(
			"parsing proposal response: %w", err,
		)
	}
	return &result, nil
}

// ActivateMember calls POST /members/statedigests/ack to
// activate the member in the CCF consortium.
func (c *CgsClient) ActivateMember(
	ctx context.Context,
	endpoint string,
) error {
	url := fmt.Sprintf(
		"%s/members/statedigests/ack", endpoint,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url, nil,
	)
	if err != nil {
		return fmt.Errorf(
			"creating activate request: %w", err,
		)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return fmt.Errorf("calling activate API: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK &&
		resp.StatusCode != http.StatusNoContent {
		respBody, _ := io.ReadAll(resp.Body)
		return fmt.Errorf(
			"activate returned %d: %s",
			resp.StatusCode, string(respBody),
		)
	}
	return nil
}

// MemberInfo represents a CCF consortium member.
type MemberInfo struct {
	MemberID            string `json:"memberId"`
	Status              string `json:"status"`
	PublicEncryptionKey string `json:"publicEncryptionKey"`
}

// MembersResponse is the response from GET /members.
type MembersResponse struct {
	Value []MemberInfo `json:"value"`
}

// GetMembers calls GET /members on the CGS client and
// returns all consortium members.
func (c *CgsClient) GetMembers(
	ctx context.Context,
	endpoint string,
) (*MembersResponse, error) {
	url := fmt.Sprintf("%s/members", endpoint)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodGet, url, nil,
	)
	if err != nil {
		return nil, fmt.Errorf(
			"creating members request: %w", err,
		)
	}

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf(
			"calling members API: %w", err,
		)
	}
	defer resp.Body.Close()

	respBody, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf(
			"members returned %d: %s",
			resp.StatusCode, string(respBody),
		)
	}

	var result MembersResponse
	if err := json.Unmarshal(
		respBody, &result,
	); err != nil {
		return nil, fmt.Errorf(
			"parsing members response: %w", err,
		)
	}
	return &result, nil
}

// ProposeAndAccept submits a proposal via
// POST /proposals/create and then votes to accept it via
// POST /proposals/{id}/ballots/vote_accept.
func (c *CgsClient) ProposeAndAccept(
	ctx context.Context,
	endpoint string,
	proposal interface{},
) (*ProposalResponse, error) {
	body, err := json.Marshal(proposal)
	if err != nil {
		return nil, fmt.Errorf(
			"marshaling proposal: %w", err,
		)
	}

	url := fmt.Sprintf("%s/proposals/create", endpoint)
	resp, err := c.postForProposal(ctx, url, body)
	if err != nil {
		return nil, fmt.Errorf(
			"submitting proposal: %w", err,
		)
	}

	if resp.ProposalState == "Accepted" {
		return resp, nil
	}

	if err := c.VoteAcceptProposal(
		ctx, endpoint, resp.ProposalID,
	); err != nil {
		return nil, fmt.Errorf(
			"voting to accept proposal %s: %w",
			resp.ProposalID, err,
		)
	}

	resp.ProposalState = "Accepted"
	return resp, nil
}

// SetCaCertBundle submits a set_ca_cert_bundle proposal.
func (c *CgsClient) SetCaCertBundle(
	ctx context.Context,
	endpoint string,
	name string,
	certs string,
) (*ProposalResponse, error) {
	proposal := map[string]interface{}{
		"actions": []map[string]interface{}{
			{
				"name": "set_ca_cert_bundle",
				"args": map[string]interface{}{
					"name":        name,
					"cert_bundle": certs,
				},
			},
		},
	}
	return c.ProposeAndAccept(ctx, endpoint, proposal)
}

// SetJwtIssuer submits a set_jwt_issuer proposal.
func (c *CgsClient) SetJwtIssuer(
	ctx context.Context,
	endpoint string,
	issuerURL string,
	caCertBundleName string,
	autoRefresh bool,
) (*ProposalResponse, error) {
	proposal := map[string]interface{}{
		"actions": []map[string]interface{}{
			{
				"name": "set_jwt_issuer",
				"args": map[string]interface{}{
					"issuer":              issuerURL,
					"ca_cert_bundle_name": caCertBundleName,
					"auto_refresh":        autoRefresh,
				},
			},
		},
	}
	return c.ProposeAndAccept(ctx, endpoint, proposal)
}

// SetConstitution submits a set_constitution proposal.
func (c *CgsClient) SetConstitution(
	ctx context.Context,
	endpoint string,
	constitution string,
) (*ProposalResponse, error) {
	proposal := map[string]interface{}{
		"actions": []map[string]interface{}{
			{
				"name": "set_constitution",
				"args": map[string]interface{}{
					"constitution": constitution,
				},
			},
		},
	}
	return c.ProposeAndAccept(ctx, endpoint, proposal)
}

// SetJsRuntimeOptions submits a set_js_runtime_options
// proposal with default settings.
func (c *CgsClient) SetJsRuntimeOptions(
	ctx context.Context,
	endpoint string,
) (*ProposalResponse, error) {
	proposal := map[string]interface{}{
		"actions": []map[string]interface{}{
			{
				"name": "set_js_runtime_options",
				"args": map[string]interface{}{
					"max_heap_bytes":           100 * 1024 * 1024,
					"max_stack_bytes":          1024 * 1024,
					"max_execution_time_ms":    10000,
					"log_exception_details":    true,
					"return_exception_details": true,
				},
			},
		},
	}
	return c.ProposeAndAccept(ctx, endpoint, proposal)
}

// JsAppBundle represents a CCF JS application bundle.
type JsAppBundle struct {
	Metadata map[string]interface{} `json:"metadata"`
	Modules  []JsAppModule          `json:"modules"`
}

// JsAppModule represents a module within a JS app bundle.
type JsAppModule struct {
	Name   string `json:"name"`
	Module string `json:"module"`
}

// SetJsApp submits a set_js_app proposal with the given
// application bundle.
func (c *CgsClient) SetJsApp(
	ctx context.Context,
	endpoint string,
	bundle *JsAppBundle,
) (*ProposalResponse, error) {
	proposal := map[string]interface{}{
		"actions": []map[string]interface{}{
			{
				"name": "set_js_app",
				"args": map[string]interface{}{
					"bundle": bundle,
				},
			},
		},
	}
	return c.ProposeAndAccept(ctx, endpoint, proposal)
}

// EnableOidcIssuer submits an enable_oidc_issuer proposal
// and votes to accept it.
func (c *CgsClient) EnableOidcIssuer(
	ctx context.Context,
	endpoint string,
) (*ProposalResponse, error) {
	kidBytes := make([]byte, 16)
	if _, err := rand.Read(kidBytes); err != nil {
		return nil, fmt.Errorf(
			"generating kid: %w", err,
		)
	}
	kid := hex.EncodeToString(kidBytes)

	proposal := map[string]interface{}{
		"actions": []map[string]interface{}{
			{
				"name": "enable_oidc_issuer",
				"args": map[string]interface{}{
					"kid": kid,
				},
			},
		},
	}
	return c.ProposeAndAccept(ctx, endpoint, proposal)
}

// GenerateOidcSigningKey calls
// POST /oidc/generateSigningKey on the CGS client.
func (c *CgsClient) GenerateOidcSigningKey(
	ctx context.Context,
	endpoint string,
) error {
	url := fmt.Sprintf(
		"%s/oidc/generateSigningKey", endpoint,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url, nil,
	)
	if err != nil {
		return fmt.Errorf(
			"creating OIDC key gen request: %w", err,
		)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return fmt.Errorf(
			"calling OIDC key gen API: %w", err,
		)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		respBody, _ := io.ReadAll(resp.Body)
		return fmt.Errorf(
			"OIDC key gen returned %d: %s",
			resp.StatusCode, string(respBody),
		)
	}
	return nil
}

// ProposeEnableSigning calls POST /proposals/create with
// the enable_signing action.
func (c *CgsClient) ProposeEnableSigning(
	ctx context.Context,
	endpoint string,
	contractId string,
) (*ProposalResponse, error) {
	kid := generateKID()
	payload := map[string]interface{}{
		"actions": []map[string]interface{}{
			{
				"name": "enable_signing",
				"args": map[string]string{
					"contractId": contractId,
					"kid":        kid,
				},
			},
		},
	}
	body, err := json.Marshal(payload)
	if err != nil {
		return nil, fmt.Errorf(
			"marshaling enable signing input: %w", err,
		)
	}

	url := fmt.Sprintf(
		"%s/proposals/create", endpoint,
	)
	return c.postForProposal(ctx, url, body)
}

// GenerateSigningKey calls
// POST /signing/generateSigningKey on the CGS client.
func (c *CgsClient) GenerateSigningKey(
	ctx context.Context,
	endpoint string,
) error {
	url := fmt.Sprintf(
		"%s/signing/generateSigningKey", endpoint,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url, nil,
	)
	if err != nil {
		return fmt.Errorf(
			"creating signing key gen request: %w", err,
		)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return fmt.Errorf(
			"calling signing key gen API: %w", err,
		)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		respBody, _ := io.ReadAll(resp.Body)
		return fmt.Errorf(
			"signing key gen returned %d: %s",
			resp.StatusCode, string(respBody),
		)
	}
	return nil
}

// SigningInfoResponse represents the response from
// POST /signing/info on the CGS client.
type SigningInfoResponse struct {
	Enabled      bool   `json:"enabled"`
	PublicKeyPem string `json:"publicKeyPem"`
}

// GetSigningInfo calls POST /signing/info to retrieve
// the signing key information.
func (c *CgsClient) GetSigningInfo(
	ctx context.Context,
	endpoint string,
) (*SigningInfoResponse, error) {
	url := fmt.Sprintf(
		"%s/signing/info", endpoint,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url, nil,
	)
	if err != nil {
		return nil, fmt.Errorf(
			"creating signing info request: %w", err,
		)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf(
			"calling signing info API: %w", err,
		)
	}
	defer resp.Body.Close()

	respBody, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf(
			"signing info returned %d: %s",
			resp.StatusCode, string(respBody),
		)
	}

	var result SigningInfoResponse
	if err := json.Unmarshal(
		respBody, &result,
	); err != nil {
		return nil, fmt.Errorf(
			"parsing signing info response: %w", err,
		)
	}
	return &result, nil
}

// generateKID produces a random key ID for signing keys.
func generateKID() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

// AddUserIdentity calls POST /users/identities/add to
// add a publisher user identity in CGS.
func (c *CgsClient) AddUserIdentity(
	ctx context.Context,
	endpoint string,
	identifier string,
	userId string,
	tenantId string,
) (*ProposalResponse, error) {
	payload := map[string]string{
		"objectId":    userId,
		"tenantId":    tenantId,
		"identifier":  identifier,
		"accountType": "microsoft",
	}
	body, err := json.Marshal(payload)
	if err != nil {
		return nil, fmt.Errorf(
			"marshaling user identity input: %w", err,
		)
	}

	url := fmt.Sprintf(
		"%s/users/identities/add", endpoint,
	)
	return c.postForProposal(ctx, url, body)
}

// CreateUserDocument calls PUT /userdocuments/{docId}
// to create a user document in CGS.
func (c *CgsClient) CreateUserDocument(
	ctx context.Context,
	endpoint string,
	docId string,
	contractId string,
	data string,
	labels map[string]string,
	approvers []map[string]string,
) error {
	payload := map[string]interface{}{
		"version":    "",
		"contractId": contractId,
		"data":       data,
		"approvers":  approvers,
	}
	if labels != nil {
		payload["labels"] = labels
	}
	body, err := json.Marshal(payload)
	if err != nil {
		return fmt.Errorf(
			"marshaling user document input: %w", err,
		)
	}

	url := fmt.Sprintf(
		"%s/userdocuments/%s", endpoint, docId,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPut, url,
		bytes.NewReader(body),
	)
	if err != nil {
		return fmt.Errorf(
			"creating user document request: %w", err,
		)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return fmt.Errorf(
			"calling user document API: %w", err,
		)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		respBody, _ := io.ReadAll(resp.Body)
		return fmt.Errorf(
			"user document API returned %d: %s",
			resp.StatusCode, string(respBody),
		)
	}
	return nil
}

// UserDocumentResponse is the response from
// GET /userdocuments/{docId}.
type UserDocumentResponse struct {
	Version    string `json:"version"`
	State      string `json:"state"`
	Data       string `json:"data"`
	ProposalID string `json:"proposalId"`
}

// GetUserDocument calls GET /userdocuments/{docId}.
func (c *CgsClient) GetUserDocument(
	ctx context.Context,
	endpoint string,
	docId string,
) (*UserDocumentResponse, error) {
	url := fmt.Sprintf(
		"%s/userdocuments/%s", endpoint, docId,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodGet, url, nil,
	)
	if err != nil {
		return nil, fmt.Errorf(
			"creating get user document request: %w",
			err,
		)
	}

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf(
			"calling get user document API: %w", err,
		)
	}
	defer resp.Body.Close()

	respBody, _ := io.ReadAll(resp.Body)
	if resp.StatusCode == http.StatusNotFound {
		return nil, ErrDocumentNotFound
	}
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf(
			"get user document returned %d: %s",
			resp.StatusCode, string(respBody),
		)
	}

	var result UserDocumentResponse
	if err := json.Unmarshal(
		respBody, &result,
	); err != nil {
		return nil, fmt.Errorf(
			"parsing user document response: %w", err,
		)
	}
	return &result, nil
}

// ProposeUserDocument calls
// POST /userdocuments/{docId}/propose.
func (c *CgsClient) ProposeUserDocument(
	ctx context.Context,
	endpoint string,
	docId string,
) (*ProposalResponse, error) {
	// First get the document version.
	doc, err := c.GetUserDocument(ctx, endpoint, docId)
	if err != nil {
		return nil, fmt.Errorf(
			"getting document version: %w", err,
		)
	}

	body, err := json.Marshal(map[string]string{
		"version": doc.Version,
	})
	if err != nil {
		return nil, fmt.Errorf(
			"marshaling propose input: %w", err,
		)
	}

	url := fmt.Sprintf(
		"%s/userdocuments/%s/propose", endpoint, docId,
	)
	return c.postForProposal(ctx, url, body)
}

// VoteUserDocument calls
// POST /userdocuments/{docId}/vote_accept.
func (c *CgsClient) VoteUserDocument(
	ctx context.Context,
	endpoint string,
	docId string,
	proposalId string,
) error {
	body, err := json.Marshal(map[string]string{
		"proposalId": proposalId,
	})
	if err != nil {
		return fmt.Errorf(
			"marshaling vote input: %w", err,
		)
	}

	url := fmt.Sprintf(
		"%s/userdocuments/%s/vote_accept",
		endpoint, docId,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url,
		bytes.NewReader(body),
	)
	if err != nil {
		return fmt.Errorf(
			"creating vote request: %w", err,
		)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return fmt.Errorf(
			"calling vote user document API: %w", err,
		)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		respBody, _ := io.ReadAll(resp.Body)
		return fmt.Errorf(
			"vote user document returned %d: %s",
			resp.StatusCode, string(respBody),
		)
	}
	return nil
}

// LocalIdpSigningKeyResponse is the response from the
// local IDP /generatesigningkey endpoint.
type LocalIdpSigningKeyResponse struct {
	KID string `json:"kid"`
	X5C string `json:"x5c"`
	PEM string `json:"pem"`
}

// SetLocalIdpIssuerUrl calls POST /setissuerurl on the
// local IDP to configure the issuer URL.
func (c *CgsClient) SetLocalIdpIssuerUrl(
	ctx context.Context,
	idpEndpoint string,
	issuerUrl string,
) error {
	payload := map[string]string{"url": issuerUrl}
	body, err := json.Marshal(payload)
	if err != nil {
		return fmt.Errorf(
			"marshaling issuer url: %w", err,
		)
	}

	url := fmt.Sprintf(
		"%s/setissuerurl", idpEndpoint,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url,
		bytes.NewReader(body),
	)
	if err != nil {
		return fmt.Errorf(
			"creating setissuerurl request: %w", err,
		)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return fmt.Errorf(
			"calling setissuerurl: %w", err,
		)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		respBody, _ := io.ReadAll(resp.Body)
		return fmt.Errorf(
			"setissuerurl returned %d: %s",
			resp.StatusCode, string(respBody),
		)
	}
	return nil
}

// GenerateLocalIdpSigningKey calls POST /generatesigningkey
// on the local IDP to generate a new RSA signing key.
func (c *CgsClient) GenerateLocalIdpSigningKey(
	ctx context.Context,
	idpEndpoint string,
) (*LocalIdpSigningKeyResponse, error) {
	url := fmt.Sprintf(
		"%s/generatesigningkey", idpEndpoint,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url, nil,
	)
	if err != nil {
		return nil, fmt.Errorf(
			"creating generatesigningkey request: %w",
			err,
		)
	}

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf(
			"calling generatesigningkey: %w", err,
		)
	}
	defer resp.Body.Close()

	respBody, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf(
			"generatesigningkey returned %d: %s",
			resp.StatusCode, string(respBody),
		)
	}

	var result LocalIdpSigningKeyResponse
	if err := json.Unmarshal(
		respBody, &result,
	); err != nil {
		return nil, fmt.Errorf(
			"parsing signing key response: %w", err,
		)
	}
	return &result, nil
}

// LocalIdpExportKeysResponse is the response from the
// local IDP /exportkeys endpoint (full key material).
type LocalIdpExportKeysResponse struct {
	KID      string `json:"kid"`
	PrivkPEM string `json:"privk_pem"`
	PubkPEM  string `json:"pubk_pem"`
	CertPEM  string `json:"cert_pem"`
	X5C      string `json:"x5c"`
}

// ExportLocalIdpKeys calls GET /exportkeys on the local
// IDP to export the full signing key material including
// the private key.
func (c *CgsClient) ExportLocalIdpKeys(
	ctx context.Context,
	idpEndpoint string,
) (*LocalIdpExportKeysResponse, error) {
	url := fmt.Sprintf(
		"%s/exportkeys", idpEndpoint,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodGet, url, nil,
	)
	if err != nil {
		return nil, fmt.Errorf(
			"creating exportkeys request: %w", err,
		)
	}

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf(
			"calling exportkeys: %w", err,
		)
	}
	defer resp.Body.Close()

	respBody, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf(
			"exportkeys returned %d: %s",
			resp.StatusCode, string(respBody),
		)
	}

	var result LocalIdpExportKeysResponse
	if err := json.Unmarshal(
		respBody, &result,
	); err != nil {
		return nil, fmt.Errorf(
			"parsing exportkeys response: %w", err,
		)
	}
	return &result, nil
}

// ProposeSetJwtIssuer submits a set_jwt_issuer governance
// proposal via the member's CGS client.
func (c *CgsClient) ProposeSetJwtIssuer(
	ctx context.Context,
	endpoint string,
	issuerUrl string,
) (*ProposalResponse, error) {
	proposal := map[string]interface{}{
		"actions": []map[string]interface{}{
			{
				"name": "set_jwt_issuer",
				"args": map[string]interface{}{
					"issuer":       issuerUrl,
					"auto_refresh": false,
				},
			},
		},
	}
	body, err := json.Marshal(proposal)
	if err != nil {
		return nil, fmt.Errorf(
			"marshaling set_jwt_issuer proposal: %w",
			err,
		)
	}

	url := fmt.Sprintf(
		"%s/proposals/create", endpoint,
	)
	return c.postForProposal(ctx, url, body)
}

// ProposeSetJwtPublicSigningKeys submits a
// set_jwt_public_signing_keys governance proposal via the
// member's CGS client.
func (c *CgsClient) ProposeSetJwtPublicSigningKeys(
	ctx context.Context,
	endpoint string,
	issuerUrl string,
	kid string,
	x5c string,
) (*ProposalResponse, error) {
	proposal := map[string]interface{}{
		"actions": []map[string]interface{}{
			{
				"name": "set_jwt_public_signing_keys",
				"args": map[string]interface{}{
					"issuer": issuerUrl,
					"jwks": map[string]interface{}{
						"keys": []map[string]interface{}{
							{
								"kty": "RSA",
								"kid": kid,
								"x5c": []string{x5c},
							},
						},
					},
				},
			},
		},
	}
	body, err := json.Marshal(proposal)
	if err != nil {
		return nil, fmt.Errorf(
			"marshaling set_jwt_public_signing_keys "+
				"proposal: %w", err,
		)
	}

	url := fmt.Sprintf(
		"%s/proposals/create", endpoint,
	)
	return c.postForProposal(ctx, url, body)
}

// AccessTokenResponse is the JSON response from the
// governance client's /identity/accessToken endpoint.
type AccessTokenResponse struct {
	AccessToken string `json:"accessToken"`
}

// GetAccessToken calls GET /identity/accessToken on the
// governance client endpoint and returns the bearer
// token used to authenticate with inferencing services.
func (c *CgsClient) GetAccessToken(
	ctx context.Context,
	endpoint string,
) (string, error) {
	url := fmt.Sprintf(
		"%s/identity/accessToken", endpoint,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodGet, url, nil,
	)
	if err != nil {
		return "", fmt.Errorf(
			"creating access token request: %w", err,
		)
	}

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return "", fmt.Errorf(
			"calling access token API: %w", err,
		)
	}
	defer resp.Body.Close()

	respBody, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != http.StatusOK {
		return "", fmt.Errorf(
			"access token returned %d: %s",
			resp.StatusCode, string(respBody),
		)
	}

	var result AccessTokenResponse
	if err := json.Unmarshal(
		respBody, &result,
	); err != nil {
		return "", fmt.Errorf(
			"parsing access token response: %w", err,
		)
	}
	if result.AccessToken == "" {
		return "", fmt.Errorf(
			"access token response is empty",
		)
	}
	return result.AccessToken, nil
}
