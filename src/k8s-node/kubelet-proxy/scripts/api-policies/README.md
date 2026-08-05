# Kubelet API Policies

Policy JSON files that define which kubelet REST APIs are allowed through the kubelet-proxy.

The full list of kubelet APIs is defined in the upstream Kubernetes source:
[`pkg/kubelet/server/server.go`](https://github.com/kubernetes/kubernetes/blob/master/pkg/kubelet/server/server.go)

## Policy Format

Each policy file is a JSON object with an `allowed_apis` array listing the kubelet API paths that the proxy will forward. Any API not in the list is rejected with `403 Forbidden`.

```json
{
  "allowed_apis": ["/pods", "/healthz", "/metrics"]
}
```

Matching rules:
- **Exact match**: `/pods` matches a request to `/pods`
- **Subpath match**: `/pods` also matches `/pods/some-pod`
- **Query match**: `/pods` also matches `/pods?watch=true`

## Available Policies

### `default-api-policy.json`

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

### `insecure-api-policy.json`

Extended policy for development and debugging. Adds `/containerLogs`, `/logs`, `/portForward`, and `/exec` to the default list. `/portForward` is required because tools like `kubectl port-forward` to services (e.g., for model inference testing) are routed through the kubelet's portForward API on the target node. `/exec` enables `kubectl exec` for debugging containers on the node.

| Additional APIs allowed |
|-------------------------|
| `/containerLogs` |
| `/logs` |
| `/portForward` |
| `/exec` |

## Usage

Pass the policy file to kubelet-proxy via the `--api-policy` flag:

```bash
kubelet-proxy --api-policy /path/to/default-api-policy.json ...
```

If `--api-policy` is not specified, the built-in default policy (equivalent to `default-api-policy.json`) is used.
