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

The test script (`scripts/kind/test-pod-policies.sh`) loads these policy files and:

1. Compacts the JSON (removes whitespace, sorts keys)
2. Base64-encodes the compacted JSON
3. Signs the base64 string using `policy-signing-tool.sh`
4. Creates pod YAML with the policy and signature as annotations

## Test Scenarios

| Test | Policy Used | Pod Spec | Expected |
|------|-------------|----------|----------|
| TEST 1: Signed pod | `nginx-pod-policy.json` | nginx:latest (matches) | ALLOWED |
| TEST 2: Unsigned pod | (none) | nginx:latest | REJECTED |
| TEST 3: Bad signature | `nginx-pod-policy.json` | nginx:latest (invalid sig) | REJECTED |
| TEST 4: Image mismatch | `nginx-pod-policy.json` | busybox:latest (mismatch) | REJECTED |
| TEST 5: Full policy pod | `full-policy-pod-policy.json` | All fields match | ALLOWED |
| TEST 6: Command mismatch | `full-policy-pod-policy.json` | command: /bin/sh (expects /bin/myapp) | REJECTED |
| TEST 7: Env mismatch | `full-policy-pod-policy.json` | APP_ENV=development (expects production) | REJECTED |
| TEST 8: Volume mismatch | `full-policy-pod-policy.json` | mountPath: /etc/config (expects /etc/app) | REJECTED |

## Running Tests

```bash
# From project root
make test-kind

# Or directly
./scripts/kind/test-pod-policies.sh
```
