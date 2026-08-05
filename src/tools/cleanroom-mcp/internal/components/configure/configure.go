// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package configure

import (
	"context"
	"fmt"
	"strings"

	"github.com/azure/azure-cleanroom/src/tools/cleanroom-mcp/internal/config"
	"github.com/azure/azure-cleanroom/src/tools/cleanroom-mcp/internal/logger"
	"github.com/mark3labs/mcp-go/mcp"
)

// RegisterConfigureTool returns the MCP tool definition for runtime
// configuration of cleanroom client names.
func RegisterConfigureTool() mcp.Tool {
	return mcp.NewTool(
		"cleanroom_configure",
		mcp.WithDescription(
			"View or update the cleanroom MCP server configuration. "+
				"Use this tool to set the client names that identify which "+
				"governance, CCF provider, and cluster provider instances to use.\n\n"+
				"Use operation 'show' to see the current configuration, or 'set' "+
				"to update one or more client names. When updating, only the "+
				"parameters you provide will be changed; others remain unchanged.\n\n"+
				"These client names correspond to Docker Compose project names "+
				"and are automatically injected into az cleanroom commands as "+
				"--governance-client or --provider-client flags."),
		mcp.WithString("operation",
			mcp.Required(),
			mcp.Description(
				"The configuration operation to perform:\n"+
					"- 'show': Display the current client configuration.\n"+
					"- 'set': Update one or more client names.")),
		mcp.WithString("governance_client",
			mcp.Description(
				"Name of the governance client (Docker Compose project name). "+
					"Used for 'az cleanroom governance' commands.")),
		mcp.WithString("ccf_provider_client",
			mcp.Description(
				"Name of the CCF provider client. "+
					"Used for 'az cleanroom ccf' commands.")),
		mcp.WithString("cluster_provider_client",
			mcp.Description(
				"Name of the cluster provider client. "+
					"Used for 'az cleanroom cluster' commands.")),
		mcp.WithString("ccf_provider_config",
			mcp.Description(
				"Provider config JSON for CCF commands (CACI infra type). "+
					"Auto-injected as --provider-config for 'az cleanroom ccf' commands.")),
		mcp.WithString("cluster_provider_config",
			mcp.Description(
				"Provider config JSON for cluster commands (AKS infra type). "+
					"Auto-injected as --provider-config for 'az cleanroom cluster' commands.")),
	)
}

// NewConfigureHandler returns an MCP tool handler for the configure tool.
func NewConfigureHandler(
	cfg *config.ConfigData,
) func(ctx context.Context, req mcp.CallToolRequest) (*mcp.CallToolResult, error) {
	return func(ctx context.Context, req mcp.CallToolRequest) (*mcp.CallToolResult, error) {
		args, ok := req.Params.Arguments.(map[string]interface{})
		if !ok {
			return mcp.NewToolResultError(
				fmt.Sprintf("arguments must be a map, got %T", req.Params.Arguments)), nil
		}

		operation, _ := args["operation"].(string)

		switch operation {
		case "show":
			return showConfig(cfg), nil

		case "set":
			gov, _ := args["governance_client"].(string)
			ccf, _ := args["ccf_provider_client"].(string)
			cluster, _ := args["cluster_provider_client"].(string)
			ccfConfig, _ := args["ccf_provider_config"].(string)
			clusterConfig, _ := args["cluster_provider_config"].(string)

			if gov == "" && ccf == "" && cluster == "" &&
				ccfConfig == "" && clusterConfig == "" {
				return mcp.NewToolResultError(
					"at least one parameter must be provided for 'set' operation"), nil
			}

			cfg.SetClients(gov, ccf, cluster, ccfConfig, clusterConfig)
			logger.Infof(
				"Configuration updated: governance=%q, ccf=%q, cluster=%q, "+
					"ccfConfig=%q, clusterConfig=%q",
				gov, ccf, cluster, ccfConfig, clusterConfig)
			return showConfig(cfg), nil

		default:
			return mcp.NewToolResultError(
				"operation must be 'show' or 'set'"), nil
		}
	}
}

func showConfig(cfg *config.ConfigData) *mcp.CallToolResult {
	gov, ccf, cluster, ccfConfig, clusterConfig := cfg.GetClients()

	var sb strings.Builder
	sb.WriteString("Current cleanroom MCP configuration:\n\n")
	sb.WriteString(fmt.Sprintf("  Governance Client:        %s\n", valueOrNotSet(gov)))
	sb.WriteString(fmt.Sprintf("  CCF Provider Client:      %s\n", valueOrNotSet(ccf)))
	sb.WriteString(fmt.Sprintf("  Cluster Provider Client:  %s\n", valueOrNotSet(cluster)))
	sb.WriteString(fmt.Sprintf("  CCF Provider Config:      %s\n", valueOrNotSet(ccfConfig)))
	sb.WriteString(fmt.Sprintf("  Cluster Provider Config:  %s\n",
		valueOrNotSet(clusterConfig)))

	if gov == "" {
		sb.WriteString("\nNote: Governance client is not set. ")
		sb.WriteString("Use operation 'set' with 'governance_client' to configure it.\n")
	}

	return mcp.NewToolResultText(sb.String())
}

func valueOrNotSet(v string) string {
	if v == "" {
		return "(not set)"
	}
	return v
}
