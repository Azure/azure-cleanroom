// Package client provides HTTP clients for cleanroom provider APIs.
package client

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"mime/multipart"
	"net/http"
	"time"

	"go.opentelemetry.io/contrib/instrumentation/net/http/otelhttp"
)

// CcfNetworkClient wraps the ccf-provider-client REST API.
type CcfNetworkClient struct {
	endpoint   string
	httpClient *http.Client
}

// NewCcfNetworkClient creates a new CCF provider client.
func NewCcfNetworkClient(endpoint string) *CcfNetworkClient {
	return &CcfNetworkClient{
		endpoint: endpoint,
		httpClient: &http.Client{
			Timeout: 5 * time.Minute,
			Transport: otelhttp.NewTransport(
				http.DefaultTransport,
			),
		},
	}
}

// PutNetworkInput is the request body for creating a CCF network.
type PutNetworkInput struct {
	NodeCount      int                  `json:"nodeCount"`
	InfraType      string               `json:"infraType"`
	Members        []MemberInput        `json:"members"`
	NodeLogLevel   string               `json:"nodeLogLevel,omitempty"`
	ProviderConfig json.RawMessage      `json:"providerConfig,omitempty"`
	SecurityPolicy *SecurityPolicyInput `json:"securityPolicy,omitempty"`
}

// MemberInput represents a CCF consortium member.
type MemberInput struct {
	Certificate         string          `json:"certificate"`
	EncryptionPublicKey string          `json:"encryptionPublicKey,omitempty"`
	MemberData          json.RawMessage `json:"memberData,omitempty"`
}

// DeleteNetworkInput is the request body for deleting a CCF network.
type DeleteNetworkInput struct {
	InfraType      string          `json:"infraType"`
	DeleteOption   string          `json:"deleteOption,omitempty"`
	ProviderConfig json.RawMessage `json:"providerConfig,omitempty"`
}

// GetNetworkInput is the request body for get operations.
type GetNetworkInput struct {
	InfraType      string          `json:"infraType"`
	ProviderConfig json.RawMessage `json:"providerConfig,omitempty"`
}

// CcfNetworkResponse is the response from create/get operations.
type CcfNetworkResponse struct {
	Name      string   `json:"name"`
	InfraType string   `json:"infraType"`
	NodeCount int      `json:"nodeCount"`
	Endpoint  string   `json:"endpoint"`
	Nodes     []string `json:"nodes,omitempty"`
}

// CreateNetwork calls POST /networks/{name}/create?async=true.
func (c *CcfNetworkClient) CreateNetwork(
	ctx context.Context,
	name string,
	input *PutNetworkInput,
) (string, error) {
	body, err := json.Marshal(input)
	if err != nil {
		return "", fmt.Errorf("marshaling create input: %w", err)
	}

	url := fmt.Sprintf(
		"%s/networks/%s/create?async=true", c.endpoint, name,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url, bytes.NewReader(body),
	)
	if err != nil {
		return "", fmt.Errorf("creating request: %w", err)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return "", fmt.Errorf("calling create API: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusAccepted &&
		resp.StatusCode != http.StatusOK {
		return "", c.readError(resp)
	}

	if resp.StatusCode == http.StatusAccepted {
		opLocation := resp.Header.Get("Operation-Location")
		if opLocation == "" {
			return "", fmt.Errorf(
				"create returned 202 but no Operation-Location header",
			)
		}
		return opLocation, nil
	}

	return "", nil
}

// GetNetwork calls POST /networks/{name}/get.
func (c *CcfNetworkClient) GetNetwork(
	ctx context.Context,
	name string,
	input *GetNetworkInput,
) (*CcfNetworkResponse, error) {
	body, err := json.Marshal(input)
	if err != nil {
		return nil, fmt.Errorf("marshaling get input: %w", err)
	}

	url := fmt.Sprintf("%s/networks/%s/get", c.endpoint, name)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url, bytes.NewReader(body),
	)
	if err != nil {
		return nil, fmt.Errorf("creating request: %w", err)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf("calling get API: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode == http.StatusNotFound {
		return nil, nil
	}

	if resp.StatusCode != http.StatusOK {
		return nil, c.readError(resp)
	}

	var result CcfNetworkResponse
	if err := json.NewDecoder(resp.Body).Decode(&result); err != nil {
		return nil, fmt.Errorf("decoding get response: %w", err)
	}
	return &result, nil
}

// DeleteNetwork calls POST /networks/{name}/delete.
func (c *CcfNetworkClient) DeleteNetwork(
	ctx context.Context,
	name string,
	input *DeleteNetworkInput,
) error {
	body, err := json.Marshal(input)
	if err != nil {
		return fmt.Errorf("marshaling delete input: %w", err)
	}

	url := fmt.Sprintf("%s/networks/%s/delete", c.endpoint, name)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url, bytes.NewReader(body),
	)
	if err != nil {
		return fmt.Errorf("creating request: %w", err)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return fmt.Errorf("calling delete API: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode == http.StatusNotFound {
		return nil
	}

	if resp.StatusCode != http.StatusOK {
		return c.readError(resp)
	}
	return nil
}

// TransitionToOpenInput is the request body for transitioning
// a CCF network to open state.
type TransitionToOpenInput struct {
	InfraType                  string          `json:"infraType"`
	PreviousServiceCertificate string          `json:"previousServiceCertificate,omitempty"`
	ProviderConfig             json.RawMessage `json:"providerConfig,omitempty"`
}

// ConfigureProvider calls POST /configure on the CCF provider
// client to set up signing cert and key used for governance
// proposals (e.g. transitionToOpen).
func (c *CcfNetworkClient) ConfigureProvider(
	ctx context.Context,
	signingCertPEM string,
	signingKeyPEM string,
) error {
	var buf bytes.Buffer
	w := multipart.NewWriter(&buf)

	certPart, err := w.CreateFormFile(
		"SigningCertPemFile", "signing-cert.pem",
	)
	if err != nil {
		return fmt.Errorf(
			"creating cert form field: %w", err,
		)
	}
	if _, err := certPart.Write(
		[]byte(signingCertPEM),
	); err != nil {
		return fmt.Errorf(
			"writing cert form field: %w", err,
		)
	}

	keyPart, err := w.CreateFormFile(
		"SigningKeyPemFile", "signing-key.pem",
	)
	if err != nil {
		return fmt.Errorf(
			"creating key form field: %w", err,
		)
	}
	if _, err := keyPart.Write(
		[]byte(signingKeyPEM),
	); err != nil {
		return fmt.Errorf(
			"writing key form field: %w", err,
		)
	}
	w.Close()

	url := fmt.Sprintf("%s/configure", c.endpoint)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url, &buf,
	)
	if err != nil {
		return fmt.Errorf(
			"creating configure request: %w", err,
		)
	}
	req.Header.Set("Content-Type", w.FormDataContentType())

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return fmt.Errorf(
			"calling configure API: %w", err,
		)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return c.readError(resp)
	}
	return nil
}

// TransitionToOpen calls POST /networks/{name}/transitionToOpen.
func (c *CcfNetworkClient) TransitionToOpen(
	ctx context.Context,
	name string,
	input *TransitionToOpenInput,
) error {
	body, err := json.Marshal(input)
	if err != nil {
		return fmt.Errorf(
			"marshaling transitionToOpen input: %w", err,
		)
	}

	url := fmt.Sprintf(
		"%s/networks/%s/transitionToOpen",
		c.endpoint, name,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url, bytes.NewReader(body),
	)
	if err != nil {
		return fmt.Errorf("creating request: %w", err)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return fmt.Errorf(
			"calling transitionToOpen API: %w", err,
		)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return c.readError(resp)
	}
	return nil
}

// GetOperation calls GET on the operation location URL.
func (c *CcfNetworkClient) GetOperation(
	ctx context.Context,
	operationLocation string,
) (*OperationResponse, error) {
	url := fmt.Sprintf("%s%s", c.endpoint, operationLocation)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodGet, url, nil,
	)
	if err != nil {
		return nil, fmt.Errorf("creating request: %w", err)
	}

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf("calling operation API: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode == http.StatusNotFound {
		return nil, nil
	}

	if resp.StatusCode != http.StatusOK {
		return nil, c.readError(resp)
	}

	var result OperationResponse
	if err := json.NewDecoder(resp.Body).Decode(&result); err != nil {
		return nil, fmt.Errorf(
			"decoding operation response: %w", err,
		)
	}
	return &result, nil
}

func (c *CcfNetworkClient) readError(
	resp *http.Response,
) error {
	body, _ := io.ReadAll(resp.Body)
	return fmt.Errorf(
		"API error (status %d): %s", resp.StatusCode, string(body),
	)
}

// RecoveryAgentResponse is the response from the recovery
// agent get endpoint.
type RecoveryAgentResponse struct {
	Endpoint string          `json:"endpoint"`
	Agents   []RecoveryAgent `json:"agents"`
}

// RecoveryAgent represents a single CCF recovery agent.
type RecoveryAgent struct {
	Name        string `json:"name"`
	Endpoint    string `json:"endpoint"`
	ServiceCert string `json:"serviceCert"`
}

// RecoveryAgentReportResponse is the response from the
// recovery agent report endpoints.
type RecoveryAgentReportResponse struct {
	Reports []RecoveryAgentReport `json:"reports"`
}

// RecoveryAgentReport is a single report entry.
type RecoveryAgentReport struct {
	Name     string                   `json:"name"`
	Endpoint string                   `json:"endpoint"`
	Report   *RecoveryAgentReportData `json:"report,omitempty"`
}

// RecoveryAgentReportData is the inner report data
// from the recovery agent's /network/report endpoint.
type RecoveryAgentReportData struct {
	ReportDataPayload string `json:"reportDataPayload"`
}

// GetRecoveryAgent calls POST
// /networks/{name}/recoveryAgents/get.
func (c *CcfNetworkClient) GetRecoveryAgent(
	ctx context.Context,
	networkName string,
	infraType string,
	providerConfig json.RawMessage,
) (*RecoveryAgentResponse, error) {
	input := map[string]interface{}{
		"infraType": infraType,
	}
	if providerConfig != nil {
		input["providerConfig"] = json.RawMessage(
			providerConfig,
		)
	}

	body, err := json.Marshal(input)
	if err != nil {
		return nil, fmt.Errorf(
			"marshaling recovery agent input: %w", err,
		)
	}

	url := fmt.Sprintf(
		"%s/networks/%s/recoveryAgents/get",
		c.endpoint, networkName,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url,
		bytes.NewReader(body),
	)
	if err != nil {
		return nil, fmt.Errorf(
			"creating recovery agent request: %w", err,
		)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf(
			"calling recovery agent API: %w", err,
		)
	}
	defer resp.Body.Close()

	if resp.StatusCode == http.StatusNotFound {
		return nil, nil
	}

	if resp.StatusCode != http.StatusOK {
		return nil, c.readError(resp)
	}

	var result RecoveryAgentResponse
	if err := json.NewDecoder(
		resp.Body,
	).Decode(&result); err != nil {
		return nil, fmt.Errorf(
			"decoding recovery agent response: %w",
			err,
		)
	}
	return &result, nil
}

// GetRecoveryAgentReport calls POST
// /networks/{name}/recoveryAgents/report.
func (c *CcfNetworkClient) GetRecoveryAgentReport(
	ctx context.Context,
	networkName string,
	infraType string,
	providerConfig json.RawMessage,
) (*RecoveryAgentReportResponse, error) {
	input := map[string]interface{}{
		"infraType": infraType,
	}
	if providerConfig != nil {
		input["providerConfig"] = json.RawMessage(
			providerConfig,
		)
	}

	body, err := json.Marshal(input)
	if err != nil {
		return nil, fmt.Errorf(
			"marshaling report input: %w", err,
		)
	}

	url := fmt.Sprintf(
		"%s/networks/%s/recoveryAgents/report",
		c.endpoint, networkName,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url,
		bytes.NewReader(body),
	)
	if err != nil {
		return nil, fmt.Errorf(
			"creating report request: %w", err,
		)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf(
			"calling report API: %w", err,
		)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return nil, c.readError(resp)
	}

	var result RecoveryAgentReportResponse
	if err := json.NewDecoder(
		resp.Body,
	).Decode(&result); err != nil {
		return nil, fmt.Errorf(
			"decoding report response: %w", err,
		)
	}
	return &result, nil
}

// GetRecoveryAgentNetworkReport calls POST
// /networks/{name}/recoveryAgents/network/report.
// This proxies to the recovery agent's /network/report
// endpoint which returns the reportDataPayload with
// constitutionDigest and jsappBundleDigest.
func (c *CcfNetworkClient) GetRecoveryAgentNetworkReport(
	ctx context.Context,
	networkName string,
	infraType string,
	providerConfig json.RawMessage,
) (*RecoveryAgentReportResponse, error) {
	input := map[string]interface{}{
		"infraType": infraType,
	}
	if providerConfig != nil {
		input["providerConfig"] = json.RawMessage(
			providerConfig,
		)
	}

	body, err := json.Marshal(input)
	if err != nil {
		return nil, fmt.Errorf(
			"marshaling network report input: %w",
			err,
		)
	}

	url := fmt.Sprintf(
		"%s/networks/%s/recoveryAgents/network/report",
		c.endpoint, networkName,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url,
		bytes.NewReader(body),
	)
	if err != nil {
		return nil, fmt.Errorf(
			"creating network report request: %w",
			err,
		)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf(
			"calling network report API: %w", err,
		)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return nil, c.readError(resp)
	}

	var result RecoveryAgentReportResponse
	if err := json.NewDecoder(
		resp.Body,
	).Decode(&result); err != nil {
		return nil, fmt.Errorf(
			"decoding network report response: %w",
			err,
		)
	}
	return &result, nil
}

// NetworkReportResponse is the response from the
// CCF network report endpoint.
type NetworkReportResponse struct {
	Reports []NetworkReport `json:"reports"`
}

// NetworkReport is a single CCF node report entry.
type NetworkReport struct {
	HostData string `json:"hostData"`
	NodeID   string `json:"nodeId"`
	Format   string `json:"format"`
}

// GetReport calls POST /networks/{name}/report.
func (c *CcfNetworkClient) GetReport(
	ctx context.Context,
	networkName string,
	infraType string,
	providerConfig json.RawMessage,
) (*NetworkReportResponse, error) {
	input := map[string]interface{}{
		"infraType": infraType,
	}
	if providerConfig != nil {
		input["providerConfig"] = json.RawMessage(
			providerConfig,
		)
	}

	body, err := json.Marshal(input)
	if err != nil {
		return nil, fmt.Errorf(
			"marshaling report input: %w", err,
		)
	}

	url := fmt.Sprintf(
		"%s/networks/%s/report",
		c.endpoint, networkName,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url,
		bytes.NewReader(body),
	)
	if err != nil {
		return nil, fmt.Errorf(
			"creating report request: %w", err,
		)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf(
			"calling report API: %w", err,
		)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return nil, c.readError(resp)
	}

	var result NetworkReportResponse
	if err := json.NewDecoder(
		resp.Body,
	).Decode(&result); err != nil {
		return nil, fmt.Errorf(
			"decoding report response: %w", err,
		)
	}
	return &result, nil
}
