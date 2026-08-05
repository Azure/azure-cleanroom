# Test Policies for Pod Policy Verification

This directory contains policy JSON files used for testing api-server-proxy pod policy verification.

## Policy Files

| File | Description | Used In Test |
|------|-------------|--------------|
| `nginx-pod-policy.json` | Policy allowing nginx:latest container on pod-policy nodes | Signed pod test (TEST 1), Bad signature test (TEST 3), Image mismatch test (TEST 4) |
| `busybox-pod-policy.json` | Policy allowing busybox:latest with sleep command | Reference only |
| `full-policy-pod-policy.json` | Policy with command, args, env, and volumeMounts | Full policy test (TEST 5), Command mismatch (TEST 6), Env mismatch (TEST 7), Volume mismatch (TEST 8) |

## Policy Schema

Each policy JSON follows the per-container structure:

```json
{
  "containers": {
    "<container-name>": {
      "image": "<image>",
      "command": ["cmd"],           // optional
      "args": ["arg1", "arg2"],     // optional
      "env": [{"name": "X", "value": "Y"}], // optional
      "volumeMounts": [{"name": "vol", "mountPath": "/mnt"}], // optional
      "privileged": true,           // optional
      "capabilities": ["CAP_NAME"]  // optional
    }
  },
  "initContainers": { ... },        // optional
  "allowHostNetwork": true,         // optional
  "allowHostPID": true,             // optional
  "allowHostIPC": true,             // optional
  "nodeSelector": { "key": "value"} // optional
}
```

## How Policies Are Used in Tests

The Go integration tests (`test/integration/pod_policy_test.go`) load these policy
files and:

1. Compact the JSON (removes whitespace, sorts keys)
2. Base64-encode the compacted JSON
3. Sign the base64 string using Go's `crypto/rsa` (RSA-PSS SHA-256)
4. Create pod YAML with the policy and signature as annotations

## Test Scenarios

| Test | Policy Used | Pod Spec | Expected |
|------|-------------|----------|----------|
| SignedPod_Allowed | `busybox-pod-policy.json` | busybox:latest (matches) | ALLOWED |
| UnsignedPod_Rejected | (none) | busybox:latest | REJECTED |
| BadSignature_Rejected | `busybox-pod-policy.json` | busybox:latest (invalid sig) | REJECTED |
| ImageMismatch_Rejected | `busybox-pod-policy.json` | nginx:latest (mismatch) | REJECTED |
| FullPolicy_Allowed | `full-policy-pod-policy.json` | All fields match | ALLOWED |
| CommandMismatch_Rejected | `full-policy-pod-policy.json` | command: /bin/sh (expects /bin/myapp) | REJECTED |
| EnvMismatch_Rejected | `full-policy-pod-policy.json` | APP_ENV=development (expects production) | REJECTED |
| VolumeMismatch_Rejected | `full-policy-pod-policy.json` | mountPath: /etc/config (expects /etc/app) | REJECTED |
| FakeK8sMount_Rejected | `nginx-pod-policy.json` | writable kube-api-access mount | REJECTED |
| Insecure_Allowed | (none) | unsigned pod, --insecure mode | ALLOWED |

## Running Tests

```bash
# From src/k8s-node/
make test-integration-kind    # Kind cluster
make test-integration-aks     # AKS cluster

# Or run just pod policy tests
go test -tags "kind,integration" -v -timeout 30m \
  -run "TestPodPolicy" ./test/integration/
```
