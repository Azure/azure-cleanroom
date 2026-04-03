# Confidential Nodes

A collection of Go binaries for enabling confidential nodes on [AKS Flex Node](https://github.com/Azure/AKSFlexNode).

## api-server-proxy

The api-server-proxy is a binary that runs on a Kubernetes node, intercepting HTTP traffic between the kubelet and the API server. It watches for pod assignments and allows you to inspect, accept, or reject pods before they are created on the node.


## Testing with Kind

Deploy api-server-proxy to a kind cluster:

```bash
# Deploy to kind cluster (2 nodes: control-plane + worker)
# This generates signing keys locally via policy-signing-tool.sh
make deploy-kind

# Run pod policy verification tests
make test-kind

# Tear down cluster
make teardown-kind
```

The kind deployment:
- Creates a 2-node cluster (control-plane + worker)
- Generates signing keys locally using `policy-signing-tool.sh`
- Installs api-server-proxy on the worker node with pod policy verification enabled
- Configures kubelet to route through the proxy

## Testing with AKS

Deploy api-server-proxy to an AKS cluster with an AKS Flex Node VM:

```bash
# Deploy AKS cluster and flex node with api-server-proxy
make deploy-aks

# Run pod policy verification tests
make test-aks
```

The AKS deployment:
- Creates an AAD-enabled AKS cluster (no Azure RBAC)
- Deploys an Ubuntu 22.04 Confidential VM (Standard_DC4as_v5) with vTPM and secure boot
- Creates a single managed identity (kubelet-identity) with Owner role on the AKS cluster
- Sets up Kubernetes RBAC (system:node-bootstrapper, system:node) for the managed identity
- Joins the VM to the AKS cluster as a flex node using [AKS Flex Node](https://github.com/Azure/AKSFlexNode) v0.0.10
- Generates signing keys locally using `policy-signing-tool.sh`
- Installs api-server-proxy with pod policy verification enabled
- Adds a taint `pod-policy=required:NoSchedule` to the node for policy-required workloads
- Signs and deploys test pods using `policy-signing-tool.sh`

### Architecture

```
┌─────────────┐      ┌──────────────┐          ┌─────────────┐
│  API Server │ ←──→ │ api-server-proxy │ ←──→ │   Kubelet   │
└─────────────┘      └──────────────┘          └─────────────┘
                            │
                            ▼
                     Pod Policy Verification
                     (accept/reject pod)
```

The kubelet connects to api-server-proxy (thinking it's the API server). The proxy:
1. Forwards all requests to the real API server
2. Intercepts pod watch/list **responses** from the API server
3. Verifies cryptographic signatures on each pod
4. For rejected pods:
   - Patches the pod status to `Failed` via the API server (Kubernetes-native rejection)
   - Filters the pod from the response so kubelet doesn't attempt to run it
5. The pod shows as `Failed` with a clear error message in `kubectl get pods`

### Pod Rejection Flow

When a pod is rejected, the proxy uses the **Kubernetes-native rejection pattern** (same as how kubelet reports failures):

```yaml
# Pod status after rejection
status:
  phase: Failed
  reason: NodeAdmissionRejected
  message: "Pod rejected by api-server-proxy: <policy reason>"
  conditions:
    - type: Ready
      status: "False"
      reason: NodeAdmissionRejected
```

This approach:
- ✅ Pod moves to `Failed` state - clear visibility
- ✅ Scheduler does not retry the pod
- ✅ User sees a clear error message via `kubectl describe pod`
- ✅ Cluster stays healthy
- ✅ Standard Kubernetes behavior

### Features

- **Kubernetes-Native Rejection**: Rejects pods by patching status to Failed (same as kubelet)
- **Response Interception**: Intercepts pod list/watch responses from the API server
- **Pod Policy Verification**: Cryptographic verification of pod policy signatures
- **Kubeconfig Support**: Uses standard kubeconfig file for API server connection
- **Watch Stream Support**: Properly handles Kubernetes watch streams
- **Logging**: Detailed logging of all requests and admission decisions
- **TLS Support**: Full TLS support for secure communication
- **Graceful Shutdown**: Handles signals for clean shutdown

### Usage

```bash
./bin/api-server-proxy \
  --kubeconfig /path/to/kubeconfig \
  --listen-addr :6443 \
  --tls-cert /path/to/server.crt \
  --tls-key /path/to/server.key \
  --policy-verification-cert /path/to/signing-cert.pem \
  --log-requests
```

### Deployment

On the node, configure the kubelet to connect to api-server-proxy instead of the API server directly:

1. Start api-server-proxy with the node's kubeconfig file
2. Update kubelet configuration to point to api-server-proxy's address
3. api-server-proxy forwards traffic to the real API server

### Command Line Options

| Option | Default | Description |
|--------|---------|-------------|
| `--kubeconfig` | (required) | Path to kubeconfig file for API server connection |
| `--context` | | Context to use from kubeconfig (uses current-context if empty) |
| `--listen-addr` | `:6443` | Address to listen on for kubelet connections |
| `--tls-cert` | | Path to TLS certificate for serving |
| `--tls-key` | | Path to TLS key for serving |
| `--policy-verification-cert` | | Path to certificate for pod policy verification |
| `--log-requests` | `true` | Log all proxied requests |
| `--log-pod-payloads` | `false` | Log full pod JSON payloads |

### Pod Policy Verification

api-server-proxy can verify cryptographic signatures on pod policies to ensure only authorized workloads run on a node. When enabled, pods must have a valid policy and signature annotation or they will be rejected.

#### How It Works

1. A **policy JSON** is created from security-relevant fields of the pod spec (image, command, env vars, volume mounts, security context) — not the full pod spec
2. The policy JSON bytes are hashed with SHA-256
3. The hash is signed with the private key (RSA-PSS or ECDSA)
4. The base64-encoded policy is stored in the `api-server-proxy.io/policy` annotation
5. The base64-encoded signature is stored in the `api-server-proxy.io/signature` annotation
6. On the node, api-server-proxy:
   - Decodes the policy from base64
   - Hashes the decoded policy bytes with SHA-256
   - Verifies the signature against the hash using the configured signing certificate
   - Validates that the actual pod spec matches the claimed policy

#### Signing Pods

Pods are signed by generating a policy JSON from the pod spec, base64-encoding it, and signing it via the `policy-signing-tool.sh` script:

```bash
# Generate signing keys (one-time setup)
./scripts/policy-signing-tool.sh generate

# Sign a base64-encoded policy
./scripts/policy-signing-tool.sh sign "<base64-encoded-policy>"

# Get the signing certificate path
./scripts/policy-signing-tool.sh cert
```

The signing tool uses RSA-PSS (SHA-256) via `openssl` — no HTTP server required.

#### Policy Schema

Instead of signing the full pod spec (which changes when Kubernetes adds defaults), api-server-proxy uses a **policy-based approach**. Security-relevant fields are extracted from the pod spec into a deterministic policy JSON, which is then signed and verified.

The policy is an **array of container policies**, where each container is identified by name, allowing precise verification that each container in the pod matches its signed policy.

##### Policy Structure

The policy is a JSON array of container policy objects:

```json
[
  {
    "name": "<container-name>",
    "properties": {
      "image": "<image>",
      "command": ["cmd", "arg1"],
      "environmentVariables": [{"name": "VAR", "value": "val"}],
      "volumeMounts": [{"name": "vol", "mountPath": "/mnt", "readOnly": true}],
      "privileged": false,
      "capabilities": ["NET_ADMIN"]
    }
  }
]
```

##### Container Policy Fields

Each container policy object has:

| Field | Type | Description |
|-------|------|-------------|
| `name` | `string` | The container name (must match the pod spec container name) |
| `properties` | `object` | Container properties to verify |

##### Container Properties Fields

| Field | Type | Description |
|-------|------|-------------|
| `image` | `string` | The container image (must match exactly) |
| `command` | `string[]` | Container entrypoint array (overrides ENTRYPOINT) |
| `environmentVariables` | `object[]` | Environment variables with `name`, `value`, and optional `regex` fields |
| `volumeMounts` | `object[]` | Volume mounts with `name`, `mountPath`, `mountType`, and `readOnly` fields |
| `privileged` | `boolean` | Whether the container can run as privileged |
| `capabilities` | `string[]` | Linux capabilities to add |

##### Environment Variable Fields

| Field | Type | Description |
|-------|------|-------------|
| `name` | `string` | Environment variable name |
| `value` | `string` | Environment variable value |
| `regex` | `boolean` | If true, value is treated as a regex pattern |

##### Volume Mount Fields

| Field | Type | Description |
|-------|------|-------------|
| `name` | `string` | Volume name |
| `mountPath` | `string` | Path where the volume is mounted |
| `mountType` | `string` | Optional volume type |
| `readOnly` | `boolean` | Whether the mount is read-only |

##### Example Policy

For a pod with:
```yaml
spec:
  containers:
    - name: app
      image: busybox:latest
      command: ["/bin/myapp"]
      env:
        - name: APP_ENV
          value: "production"
        - name: LOG_LEVEL
          value: "debug"
      volumeMounts:
        - name: config
          mountPath: /etc/app
          readOnly: true
        - name: data
          mountPath: /data
```

The policy would be:
```json
[
  {
    "name": "app",
    "properties": {
      "image": "busybox:latest",
      "command": ["/bin/myapp"],
      "environmentVariables": [
        {"name": "APP_ENV", "value": "production"},
        {"name": "LOG_LEVEL", "value": "debug"}
      ],
      "volumeMounts": [
        {"name": "config", "mountPath": "/etc/app", "readOnly": true},
        {"name": "data", "mountPath": "/data", "readOnly": false}
      ]
    }
  }
]
```

##### Simple Policy Example

For a basic nginx pod:
```yaml
spec:
  containers:
    - name: test
      image: nginx:latest
```

The policy would be:
```json
[
  {
    "name": "test",
    "properties": {
      "image": "nginx:latest",
      "command": [],
      "environmentVariables": [],
      "volumeMounts": []
    }
  }
]
```

##### Verification Behavior

During admission, api-server-proxy verifies:
1. **Container name matching**: Every container in the pod must have a corresponding entry in the policy (by name)
2. **Image matching**: Each container's image must match its policy entry
3. **Command matching**: `command` must match exactly per container
4. **Environment variables matching**: Environment variables must match (supports regex matching)
5. **Volume mounts matching**: Volume mounts must match by name, mountPath, and readOnly flag
6. **Security context matching**: `privileged` and `capabilities` must match exactly per container

##### Special "allowall" Policy

A special policy value `["allowall"]` can be used to bypass all pod validation. When signed and provided as the policy annotation, api-server-proxy will allow the pod without checking any container properties. This is useful for trusted workloads or debugging scenarios.

```yaml
metadata:
  annotations:
    # Base64-encoded ["allowall"] = WyJhbGxvd2FsbCJd
    api-server-proxy.io/policy: "WyJhbGxvd2FsbCJd"
    api-server-proxy.io/signature: "<signature-of-the-allowall-policy>"
```

**Warning**: The `allowall` policy still requires a valid signature, ensuring only authorized signers can bypass validation.

##### Viewing a Policy

#### Signature Annotation

The policy and signature are stored in pod annotations:

```yaml
metadata:
  annotations:
    api-server-proxy.io/policy: "eyJhbGxvd2VkSW1hZ2VzIjpbIm5naW54OmxhdGVzdCJdfQ=="  # base64-encoded policy JSON
    api-server-proxy.io/signature: "MEUCIQDx...base64-encoded-signature..."           # signature of the policy
```

The api-server-proxy verifies pods by:
1. Extracting the `api-server-proxy.io/policy` annotation (base64-encoded policy)
2. Extracting the `api-server-proxy.io/signature` annotation
3. Verifying the signature against the policy using the configured signing certificate
4. Optionally validating that the pod spec matches the claimed policy

#### Supported Key Types

- **RSA-PSS** (recommended): RSA with PSS padding and SHA-256
- **ECDSA**: P-256, P-384, P-521 curves (legacy support)

## policy-signing-tool.sh

The `policy-signing-tool.sh` test script uses `openssl` CLI to generate RSA-2048 signing keys and sign payloads using RSA-PSS with SHA-256 — no HTTP server required.

### Commands

| Command | Description |
|---------|-------------|
| `generate` | Generate RSA-2048 key pair and self-signed certificate |
| `sign <base64-payload>` | Sign a base64-encoded payload, output base64 signature |
| `cert` | Print the path to the signing certificate PEM file |
