// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package config

import (
	"fmt"
	"os"
	"sync"

	flag "github.com/spf13/pflag"
)

// ConfigData holds the global configuration for the cleanroom MCP server.
// The client name fields can be updated at runtime via the cleanroom_config tool.
type ConfigData struct {
	Transport string
	Host      string
	Port      int
	LogLevel  string
	Timeout   int

	// Client names for az cleanroom commands (mutable at runtime).
	mu                    sync.RWMutex
	GovernanceClient      string
	CCFProviderClient     string
	ClusterProviderClient string

	// Provider config JSON for AKS/CACI infra types (mutable at runtime).
	CCFProviderConfig     string
	ClusterProviderConfig string
}

// GetClients returns the current client and provider configuration (thread-safe).
func (cfg *ConfigData) GetClients() (
	governance, ccfProvider, clusterProvider,
	ccfProviderConfig, clusterProviderConfig string,
) {
	cfg.mu.RLock()
	defer cfg.mu.RUnlock()
	return cfg.GovernanceClient, cfg.CCFProviderClient, cfg.ClusterProviderClient,
		cfg.CCFProviderConfig, cfg.ClusterProviderConfig
}

// SetClients updates one or more client names and provider configs at runtime
// (thread-safe). Empty strings are ignored (existing values are kept).
func (cfg *ConfigData) SetClients(
	governance, ccfProvider, clusterProvider,
	ccfProviderConfig, clusterProviderConfig string,
) {
	cfg.mu.Lock()
	defer cfg.mu.Unlock()
	if governance != "" {
		cfg.GovernanceClient = governance
	}
	if ccfProvider != "" {
		cfg.CCFProviderClient = ccfProvider
	}
	if clusterProvider != "" {
		cfg.ClusterProviderClient = clusterProvider
	}
	if ccfProviderConfig != "" {
		cfg.CCFProviderConfig = ccfProviderConfig
	}
	if clusterProviderConfig != "" {
		cfg.ClusterProviderConfig = clusterProviderConfig
	}
}

// NewConfig creates a new configuration instance with defaults.
func NewConfig() *ConfigData {
	return &ConfigData{
		Transport:             "stdio",
		Host:                  "127.0.0.1",
		Port:                  8080,
		LogLevel:              "info",
		Timeout:               120,
		GovernanceClient:      envOrDefault("GOVERNANCE_CLIENT", ""),
		CCFProviderClient:     envOrDefault("CCF_PROVIDER_CLIENT", "ccf-provider"),
		ClusterProviderClient: envOrDefault("CLUSTER_PROVIDER_CLIENT", "cleanroom-cluster-provider"),
		CCFProviderConfig:     envOrDefault("CCF_PROVIDER_CONFIG", ""),
		ClusterProviderConfig: envOrDefault("CLUSTER_PROVIDER_CONFIG", ""),
	}
}

func envOrDefault(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}

// ParseFlags parses command line arguments and updates the configuration.
func (cfg *ConfigData) ParseFlags() {
	flag.StringVar(
		&cfg.Transport, "transport", cfg.Transport,
		"Transport mechanism to use (stdio, sse or streamable-http)")
	flag.StringVar(&cfg.Host, "host", cfg.Host,
		"Host to listen on (only used with transport sse or streamable-http)")
	flag.IntVar(&cfg.Port, "port", cfg.Port,
		"Port to listen on (only used with transport sse or streamable-http)")
	flag.IntVar(&cfg.Timeout, "timeout", cfg.Timeout,
		"Timeout for CLI command execution in seconds")
	flag.StringVar(&cfg.LogLevel, "log-level", cfg.LogLevel,
		"Log level (debug, info, warn, error)")
	flag.StringVar(&cfg.GovernanceClient, "governance-client", cfg.GovernanceClient,
		"Name of the governance client (Docker Compose project name)")
	flag.StringVar(&cfg.CCFProviderClient, "ccf-provider-client", cfg.CCFProviderClient,
		"Name of the CCF provider client (default \"ccf-provider\")")
	flag.StringVar(&cfg.ClusterProviderClient, "cluster-provider-client",
		cfg.ClusterProviderClient,
		"Name of the cluster provider client (default \"cleanroom-cluster-provider\")")
	flag.StringVar(&cfg.CCFProviderConfig, "ccf-provider-config",
		cfg.CCFProviderConfig,
		"Provider config JSON for CCF commands (CACI infra type)")
	flag.StringVar(&cfg.ClusterProviderConfig, "cluster-provider-config",
		cfg.ClusterProviderConfig,
		"Provider config JSON for cluster commands (AKS infra type)")

	var showHelp bool
	flag.BoolVarP(&showHelp, "help", "h", false, "Show help message")

	if err := flag.CommandLine.Parse(os.Args[1:]); err != nil {
		fmt.Printf("\nUsage of %s:\n", os.Args[0])
		flag.PrintDefaults()
		os.Exit(1)
	}

	if showHelp {
		fmt.Printf("Usage of %s:\n", os.Args[0])
		flag.PrintDefaults()
		os.Exit(0)
	}
}
