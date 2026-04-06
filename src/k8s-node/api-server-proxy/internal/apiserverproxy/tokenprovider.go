package apiserverproxy

import (
	"bytes"
	"encoding/json"
	"fmt"
	"log"
	"os"
	"os/exec"
	"strings"
	"sync"
	"time"
)

// TokenProvider provides bearer tokens for API server authentication.
// It supports both static tokens and exec-based credential providers.
type TokenProvider struct {
	// Static token (if configured)
	staticToken string

	// Exec-based credential provider (if configured)
	execConfig *ExecConfig

	// Cached token from exec provider
	cachedToken     string
	tokenExpiration time.Time
	mu              sync.RWMutex

	logger *log.Logger
}

// ExecCredential is the response format from an exec credential plugin
// See: https://kubernetes.io/docs/reference/config-api/client-authentication.v1beta1/
type ExecCredential struct {
	APIVersion string                `json:"apiVersion"`
	Kind       string                `json:"kind"`
	Status     *ExecCredentialStatus `json:"status,omitempty"`
}

// ExecCredentialStatus contains the token from an exec credential plugin
type ExecCredentialStatus struct {
	Token                 string     `json:"token,omitempty"`
	ExpirationTimestamp   *time.Time `json:"expirationTimestamp,omitempty"`
	ClientCertificateData string     `json:"clientCertificateData,omitempty"`
	ClientKeyData         string     `json:"clientKeyData,omitempty"`
}

// NewTokenProvider creates a new TokenProvider from LoadedKubeConfig
func NewTokenProvider(kubeConfig *LoadedKubeConfig, logger *log.Logger) *TokenProvider {
	if logger == nil {
		logger = log.New(os.Stdout, "[token-provider] ", log.LstdFlags)
	}

	tp := &TokenProvider{
		staticToken: kubeConfig.BearerToken,
		execConfig:  kubeConfig.ExecConfig,
		logger:      logger,
	}

	return tp
}

// GetToken returns a valid bearer token for API server authentication.
// For exec-based providers, it will execute the command if the cached token
// is expired or not available.
func (tp *TokenProvider) GetToken() (string, error) {
	// If we have a static token, use it
	if tp.staticToken != "" {
		return tp.staticToken, nil
	}

	// If no exec config, no token available
	if tp.execConfig == nil {
		return "", nil
	}

	// Check if we have a valid cached token
	tp.mu.RLock()
	if tp.cachedToken != "" && (tp.tokenExpiration.IsZero() || time.Now().Before(tp.tokenExpiration.Add(-30*time.Second))) {
		token := tp.cachedToken
		tp.mu.RUnlock()
		return token, nil
	}
	tp.mu.RUnlock()

	// Need to refresh the token
	return tp.refreshToken()
}

// refreshToken executes the credential plugin command to get a new token
func (tp *TokenProvider) refreshToken() (string, error) {
	tp.mu.Lock()
	defer tp.mu.Unlock()

	// Double-check after acquiring write lock
	if tp.cachedToken != "" && (tp.tokenExpiration.IsZero() || time.Now().Before(tp.tokenExpiration.Add(-30*time.Second))) {
		return tp.cachedToken, nil
	}

	tp.logger.Printf("Executing credential plugin: %s", tp.execConfig.Command)

	// Build the command
	cmd := exec.Command(tp.execConfig.Command, tp.execConfig.Args...)

	// Set up environment variables
	cmd.Env = os.Environ()
	for _, envVar := range tp.execConfig.Env {
		cmd.Env = append(cmd.Env, fmt.Sprintf("%s=%s", envVar.Name, envVar.Value))
	}

	// Capture stdout and stderr
	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr

	// Execute the command
	if err := cmd.Run(); err != nil {
		return "", fmt.Errorf("exec credential plugin failed: %w, stderr: %s", err, stderr.String())
	}

	// Parse the ExecCredential response
	var execCred ExecCredential
	if err := json.Unmarshal(stdout.Bytes(), &execCred); err != nil {
		// If JSON parsing fails, treat the entire output as a raw token
		// Some simple scripts might just output a token directly
		rawToken := strings.TrimSpace(stdout.String())
		if rawToken != "" {
			tp.logger.Printf("Credential plugin returned raw token (non-JSON output)")
			tp.cachedToken = rawToken
			tp.tokenExpiration = time.Time{} // No expiration info, will refresh on each call
			return rawToken, nil
		}
		return "", fmt.Errorf("failed to parse exec credential response: %w", err)
	}

	// Validate the response
	if execCred.Status == nil {
		return "", fmt.Errorf("exec credential response has no status")
	}

	if execCred.Status.Token == "" {
		return "", fmt.Errorf("exec credential response has no token")
	}

	// Cache the token
	tp.cachedToken = execCred.Status.Token
	if execCred.Status.ExpirationTimestamp != nil {
		tp.tokenExpiration = *execCred.Status.ExpirationTimestamp
		tp.logger.Printf("Token cached, expires at: %s", tp.tokenExpiration.Format(time.RFC3339))
	} else {
		tp.tokenExpiration = time.Time{}
		tp.logger.Printf("Token cached, no expiration")
	}

	return tp.cachedToken, nil
}

// HasExecProvider returns true if this TokenProvider uses an exec-based credential provider
func (tp *TokenProvider) HasExecProvider() bool {
	return tp.execConfig != nil
}

// HasStaticToken returns true if this TokenProvider has a static token configured
func (tp *TokenProvider) HasStaticToken() bool {
	return tp.staticToken != ""
}

// HasCredentials returns true if any form of token authentication is configured
func (tp *TokenProvider) HasCredentials() bool {
	return tp.HasStaticToken() || tp.HasExecProvider()
}
