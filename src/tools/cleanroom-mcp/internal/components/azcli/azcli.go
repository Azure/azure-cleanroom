// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package azcli

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/azure/azure-cleanroom/src/tools/cleanroom-mcp/internal/command"
	"github.com/azure/azure-cleanroom/src/tools/cleanroom-mcp/internal/config"
	"github.com/azure/azure-cleanroom/src/tools/cleanroom-mcp/internal/logger"
	"github.com/mark3labs/mcp-go/mcp"
)

// RegisterCallAzCleanroomTool returns the MCP tool definition for the unified
// az cleanroom CLI tool.
func RegisterCallAzCleanroomTool() mcp.Tool {
	return mcp.NewTool(
		"call_az_cleanroom",
		mcp.WithDescription(
			"Execute Azure CLI commands for managing Azure Clean Room resources. "+
				"This tool provides a unified interface to run any `az cleanroom` command.\n\n"+
				"Available command groups:\n"+
				"- `az cleanroom governance` - Manage governance contracts, proposals, "+
				"voting, secrets, members, deployments, OIDC issuers, and CA.\n"+
				"- `az cleanroom ccf` - Manage CCF networks, providers, recovery "+
				"services, and consortium managers.\n"+
				"- `az cleanroom cluster` - Manage cleanroom clusters, providers, "+
				"and workload deployments.\n"+
				"- `az cleanroom config` - Initialize and manage cleanroom configurations, "+
				"applications, data sources, identities, and network settings.\n"+
				"- `az cleanroom datastore` - Manage data stores (add, upload, "+
				"download, encrypt, decrypt).\n"+
				"- `az cleanroom secretstore` - Manage secret stores.\n"+
				"- `az cleanroom telemetry` - Download and decrypt telemetry data.\n"+
				"- `az cleanroom logs` - Download and decrypt logs.\n"+
				"- `az cleanroom collaboration` - Manage collaboration contexts, "+
				"identities, datasets, and Spark SQL queries.\n\n"+
				"Commands must start with `az cleanroom`. Shell features like pipes (|), "+
				"redirects (>), or command substitution ($()) are not allowed.\n\n"+
				"Use `--output json` for structured output when needed."),
		mcp.WithString("cli_command",
			mcp.Required(),
			mcp.Description(
				"The complete Azure CLI command to execute. Must start with "+
					"`az cleanroom`. Examples:\n"+
					"- `az cleanroom governance contract show --id my-contract`\n"+
					"- `az cleanroom ccf network show-health --name my-network`\n"+
					"- `az cleanroom cluster show --name my-cluster`\n"+
					"- `az cleanroom governance proposal list --output json`")),
		mcp.WithNumber("timeout",
			mcp.Description(
				"Optional timeout in seconds for command execution. "+
					"Defaults to the server's configured timeout.")),
	)
}

// NewCallAzCleanroomHandler returns an MCP tool handler for executing
// az cleanroom CLI commands.
func NewCallAzCleanroomHandler(
	cfg *config.ConfigData,
) func(ctx context.Context, req mcp.CallToolRequest) (*mcp.CallToolResult, error) {
	return func(ctx context.Context, req mcp.CallToolRequest) (*mcp.CallToolResult, error) {
		args, ok := req.Params.Arguments.(map[string]interface{})
		if !ok {
			return mcp.NewToolResultError(
				fmt.Sprintf("arguments must be a map, got %T", req.Params.Arguments)), nil
		}

		cliCommand, ok := args["cli_command"].(string)
		if !ok || cliCommand == "" {
			return mcp.NewToolResultError(
				"missing or invalid 'cli_command' parameter"), nil
		}

		// Validate the command starts with "az cleanroom".
		cliCommand = strings.TrimSpace(cliCommand)
		if !strings.HasPrefix(cliCommand, "az cleanroom") {
			return mcp.NewToolResultError(
				"command must start with 'az cleanroom'"), nil
		}

		// Auto-inject client parameters if not already present.
		cliCommand = injectClientParams(cliCommand, cfg)

		timeout := cfg.Timeout
		if timeoutParam, ok := args["timeout"].(float64); ok && timeoutParam > 0 {
			timeout = int(timeoutParam)
		}

		logger.Infof("Executing: %s (timeout: %ds)", sanitizeCommand(cliCommand), timeout)

		cmdCtx, cancel := context.WithTimeout(ctx, time.Duration(timeout)*time.Second)
		defer cancel()

		result, err := command.Execute(cmdCtx, cliCommand, timeout)
		if err != nil {
			errMsg := err.Error()
			if result != nil && result.Error != "" {
				errMsg = result.Error
			}
			logger.Errorf("Command failed: %s", errMsg)
			return mcp.NewToolResultError(
				fmt.Sprintf("Command failed: %s", errMsg)), nil
		}

		output := strings.TrimSpace(result.Output)
		if output == "" {
			output = "Command completed successfully with no output."
		}

		logger.Infof("Command succeeded: %s", sanitizeCommand(cliCommand))
		return mcp.NewToolResultText(output), nil
	}
}

// injectClientParams appends the appropriate --governance-client or
// --provider-client flag if the command belongs to a known group and
// does not already include the flag.
func injectClientParams(cmd string, cfg *config.ConfigData) string {
	gov, ccfProv, clusterProv, ccfProvConfig, clusterProvConfig := cfg.GetClients()

	// Governance commands — skip "governance client" and "governance service"
	// subgroups as they use --name instead of --governance-client.
	if strings.Contains(cmd, "az cleanroom governance") ||
		strings.Contains(cmd, "az cleanroom config wrap-deks") ||
		strings.Contains(cmd, "az cleanroom config wrap-secret") {
		if !strings.Contains(cmd, "az cleanroom governance client") &&
			!strings.Contains(cmd, "az cleanroom governance service") {
			if gov != "" &&
				!strings.Contains(cmd, "--governance-client") {
				cmd += " --governance-client " + gov
			}
		}
		return cmd
	}

	// CCF commands — only network, recovery-service, and consortium-manager
	// subgroups use --provider-client. The "ccf provider" subgroup uses --name.
	if strings.Contains(cmd, "az cleanroom ccf network") ||
		strings.Contains(cmd, "az cleanroom ccf recovery-service") ||
		strings.Contains(cmd, "az cleanroom ccf consortium-manager") {
		if ccfProv != "" &&
			!strings.Contains(cmd, "--provider-client") {
			cmd += " --provider-client " + ccfProv
		}
		if ccfProvConfig != "" &&
			!strings.Contains(cmd, "--provider-config") {
			cmd += " --provider-config " + ccfProvConfig
		}
		return cmd
	}

	// Cluster commands — only non-provider subcommands use --provider-client.
	// The "cluster provider" subgroup uses --name.
	if strings.Contains(cmd, "az cleanroom cluster") &&
		!strings.Contains(cmd, "az cleanroom cluster provider") {
		if clusterProv != "" &&
			!strings.Contains(cmd, "--provider-client") {
			cmd += " --provider-client " + clusterProv
		}
		if clusterProvConfig != "" &&
			!strings.Contains(cmd, "--provider-config") {
			cmd += " --provider-config " + clusterProvConfig
		}
		return cmd
	}

	return cmd
}

// sanitizeCommand strips flags and values for safe logging.
func sanitizeCommand(cmd string) string {
	tokens := strings.Fields(cmd)
	var kept []string
	for _, t := range tokens {
		if strings.HasPrefix(t, "--") {
			break
		}
		kept = append(kept, t)
	}
	return strings.Join(kept, " ")
}
