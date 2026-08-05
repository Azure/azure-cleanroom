// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package sidecar

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"

	retryablehttp "github.com/hashicorp/go-retryablehttp"
	log "github.com/sirupsen/logrus"
)

// RetryPolicy controls retry behaviour for sidecar HTTP calls.
type RetryPolicy struct {
	MaxAttempts int
	BaseBackoff time.Duration
	MaxBackoff  time.Duration
	// PerTryTimeout is the total timeout for the entire request lifecycle.
	// go-retryablehttp does not support per-attempt timeouts; the value is set as
	// the deadline on the underlying http.Client. For localhost sidecars this
	// trade-off is acceptable.
	PerTryTimeout time.Duration
}

// DefaultRetryPolicy returns sensible defaults for sidecar calls.
func DefaultRetryPolicy() RetryPolicy {
	return RetryPolicy{
		MaxAttempts:   5,
		BaseBackoff:   500 * time.Millisecond,
		MaxBackoff:    5 * time.Second,
		PerTryTimeout: 5 * time.Second,
	}
}

// newRetryableClient builds a configured *retryablehttp.Client from a RetryPolicy.
// 429 and all 5xx status codes are retried automatically.
func newRetryableClient(retry RetryPolicy) *retryablehttp.Client {
	if retry.MaxAttempts <= 0 {
		retry.MaxAttempts = 1
	}
	if retry.BaseBackoff <= 0 {
		retry.BaseBackoff = 250 * time.Millisecond
	}
	if retry.MaxBackoff <= 0 {
		retry.MaxBackoff = 2 * time.Second
	}
	if retry.PerTryTimeout <= 0 {
		retry.PerTryTimeout = 5 * time.Second
	}

	c := retryablehttp.NewClient()
	c.RetryMax = retry.MaxAttempts - 1 // retryablehttp counts retries, not attempts.
	c.RetryWaitMin = retry.BaseBackoff
	c.RetryWaitMax = retry.MaxBackoff
	c.HTTPClient.Timeout = retry.PerTryTimeout
	c.Logger = &logrusLeveledLogger{}

	// Retry on network errors, 429, and all 5xx responses.
	c.CheckRetry = func(ctx context.Context, resp *http.Response, err error) (bool, error) {
		if err != nil || resp == nil {
			return retryablehttp.DefaultRetryPolicy(ctx, resp, err)
		}
		if resp.StatusCode == http.StatusTooManyRequests || resp.StatusCode >= 500 {
			return true, nil
		}
		return false, nil
	}

	return c
}

// logrusLeveledLogger adapts logrus to the retryablehttp LeveledLogger interface.
type logrusLeveledLogger struct{}

func (l *logrusLeveledLogger) Error(msg string, keysAndValues ...interface{}) {
	log.WithField("retryablehttp", keysAndValues).Error(msg)
}
func (l *logrusLeveledLogger) Warn(msg string, keysAndValues ...interface{}) {
	log.WithField("retryablehttp", keysAndValues).Warn(msg)
}
func (l *logrusLeveledLogger) Info(msg string, keysAndValues ...interface{}) {
	log.WithField("retryablehttp", keysAndValues).Debug(msg)
}
func (l *logrusLeveledLogger) Debug(msg string, keysAndValues ...interface{}) {
	log.WithField("retryablehttp", keysAndValues).Trace(msg)
}

// doJSONWithRetry sends a JSON request via the retryablehttp client, decoding the
// response body into out (if non-nil). Non-2xx responses that are not retried are
// returned as errors.
func doJSONWithRetry(
	ctx context.Context,
	client *retryablehttp.Client,
	method string,
	url string,
	reqBody any,
	out any,
) error {
	payload, err := json.Marshal(reqBody)
	if err != nil {
		return fmt.Errorf("marshal request: %w", err)
	}

	req, err := retryablehttp.NewRequestWithContext(ctx, method, url, bytes.NewReader(payload))
	if err != nil {
		return fmt.Errorf("create request: %w", err)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := client.Do(req)
	if err != nil {
		return fmt.Errorf("request failed: %w", err)
	}
	defer func() { _ = resp.Body.Close() }()

	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return fmt.Errorf("read response body: %w", err)
	}

	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return fmt.Errorf("http %d: %s", resp.StatusCode, strings.TrimSpace(string(body)))
	}

	if out != nil {
		if err := json.Unmarshal(body, out); err != nil {
			return fmt.Errorf("decode response: %w", err)
		}
	}
	return nil
}
