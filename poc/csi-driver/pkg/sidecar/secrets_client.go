// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package sidecar

import (
	"context"
	"fmt"
	"net/http"
	"strings"

	"github.com/azure/azure-cleanroom/poc/csi-driver/pkg/contracts"
	retryablehttp "github.com/hashicorp/go-retryablehttp"
)

// SecretsClient wraps the secrets sidecar HTTP API (default endpoint :9300).
type SecretsClient struct {
	baseURL string
	client  *retryablehttp.Client
}

// NewSecretsClient creates a SecretsClient backed by go-retryablehttp.
func NewSecretsClient(baseURL string, retry RetryPolicy) *SecretsClient {
	return &SecretsClient{
		baseURL: strings.TrimRight(baseURL, "/"),
		client:  newRetryableClient(retry),
	}
}

// UnwrapDEK calls the secrets sidecar to unwrap a data encryption key.
func (c *SecretsClient) UnwrapDEK(
	ctx context.Context,
	req contracts.UnwrapDEKRequest,
) (string, error) {
	var out contracts.UnwrapDEKResponse
	if err := doJSONWithRetry(ctx, c.client, http.MethodPost, c.baseURL+"/secrets/unwrap", req, &out); err != nil {
		return "", err
	}
	if strings.TrimSpace(out.Value) == "" {
		return "", fmt.Errorf("sidecar returned empty value")
	}
	return out.Value, nil
}
