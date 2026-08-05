# Kubelet Proxy

The kubelet-proxy is a reverse proxy that sits between the Kubernetes API Server and the kubelet on a node. It intercepts API Server requests destined for the kubelet and enforces API-level policy before forwarding them.

The kubelet REST APIs are defined in the upstream Kubernetes source:
[`pkg/kubelet/server/server.go`](https://github.com/kubernetes/kubernetes/blob/master/pkg/kubelet/server/server.go)

Though there is no official documentation from kubernetes, a privately maintained repository documents the [kubelet APIs](https://github.com/cyberark/kubeletctl/blob/master/API_TABLE.md).
## Architecture

```
┌─────────────┐             ┌────────────────┐            ┌─────────────┐
│  API Server │──:10250──▶ │ kubelet-proxy  │──:10251──▶ │   Kubelet   │
└─────────────┘             └────────────────┘            └─────────────┘
```

The kubelet-proxy listens on port 10250 (where the API Server expects the kubelet). The real kubelet is moved to port 10251. The proxy uses self-signed TLS certificates:

- **Server certificate**: Used to serve HTTPS requests from the API Server
- **Client certificate**: Used to authenticate with the kubelet backend

## Features

- **OPA-Based API Policy Enforcement**: Allow/deny kubelet APIs using Rego policies with configurable JSON data files
- **SPDY Upgrade Support**: Handles SPDY connection upgrades for `exec` and `portForward` requests when allowed by policy
- **TLS Mutual Authentication**: Server cert for API Server, client cert for kubelet
- **Kubelet CA Trust**: The install script registers the proxy's client certificate with the kubelet's trusted CA bundle so the kubelet accepts proxied connections
- **Request Logging**: Detailed logging of all proxied requests and policy decisions
- **Graceful Shutdown**: Handles SIGINT/SIGTERM for clean shutdown

### Insecure Mode

When deployed with the insecure API policy (`insecure-api-policy.json`), the proxy additionally allows `/containerLogs`, `/logs`, and `/portForward`. The `/portForward` API is needed because `kubectl port-forward` to services is routed through the kubelet's portForward endpoint on the target node.

## Usage

```bash
./bin/kubelet-proxy \
  --kubelet-url https://127.0.0.1:10251 \
  --listen-addr :10250 \
  --server-cert /path/to/server.crt \
  --server-key /path/to/server.key \
  --client-cert /path/to/client.crt \
  --client-key /path/to/client.key \
  --ca-cert /path/to/ca.crt \
  --api-policy /path/to/api-policy.json \
  --log-requests
```

## Command Line Options

| Option | Default | Description |
|--------|---------|-------------|
| `--kubelet-url` | (required) | URL of the kubelet backend (e.g., `https://127.0.0.1:10251`) |
| `--listen-addr` | `:10250` | Address to listen on for API Server connections |
| `--server-cert` | (required) | Path to TLS certificate for serving API Server requests |
| `--server-key` | (required) | Path to TLS key for serving API Server requests |
| `--client-cert` | (required) | Path to TLS client certificate for connecting to kubelet |
| `--client-key` | (required) | Path to TLS client key for connecting to kubelet |
| `--ca-cert` | | Path to CA certificate for verifying API Server client certs (optional) |
| `--api-policy` | | Path to JSON file listing allowed kubelet APIs (optional, uses built-in defaults if empty) |
| `--log-requests` | `true` | Log all proxied requests |

## Kubelet API Policy

The proxy uses OPA (Open Policy Agent) with a Rego policy to evaluate whether each kubelet API request is allowed. The list of allowed API paths is loaded from a JSON file at startup via the `--api-policy` flag. If no file is specified, a built-in default list is used.

### How It Works

1. On startup, the proxy loads the `allowed_apis` list from the JSON policy file (or uses built-in defaults)
2. The list is loaded into an OPA in-memory store as data
3. For each incoming request, the Rego policy checks if the request URI matches any allowed API path
4. Matching supports exact path, subpath (`/pods/foo`), and query parameter (`/pods?watch=true`) variants
5. If no match is found, the request is rejected with `403 Forbidden`

### Policy File Format

The policy file is a JSON object with an `allowed_apis` array:

```json
{
  "allowed_apis": [
    "/pods",
    "/metrics",
    "/healthz"
  ]
}
```

### Available Policy Files

Pre-built policy files are provided in [`scripts/api-policies/`](scripts/api-policies/):

#### `default-api-policy.json`

The default production policy. Allows safe monitoring and read APIs. Blocks all debug and interactive APIs.

| Allowed | Blocked |
|---------|---------|
| `/pods` | `/exec` |
| `/metrics` | `/run` |
| `/metrics/cadvisor` | `/attach` |
| `/metrics/resource` | `/portForward` |
| `/metrics/probes` | `/containerLogs` |
| `/stats` | `/logs` |
| `/checkpoint` | `/runningpods` |
| `/healthz` | `/debug/pprof` |
| | `/debug/flags/v` |

#### `debug-api-policy.json`

Extended policy for debugging and troubleshooting. Adds `/containerLogs` and `/logs` to the default list, enabling container and node log access while still blocking interactive APIs.

| Additional APIs allowed |
|-------------------------|
| `/containerLogs` |
| `/logs` |

### Built-in Default Policy

When `--api-policy` is not specified, the proxy uses a built-in default equivalent to `default-api-policy.json`. This ensures the proxy works out-of-the-box without requiring an external file.

## Deployment

On the node:

1. Move the kubelet to listen on port 10251 (e.g., `--port=10251`)
2. Generate self-signed server and client certificates
3. Start kubelet-proxy with the certificates, pointing to the kubelet on port 10251
4. The API Server continues to connect to port 10250, now served by kubelet-proxy

## Building

```bash
# Build the binary
make build

# Run unit tests
make test

# Build release artifacts
make release
```

## Testing with Kind

```bash
# Deploy to a kind cluster (creates cluster if needed)
make deploy-kind

# Run kubelet API policy tests
make test-kind

# Tear down cluster
make teardown-kind
```

## Testing with AKS

```bash
# Deploy AKS cluster, flex node VM, and kubelet-proxy
make deploy-aks

# Run kubelet API policy tests
make test-aks

# Tear down cluster
make teardown-aks
```
