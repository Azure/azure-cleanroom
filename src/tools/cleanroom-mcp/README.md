# Cleanroom MCP

The Cleanroom MCP is a Model Context Protocol (MCP) server that enables AI
assistants to interact with Azure Clean Room resources. It serves as a bridge
between AI tools (like GitHub Copilot, Claude, and other MCP-compatible AI
assistants) and the `az cleanroom` CLI, translating natural language requests
into CLI commands and returning the results in a format the AI tools can
understand.

It allows AI tools to:

- Manage governance contracts, proposals, voting, secrets, and members
- Create, delete, and inspect CCF (Confidential Consortium Framework) networks
- Manage cleanroom clusters and generate workload deployments
- Configure cleanroom applications, data sources, identities, and networking
- Manage data stores, secret stores, telemetry, and logs
- Run collaboration workflows and Spark SQL queries

## How it works

Cleanroom MCP executes `az cleanroom` CLI commands on behalf of AI assistants,
similar to how [aks-mcp](https://github.com/Azure/aks-mcp) uses `az aks` for
AKS operations. It leverages the
[Model Context Protocol](https://modelcontextprotocol.io/) to facilitate
communication, enabling AI tools to invoke CLI commands and interpret the
responses.

```
┌──────────────────────┐
│  AI Assistant        │
│  (Copilot, Claude)   │
└──────────┬───────────┘
           │ MCP (stdio / SSE / streamable-http)
           ▼
┌──────────────────────┐
│  Cleanroom MCP       │
│  Server              │
└──────────┬───────────┘
           │ exec
           ▼
┌──────────────────────┐
│  az cleanroom CLI    │
│  (Azure CLI ext)     │
└──────────────────────┘
```

## Available Tools

The Cleanroom MCP server provides two tools:

### `call_az_cleanroom`

Unified tool for executing `az cleanroom` commands directly. This tool provides
a flexible interface to run any `az cleanroom` command.

**Parameters:**
- `cli_command` (required): The complete Azure CLI command to execute (must
  start with `az cleanroom`)
- `timeout` (optional): Timeout in seconds (default: 120)

**Available command groups:**

| Command Group | Description |
|---------------|-------------|
| `az cleanroom governance` | Contracts, proposals, voting, secrets, members, deployments, OIDC, CA |
| `az cleanroom ccf` | CCF networks, providers, recovery services, consortium managers |
| `az cleanroom cluster` | Clusters, providers, analytics and inferencing workload deployments |
| `az cleanroom config` | Configuration, applications, data sources, identities, networking |
| `az cleanroom datastore` | Data stores (add, upload, download, encrypt, decrypt) |
| `az cleanroom secretstore` | Secret stores |
| `az cleanroom telemetry` | Download and decrypt telemetry data |
| `az cleanroom logs` | Download and decrypt logs |
| `az cleanroom collaboration` | Collaboration contexts, identities, datasets, Spark SQL |

**Security:** Commands must start with `az cleanroom`. Shell features like
pipes (`|`), redirects (`>`), or command substitution (`$()`) are not allowed.

### `cleanroom_configure`

View or update the MCP server's runtime configuration. Use this tool to
dynamically set the client names that identify which governance, CCF provider,
and cluster provider instances to use — no server restart required.

**Parameters:**
- `operation` (required): `show` to display configuration, `set` to update
- `governance_client` (optional): Governance client name
- `ccf_provider_client` (optional): CCF provider client name
- `cluster_provider_client` (optional): Cluster provider client name
- `ccf_provider_config` (optional): Provider config JSON for CCF commands
  (CACI infra type)
- `cluster_provider_config` (optional): Provider config JSON for cluster
  commands (AKS infra type)

Client names correspond to Docker Compose project names and are automatically
injected into `az cleanroom` commands as `--governance-client` or
`--provider-client` flags. Provider configs are injected as
`--provider-config` when set.

## How to install

### Prerequisites

1. **Go** >= `1.24.x` installed on your machine.
2. **Azure CLI** with the `cleanroom` extension installed:
   ```bash
   az extension add --name cleanroom
   ```

### VS Code with GitHub Copilot (Recommended)

#### Step 1: Configure the MCP Server

Create a `.vscode/mcp.json` file in your workspace root (or use the existing
one from this repository):

```json
{
  "servers": {
    "cleanroom-mcp": {
      "type": "stdio",
      "command": "bash",
      "args": [
        "-c",
        "cd src/tools/cleanroom-mcp && go run ./cmd/cleanroom-mcp/ --transport stdio"
      ]
    }
  }
}
```

Alternatively, to use a pre-built binary:

```json
{
  "servers": {
    "cleanroom-mcp": {
      "type": "stdio",
      "command": "<path-to-binary>/cleanroom-mcp",
      "args": [
        "--transport",
        "stdio"
      ]
    }
  }
}
```

#### Step 2: Load the Cleanroom MCP Server Tools

1. Restart VS Code to load the new MCP server configuration.
2. Open GitHub Copilot in VS Code and
   [switch to Agent mode](https://code.visualstudio.com/docs/copilot/chat/chat-agent-mode).
3. Click the **Tools** button to see the list of available tools.
4. You should see `call_az_cleanroom` and `cleanroom_configure` in the list.

> **Tip**: If you don't see the Cleanroom MCP tools after restarting, check the
> VS Code output panel for any MCP server connection errors.

#### Step 3: Configure Client Names

Client names can be configured dynamically at runtime — no server restart
required. Ask the AI assistant to configure them:

```
Set the governance client to my-governance-client.

Update the CCF provider client to my-ccf-provider and the cluster provider
client to my-cluster-provider.

Show me the current cleanroom MCP configuration.
```

Alternatively, set defaults via environment variables in the `env` block of
`mcp.json`:

```json
"env": {
  "GOVERNANCE_CLIENT": "my-governance-client",
  "CCF_PROVIDER_CLIENT": "my-ccf-provider",
  "CLUSTER_PROVIDER_CLIENT": "my-cluster-provider"
}
```

### Other MCP-Compatible Clients

For other MCP-compatible AI clients like
[Claude Desktop](https://claude.ai/), configure the server in your MCP
configuration:

```json
{
  "mcpServers": {
    "cleanroom": {
      "command": "<path-to-binary>/cleanroom-mcp",
      "args": [
        "--transport", "stdio"
      ]
    }
  }
}
```

Once connected, use the `cleanroom_configure` tool to set client names
dynamically.

### Options

Command line arguments:

```
Usage of cleanroom-mcp:
      --transport string              Transport mechanism (stdio, sse or
                                      streamable-http) (default "stdio")
      --host string                   Host to listen on (only used with sse or
                                      streamable-http) (default "127.0.0.1")
      --port int                      Port to listen on (only used with sse or
                                      streamable-http) (default 8080)
      --timeout int                   Timeout for CLI command execution in
                                      seconds (default 120)
      --log-level string              Log level: debug, info, warn, error
                                      (default "info")
      --governance-client string      Name of the governance client
                                      (Docker Compose project name)
      --ccf-provider-client string    Name of the CCF provider client
                                      (default "ccf-provider")
      --cluster-provider-client string Name of the cluster provider client
                                      (default "cleanroom-cluster-provider")
      --ccf-provider-config string    Provider config JSON for CCF commands
                                      (CACI infra type)
      --cluster-provider-config string Provider config JSON for cluster
                                      commands (AKS infra type)
  -h, --help                          Show help message
```

**Environment variables:**

| Variable | Description | Default |
|----------|-------------|---------|
| `GOVERNANCE_CLIENT` | Name of the governance client (Docker Compose project) | *(none)* |
| `CCF_PROVIDER_CLIENT` | Name of the CCF provider client | `ccf-provider` |
| `CLUSTER_PROVIDER_CLIENT` | Name of the cluster provider client | `cleanroom-cluster-provider` |
| `CCF_PROVIDER_CONFIG` | Provider config JSON for CCF commands (CACI infra) | *(none)* |
| `CLUSTER_PROVIDER_CONFIG` | Provider config JSON for cluster commands (AKS infra) | *(none)* |

Environment variables provide initial defaults. Client names and provider
configs can also be updated at runtime using the `cleanroom_configure` tool —
no server restart required. Client names are automatically injected into the
appropriate `az cleanroom` commands as `--governance-client` or
`--provider-client` flags, and provider configs as `--provider-config`.

## Usage

Ask any questions about your cleanroom resources in your AI client, for
example:

```
List all my governance contracts.

Show me the details of contract abc-123.

What is the execution status for contract abc-123?

Create a new CCF network named my-network.

Check the health of CCF network my-network.

Show me all cleanroom cluster details for my-cluster.

Generate an analytics deployment for cluster my-cluster.

List all governance members.

What proposals are pending?

Vote to accept proposal xyz on contract abc-123.

Show me the cleanroom configuration.

Download telemetry data for my cleanroom.
```

## Development

### Prerequisites

- **Go** >= `1.24.x` installed on your local machine

### Building from Source

```bash
cd src/tools/cleanroom-mcp

# Build the binary
go build -o cleanroom-mcp ./cmd/cleanroom-mcp/

# Run with default settings
./cleanroom-mcp --transport stdio
```

### Project Structure

```
src/tools/cleanroom-mcp/
├── cmd/
│   └── cleanroom-mcp/
│       └── main.go              # Entry point
├── internal/
│   ├── command/
│   │   └── command.go           # CLI command executor and validation
│   ├── components/
│   │   ├── azcli/
│   │   │   └── azcli.go         # Unified call_az_cleanroom tool
│   │   └── configure/
│   │       └── configure.go     # Runtime configuration tool
│   ├── config/
│   │   └── config.go            # Configuration and CLI flags
│   ├── logger/
│   │   └── logger.go            # Logging wrapper
│   └── server/
│       └── server.go            # MCP server setup and transport
├── go.mod
├── go.sum
└── README.md
```

## Contributing

This project welcomes contributions and suggestions. Most contributions require
you to agree to a Contributor License Agreement (CLA) declaring that you have
the right to, and actually do, grant us the rights to use your contribution.
For details, visit https://cla.opensource.microsoft.com.

This project has adopted the
[Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the
[Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any
additional questions or comments.

## Trademarks

This project may contain trademarks or logos for projects, products, or
services. Authorized use of Microsoft trademarks or logos is subject to and must
follow
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/en-us/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must
not cause confusion or imply Microsoft sponsorship. Any use of third-party
trademarks or logos are subject to those third-party's policies.
