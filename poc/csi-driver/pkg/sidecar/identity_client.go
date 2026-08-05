// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package sidecar

import (
	"context"
	"net/http"
	"strings"

	"github.com/azure/azure-cleanroom/poc/csi-driver/pkg/contracts"
	retryablehttp "github.com/hashicorp/go-retryablehttp"
)

// IdentityClient wraps the identity sidecar HTTP API (default endpoint :8290).
type IdentityClient struct {
	baseURL string
	client  *retryablehttp.Client
}

// NewIdentityClient creates an IdentityClient backed by go-retryablehttp.
func NewIdentityClient(baseURL string, retry RetryPolicy) *IdentityClient {
	return &IdentityClient{
		baseURL: strings.TrimRight(baseURL, "/"),
		client:  newRetryableClient(retry),
	}
}

// Register calls the identity sidecar to register a federated credential.
func (c *IdentityClient) Register(
	ctx context.Context,
	req contracts.RegisterIdentityRequest,
) error {
	return doJSONWithRetry(ctx, c.client, http.MethodPost, c.baseURL+"/metadata/identity/register", req, nil)
}
