// Package client provides an HTTP client for the cleanroom-cluster-provider
// REST API.
package client

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"time"

	log "github.com/sirupsen/logrus"
	"go.opentelemetry.io/contrib/instrumentation/net/http/otelhttp"
)

// ClusterClient wraps the cleanroom-cluster-provider-client REST API.
type ClusterClient struct {
	endpoint   string
	httpClient *http.Client
}

// NewClusterClient creates a new provider client.
func NewClusterClient(endpoint string) *ClusterClient {
	return &ClusterClient{
		endpoint: endpoint,
		httpClient: &http.Client{
			Timeout: 5 * time.Minute,
			Transport: otelhttp.NewTransport(
				http.DefaultTransport,
			),
		},
	}
}

// PutClusterInput is the request body for creating/updating a cluster.
type PutClusterInput struct {
	InfraType                  string                           `json:"infraType"`
	ObservabilityProfile       *ObservabilityProfileInput       `json:"observabilityProfile,omitempty"`
	MonitoringProfile          *MonitoringProfileInput          `json:"monitoringProfile,omitempty"`
	AnalyticsWorkloadProfile   *AnalyticsWorkloadProfileInput   `json:"analyticsWorkloadProfile,omitempty"`
	InferencingWorkloadProfile *InferencingWorkloadProfileInput `json:"inferencingWorkloadProfile,omitempty"`
	FlexNodeProfile            *FlexNodeProfileInput            `json:"flexNodeProfile,omitempty"`
	AadProfile                 *AadProfileInput                 `json:"aadProfile,omitempty"`
	ProviderConfig             json.RawMessage                  `json:"providerConfig,omitempty"`
}

// ObservabilityProfileInput mirrors the provider API input.
type ObservabilityProfileInput struct {
	Enabled bool `json:"enabled"`
}

// MonitoringProfileInput mirrors the provider API input.
type MonitoringProfileInput struct {
	Enabled bool `json:"enabled"`
}

// AnalyticsWorkloadProfileInput mirrors the provider API input.
type AnalyticsWorkloadProfileInput struct {
	Enabled                bool                 `json:"enabled"`
	ConfigurationUrl       string               `json:"configurationUrl,omitempty"`
	ConfigurationUrlCaCert string               `json:"configurationUrlCaCert,omitempty"`
	SecurityPolicy         *SecurityPolicyInput `json:"securityPolicy,omitempty"`
	PoolProfile            *WorkloadPoolInput   `json:"poolProfile,omitempty"`
}

// SecurityPolicyInput mirrors the provider API input.
type SecurityPolicyInput struct {
	PolicyCreationOption string `json:"policyCreationOption,omitempty"`
}

// WorkloadPoolInput mirrors the provider API input.
type WorkloadPoolInput struct {
	NodeCount int `json:"nodeCount"`
}

// InferencingWorkloadProfileInput mirrors the provider API input.
type InferencingWorkloadProfileInput struct {
	KServeProfile *KServeProfileInput `json:"kserveProfile,omitempty"`
}

// KServeProfileInput mirrors the provider API input.
type KServeProfileInput struct {
	Enabled                bool                 `json:"enabled"`
	ConfigurationUrl       string               `json:"configurationUrl,omitempty"`
	ConfigurationUrlCaCert string               `json:"configurationUrlCaCert,omitempty"`
	SecurityPolicy         *SecurityPolicyInput `json:"securityPolicy,omitempty"`
}

// FlexNodeProfileInput mirrors the provider API input.
type FlexNodeProfileInput struct {
	Enabled                        bool   `json:"enabled"`
	Mode                           string `json:"mode,omitempty"`
	PolicySigningCertPem           string `json:"policySigningCertPem,omitempty"`
	NodeCount                      *int   `json:"nodeCount,omitempty"`
	VmSize                         string `json:"vmSize,omitempty"`
	OsDiskSizeInGB                 *int   `json:"osDiskSizeInGB,omitempty"`
	MaxPodsPerNode                 *int   `json:"maxPodsPerNode,omitempty"`
	Insecure                       bool   `json:"insecure"`
	SshPrivateKeyPem               string `json:"sshPrivateKeyPem,omitempty"`
	SshPublicKey                   string `json:"sshPublicKey,omitempty"`
	ProvisionUsingSSH              bool   `json:"provisionUsingSSH"`
	RequirePreProvisionedKindNodes bool   `json:"requirePreProvisionedKindNodes,omitempty"`
}

// AadProfileInput mirrors the provider API input.
type AadProfileInput struct {
	Enabled             bool     `json:"enabled"`
	AdminGroupObjectIds []string `json:"adminGroupObjectIds,omitempty"`
}

// GetClusterInput is the request body for get/delete/health operations.
type GetClusterInput struct {
	InfraType      string          `json:"infraType"`
	ProviderConfig json.RawMessage `json:"providerConfig,omitempty"`
}

// GetKubeconfigInput is the request body for kubeconfig retrieval.
type GetKubeconfigInput struct {
	InfraType      string          `json:"infraType"`
	ProviderConfig json.RawMessage `json:"providerConfig,omitempty"`
	AccessRole     string          `json:"accessRole,omitempty"`
	Internal       bool            `json:"internal,omitempty"`
}

// ClusterResponse is the response from create/get operations.
type ClusterResponse struct {
	Name                       string               `json:"name"`
	InfraType                  string               `json:"infraType"`
	ObservabilityProfile       *ObservabilityOutput `json:"observabilityProfile,omitempty"`
	MonitoringProfile          *MonitoringOutput    `json:"monitoringProfile,omitempty"`
	AnalyticsWorkloadProfile   *AnalyticsOutput     `json:"analyticsWorkloadProfile,omitempty"`
	InferencingWorkloadProfile *InferencingOutput   `json:"inferencingWorkloadProfile,omitempty"`
	FlexNodeProfile            *FlexNodeOutput      `json:"flexNodeProfile,omitempty"`
	ProviderProperties         json.RawMessage      `json:"providerProperties,omitempty"`
}

// ObservabilityOutput mirrors the provider API response.
type ObservabilityOutput struct {
	Enabled               bool   `json:"enabled"`
	MetricsEndpoint       string `json:"metricsEndpoint,omitempty"`
	LogsEndpoint          string `json:"logsEndpoint,omitempty"`
	TracesEndpoint        string `json:"tracesEndpoint,omitempty"`
	VisualizationEndpoint string `json:"visualizationEndpoint,omitempty"`
}

// MonitoringOutput mirrors the provider API response.
type MonitoringOutput struct {
	Enabled bool `json:"enabled"`
}

// AnalyticsOutput mirrors the provider API response.
type AnalyticsOutput struct {
	Enabled   bool   `json:"enabled"`
	Namespace string `json:"namespace,omitempty"`
	Endpoint  string `json:"endpoint,omitempty"`
}

// InferencingOutput mirrors the provider API response.
type InferencingOutput struct {
	KServeProfile *KServeOutput `json:"kserveProfile,omitempty"`
}

// KServeOutput mirrors the provider API response.
type KServeOutput struct {
	Enabled   bool   `json:"enabled"`
	Namespace string `json:"namespace,omitempty"`
	Endpoint  string `json:"endpoint,omitempty"`
}

// FlexNodeOutput mirrors the provider API response.
type FlexNodeOutput struct {
	Enabled bool            `json:"enabled"`
	Nodes   json.RawMessage `json:"nodes,omitempty"`
}

// KubeconfigResponse is the response from getkubeconfig.
type KubeconfigResponse struct {
	Kubeconfig []byte `json:"kubeconfig"`
}

// HealthResponse is the response from health checks.
type HealthResponse struct {
	PodHealth json.RawMessage `json:"podHealth,omitempty"`
}

// OperationResponse represents an async operation status.
type OperationResponse struct {
	OperationID string          `json:"operationId"`
	Status      string          `json:"status"`
	Progress    []string        `json:"progress,omitempty"`
	Resource    json.RawMessage `json:"resource,omitempty"`
	Error       json.RawMessage `json:"error,omitempty"`
	StatusCode  *int            `json:"statusCode,omitempty"`
}

// CreateCluster calls POST /clusters/{name}/create?async=true.
func (c *ClusterClient) CreateCluster(
	ctx context.Context,
	name string,
	input *PutClusterInput,
) (string, error) {
	body, err := json.Marshal(input)
	if err != nil {
		return "", fmt.Errorf("marshaling create input: %w", err)
	}

	url := fmt.Sprintf(
		"%s/clusters/%s/create?async=true", c.endpoint, name,
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

	// For 202 Accepted, extract operation ID from Operation-Location header.
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

// UpdateCluster calls POST /clusters/{name}/update?async=true.
func (c *ClusterClient) UpdateCluster(
	ctx context.Context,
	name string,
	input *PutClusterInput,
) (string, error) {
	body, err := json.Marshal(input)
	if err != nil {
		return "", fmt.Errorf("marshaling update input: %w", err)
	}

	url := fmt.Sprintf(
		"%s/clusters/%s/update?async=true", c.endpoint, name,
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
		return "", fmt.Errorf("calling update API: %w", err)
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
				"update returned 202 but no Operation-Location header",
			)
		}
		return opLocation, nil
	}

	return "", nil
}

// GetCluster calls POST /clusters/{name}/get.
func (c *ClusterClient) GetCluster(
	ctx context.Context,
	name string,
	input *GetClusterInput,
) (*ClusterResponse, error) {
	body, err := json.Marshal(input)
	if err != nil {
		return nil, fmt.Errorf("marshaling get input: %w", err)
	}

	url := fmt.Sprintf("%s/clusters/%s/get", c.endpoint, name)
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

	var result ClusterResponse
	if err := json.NewDecoder(resp.Body).Decode(&result); err != nil {
		return nil, fmt.Errorf("decoding get response: %w", err)
	}
	return &result, nil
}

// DeleteCluster calls POST /clusters/{name}/delete.
func (c *ClusterClient) DeleteCluster(
	ctx context.Context,
	name string,
	input *GetClusterInput,
) error {
	body, err := json.Marshal(input)
	if err != nil {
		return fmt.Errorf("marshaling delete input: %w", err)
	}

	url := fmt.Sprintf("%s/clusters/%s/delete", c.endpoint, name)
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

// GetHealth calls POST /clusters/{name}/health.
func (c *ClusterClient) GetHealth(
	ctx context.Context,
	name string,
	input *GetClusterInput,
) (*HealthResponse, error) {
	body, err := json.Marshal(input)
	if err != nil {
		return nil, fmt.Errorf("marshaling health input: %w", err)
	}

	url := fmt.Sprintf("%s/clusters/%s/health", c.endpoint, name)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url, bytes.NewReader(body),
	)
	if err != nil {
		return nil, fmt.Errorf("creating request: %w", err)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf("calling health API: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return nil, c.readError(resp)
	}

	var result HealthResponse
	if err := json.NewDecoder(resp.Body).Decode(&result); err != nil {
		return nil, fmt.Errorf("decoding health response: %w", err)
	}
	return &result, nil
}

// GetKubeconfig calls POST /clusters/{name}/getkubeconfig.
func (c *ClusterClient) GetKubeconfig(
	ctx context.Context,
	name string,
	input *GetKubeconfigInput,
) ([]byte, error) {
	body, err := json.Marshal(input)
	if err != nil {
		return nil, fmt.Errorf(
			"marshaling kubeconfig input: %w", err,
		)
	}

	url := fmt.Sprintf(
		"%s/clusters/%s/getkubeconfig", c.endpoint, name,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url, bytes.NewReader(body),
	)
	if err != nil {
		return nil, fmt.Errorf("creating request: %w", err)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf("calling kubeconfig API: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return nil, c.readError(resp)
	}

	var result KubeconfigResponse
	if err := json.NewDecoder(resp.Body).Decode(&result); err != nil {
		return nil, fmt.Errorf(
			"decoding kubeconfig response: %w", err,
		)
	}
	return result.Kubeconfig, nil
}

// GetOperation polls GET /operations/{operationId}.
func (c *ClusterClient) GetOperation(
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

// CheckReady calls GET /ready to check if the provider client is up.
func (c *ClusterClient) CheckReady(ctx context.Context) error {
	url := fmt.Sprintf("%s/ready", c.endpoint)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodGet, url, nil,
	)
	if err != nil {
		return fmt.Errorf("creating ready request: %w", err)
	}

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return fmt.Errorf("calling ready endpoint: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf(
			"provider client not ready: status %d",
			resp.StatusCode,
		)
	}
	log.Info("Provider client is ready")
	return nil
}

func (c *ClusterClient) readError(resp *http.Response) error {
	body, _ := io.ReadAll(resp.Body)
	return fmt.Errorf(
		"API error (status %d): %s", resp.StatusCode, string(body),
	)
}

// RetryableError indicates a transient failure that the
// caller should retry (e.g. 5xx, connection refused).
type RetryableError struct {
	Err error
}

func (e *RetryableError) Error() string {
	return e.Err.Error()
}

func (e *RetryableError) Unwrap() error {
	return e.Err
}

// IsRetryable reports whether err is a RetryableError.
func IsRetryable(err error) bool {
	var re *RetryableError
	return errors.As(err, &re)
}

func (c *ClusterClient) readRetryableError(
	resp *http.Response,
) error {
	body, _ := io.ReadAll(resp.Body)
	apiErr := fmt.Errorf(
		"API error (status %d): %s",
		resp.StatusCode, string(body),
	)
	if resp.StatusCode >= 500 {
		return &RetryableError{Err: apiErr}
	}
	return apiErr
}

// CreateFlexNodeInput is the request body for creating a
// flex node via PUT /clusters/{name}/flexnodes/{nodeName}.
type CreateFlexNodeInput struct {
	InfraType            string          `json:"infraType"`
	ProviderID           string          `json:"providerID"`
	PolicySigningCertPem string          `json:"policySigningCertPem,omitempty"`
	ProviderConfig       json.RawMessage `json:"providerConfig,omitempty"`
}

// CreateFlexNode calls
// PUT /clusters/{name}/flexnodes/{nodeName}?async=true.
// Returns the Operation-Location for polling.
func (c *ClusterClient) CreateFlexNode(
	ctx context.Context,
	clusterName string,
	nodeName string,
	input *CreateFlexNodeInput,
) (string, error) {
	body, err := json.Marshal(input)
	if err != nil {
		return "", fmt.Errorf(
			"marshaling put flexnode input: %w", err,
		)
	}

	url := fmt.Sprintf(
		"%s/clusters/%s/flexnodes/%s?async=true",
		c.endpoint, clusterName, nodeName,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPut, url, bytes.NewReader(body),
	)
	if err != nil {
		return "", fmt.Errorf("creating request: %w", err)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return "", &RetryableError{
			Err: fmt.Errorf(
				"calling put flexnode API: %w", err,
			),
		}
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusAccepted &&
		resp.StatusCode != http.StatusOK &&
		resp.StatusCode != http.StatusCreated {
		return "", c.readRetryableError(resp)
	}

	if resp.StatusCode == http.StatusAccepted {
		opLocation := resp.Header.Get("Operation-Location")
		if opLocation == "" {
			return "", fmt.Errorf(
				"put flexnode returned 202 but no " +
					"Operation-Location header",
			)
		}
		return opLocation, nil
	}

	return "", nil
}

// DeleteFlexNode calls
// DELETE /clusters/{name}/flexnodes/{nodeName}?async=true.
// Returns the Operation-Location for polling.
func (c *ClusterClient) DeleteFlexNode(
	ctx context.Context,
	clusterName string,
	nodeName string,
	input *GetClusterInput,
) (string, error) {
	body, err := json.Marshal(input)
	if err != nil {
		return "", fmt.Errorf(
			"marshaling delete flexnode input: %w", err,
		)
	}

	url := fmt.Sprintf(
		"%s/clusters/%s/flexnodes/%s?async=true",
		c.endpoint, clusterName, nodeName,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodDelete, url, bytes.NewReader(body),
	)
	if err != nil {
		return "", fmt.Errorf("creating request: %w", err)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return "", fmt.Errorf(
			"calling delete flexnode API: %w", err,
		)
	}
	defer resp.Body.Close()

	if resp.StatusCode == http.StatusNotFound {
		return "", nil
	}

	if resp.StatusCode != http.StatusAccepted &&
		resp.StatusCode != http.StatusOK {
		return "", c.readError(resp)
	}

	if resp.StatusCode == http.StatusAccepted {
		opLocation := resp.Header.Get("Operation-Location")
		if opLocation == "" {
			return "", fmt.Errorf(
				"delete flexnode returned 202 but no " +
					"Operation-Location header",
			)
		}
		return opLocation, nil
	}

	return "", nil
}

// kserveInferencingWorkload/generateDeployment API.
type GenerateDeploymentInput struct {
	InfraType         string               `json:"infraType"`
	ContractUrl       string               `json:"contractUrl,omitempty"`
	ContractUrlCaCert string               `json:"contractUrlCaCert,omitempty"`
	TelemetryProfile  bool                 `json:"telemetryProfile,omitempty"`
	SecurityPolicy    *SecurityPolicyInput `json:"securityPolicy,omitempty"`
	ProviderConfig    json.RawMessage      `json:"providerConfig,omitempty"`
}

// GenerateDeploymentResponse is the response from the
// generateDeployment API.
type GenerateDeploymentResponse struct {
	DeploymentTemplate json.RawMessage `json:"deploymentTemplate"`
	GovernancePolicy   json.RawMessage `json:"governancePolicy"`
}

// GenerateKServeInferencingDeployment calls
// POST /clusters/kserveInferencingWorkload/generateDeployment.
func (c *ClusterClient) GenerateKServeInferencingDeployment(
	ctx context.Context,
	input *GenerateDeploymentInput,
) (*GenerateDeploymentResponse, error) {
	body, err := json.Marshal(input)
	if err != nil {
		return nil, fmt.Errorf(
			"marshaling generate deployment input: %w", err,
		)
	}

	url := fmt.Sprintf(
		"%s/clusters/kserveInferencingWorkload/"+
			"generateDeployment",
		c.endpoint,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url, bytes.NewReader(body),
	)
	if err != nil {
		return nil, fmt.Errorf(
			"creating generate deployment request: %w", err,
		)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf(
			"calling generate deployment API: %w", err,
		)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return nil, c.readError(resp)
	}

	respBody, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, fmt.Errorf(
			"reading generate deployment response: %w", err,
		)
	}

	var result GenerateDeploymentResponse
	if err := json.Unmarshal(
		respBody, &result,
	); err != nil {
		return nil, fmt.Errorf(
			"parsing generate deployment response: %w", err,
		)
	}
	return &result, nil
}
