// Package client provides HTTP clients for cleanroom
// provider APIs.
package client

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
)

// ErrDeploymentNotFound is returned when the
// inferencing agent returns 404 for a deployment.
var ErrDeploymentNotFound = errors.New(
	"deployment not found",
)

// InferencingClient wraps the inferencing agent REST API
// used to deploy and poll model endpoints.
type InferencingClient struct {
	baseURL    string
	httpClient *http.Client
}

// NewInferencingClient creates a new inferencing client
// targeting the given base URL. The httpClient should be
// configured with appropriate TLS and auth for the
// transport mode (direct or API server proxy).
func NewInferencingClient(
	baseURL string,
	httpClient *http.Client,
) *InferencingClient {
	return &InferencingClient{
		baseURL:    baseURL,
		httpClient: httpClient,
	}
}

// DeploymentStatus represents the status portion of the
// inferencing agent's GET /inferenceServices/{name}/status
// response.
type DeploymentStatus struct {
	URL        string            `json:"url,omitempty"`
	Conditions []StatusCondition `json:"conditions,omitempty"`
	Status     *DeploymentInner  `json:"status,omitempty"`
	Name       string            `json:"name,omitempty"`
	PodHealth  map[string]any    `json:"podHealth,omitempty"`
	Raw        map[string]any    `json:"-"`
}

// DeploymentInner holds the nested status object returned
// by the inferencing agent.
type DeploymentInner struct {
	URL        string            `json:"url,omitempty"`
	Conditions []StatusCondition `json:"conditions,omitempty"`
}

// StatusCondition is a condition entry reported by the
// inferencing agent status endpoint.
type StatusCondition struct {
	Type   string `json:"type"`
	Status string `json:"status"`
}

// SubmitDeployment calls POST /inferenceServices on the
// inferencing agent to deploy a model. It is idempotent —
// HTTP 409 (conflict) is treated as success.
func (c *InferencingClient) SubmitDeployment(
	ctx context.Context,
	token string,
	correlationID string,
	clientRequestID string,
	body map[string]any,
) error {
	url := fmt.Sprintf(
		"%s/inferenceServices", c.baseURL,
	)

	payload, err := json.Marshal(body)
	if err != nil {
		return fmt.Errorf(
			"marshaling deployment body: %w", err,
		)
	}

	req, err := http.NewRequestWithContext(
		ctx, http.MethodPost, url,
		bytes.NewReader(payload),
	)
	if err != nil {
		return fmt.Errorf(
			"creating submit deployment request: %w",
			err,
		)
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set(
		"x-ms-cleanroom-authorization",
		"Bearer "+token,
	)
	req.Header.Set(
		"x-ms-correlation-id", correlationID,
	)
	req.Header.Set(
		"x-ms-client-request-id", clientRequestID,
	)

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return fmt.Errorf(
			"calling inferencing submit API: %w", err,
		)
	}
	defer resp.Body.Close()

	// 409 Conflict means the deployment already exists;
	// treat as success for idempotency.
	if resp.StatusCode == http.StatusConflict {
		return nil
	}

	if resp.StatusCode < 200 ||
		resp.StatusCode >= 300 {
		respBody, _ := io.ReadAll(resp.Body)
		return fmt.Errorf(
			"submit deployment returned %d: %s",
			resp.StatusCode, string(respBody),
		)
	}
	return nil
}

// GetDeploymentStatus calls
// GET /inferenceServices/{name}/status on the
// inferencing agent and returns the parsed status.
func (c *InferencingClient) GetDeploymentStatus(
	ctx context.Context,
	token string,
	name string,
	correlationID string,
	clientRequestID string,
) (*DeploymentStatus, error) {
	url := fmt.Sprintf(
		"%s/inferenceServices/%s/status",
		c.baseURL, name,
	)

	req, err := http.NewRequestWithContext(
		ctx, http.MethodGet, url, nil,
	)
	if err != nil {
		return nil, fmt.Errorf(
			"creating get status request: %w", err,
		)
	}
	req.Header.Set(
		"x-ms-cleanroom-authorization",
		"Bearer "+token,
	)
	req.Header.Set(
		"x-ms-correlation-id", correlationID,
	)
	req.Header.Set(
		"x-ms-client-request-id", clientRequestID,
	)

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf(
			"calling inferencing status API: %w", err,
		)
	}
	defer resp.Body.Close()

	respBody, _ := io.ReadAll(resp.Body)
	if resp.StatusCode == http.StatusNotFound {
		return nil, fmt.Errorf(
			"get deployment status returned 404: "+
				"%s: %w",
			string(respBody),
			ErrDeploymentNotFound,
		)
	}
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf(
			"get deployment status returned %d: %s",
			resp.StatusCode, string(respBody),
		)
	}

	// Parse as raw map first, then extract known fields.
	var raw map[string]any
	if err := json.Unmarshal(respBody, &raw); err != nil {
		return nil, fmt.Errorf(
			"parsing status response: %w", err,
		)
	}

	result := &DeploymentStatus{
		Name: name,
		Raw:  raw,
	}

	// Extract pod health diagnostics if present.
	if ph, ok :=
		raw["podHealth"].(map[string]any); ok {
		result.PodHealth = ph
	}

	// The response shape is:
	// { "status": { "url": "...", "conditions": [...] } }
	statusObj, _ := raw["status"].(map[string]any)
	if statusObj != nil {
		if u, ok := statusObj["url"].(string); ok {
			result.URL = u
		}
		if conds, ok :=
			statusObj["conditions"].([]any); ok {
			for _, ci := range conds {
				cm, _ := ci.(map[string]any)
				if cm == nil {
					continue
				}
				result.Conditions = append(
					result.Conditions,
					StatusCondition{
						Type: fmt.Sprintf(
							"%v", cm["type"],
						),
						Status: fmt.Sprintf(
							"%v", cm["status"],
						),
					},
				)
			}
		}
	}

	return result, nil
}

// IsReady returns true if the deployment has a URL and
// the Ready condition is True (or no conditions are
// reported yet).
func (s *DeploymentStatus) IsReady() bool {
	if s.URL == "" {
		return false
	}
	for _, c := range s.Conditions {
		if c.Type == "Ready" {
			return c.Status == "True"
		}
	}
	// No Ready condition yet — treat as ready if URL
	// is present (early deployment phase).
	return true
}

// FirstContainerError extracts the first unhealthy
// container error message from PodHealth, if any.
// Returns empty string when all containers are healthy
// or in normal transient states (PodInitializing,
// ContainerCreating).
func (s *DeploymentStatus) FirstContainerError() string {
	if s.PodHealth == nil {
		return ""
	}
	pods, _ := s.PodHealth["pods"].([]any)
	for _, pi := range pods {
		pod, _ := pi.(map[string]any)
		if pod == nil {
			continue
		}
		podName, _ := pod["name"].(string)
		containers, _ :=
			pod["containers"].([]any)
		for _, ci := range containers {
			c, _ := ci.(map[string]any)
			if c == nil {
				continue
			}
			ready, _ := c["ready"].(bool)
			if ready {
				continue
			}
			name, _ := c["name"].(string)
			state, _ := c["state"].(string)
			if isTransientContainerState(state) {
				continue
			}
			msg, _ := c["message"].(string)
			if msg != "" {
				return fmt.Sprintf(
					"pod %s container %s: %s: %s",
					podName, name, state, msg,
				)
			}
			if state != "" && state != "Running" {
				return fmt.Sprintf(
					"pod %s container %s: %s",
					podName, name, state,
				)
			}
		}
	}
	return ""
}

// isTransientContainerState returns true for container
// waiting states that are normal during pod startup and
// should not be reported as errors.
func isTransientContainerState(state string) bool {
	switch state {
	case "PodInitializing", "ContainerCreating":
		return true
	}
	return false
}

// ContainerStateMessages returns a summary line for each
// container across all pods, including transient states
// like PodInitializing and Running. Use this for progress
// events rather than error reporting.
func (s *DeploymentStatus) ContainerStateMessages() []string {
	if s.PodHealth == nil {
		return nil
	}
	var msgs []string
	pods, _ := s.PodHealth["pods"].([]any)
	for _, pi := range pods {
		pod, _ := pi.(map[string]any)
		if pod == nil {
			continue
		}
		podName, _ := pod["name"].(string)
		containers, _ :=
			pod["containers"].([]any)
		for _, ci := range containers {
			c, _ := ci.(map[string]any)
			if c == nil {
				continue
			}
			name, _ := c["name"].(string)
			ready, _ := c["ready"].(bool)
			state, _ := c["state"].(string)
			if ready || state == "Running" {
				msgs = append(msgs, fmt.Sprintf(
					"pod %s container %s: Running",
					podName, name,
				))
				continue
			}
			if state == "" {
				continue
			}
			msgs = append(msgs, fmt.Sprintf(
				"pod %s container %s: %s",
				podName, name, state,
			))
		}
	}
	return msgs
}
