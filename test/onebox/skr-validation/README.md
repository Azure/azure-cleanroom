# CACI SKR Validation Test

Validates that Azure CACI's **Secure Key Release (SKR)** mechanism works correctly. If the CACI platform makes a change that breaks attestation or the SKR sidecar, this test will catch it.

## What It Does

Deploys a minimal **Confidential ACI** container group with two containers (both using the same Microsoft SKR image `mcr.microsoft.com/aci/skr:2.12`):

| Container | Entrypoint | Role |
|-----------|-----------|------|
| `skr-sidecar` | `/skr.sh` | SKR HTTP server on port 8284. Handles `POST /key/release` by performing SEV-SNP attestation → MAA token → KV key release |
| `skr-test-client` | `/tests/skr/skr_client.sh` | Calls `POST http://localhost:8284/key/release` with the KV endpoint, MAA endpoint, and key name |

No custom code runs in CACI — it's entirely Microsoft's official SKR image. No CCF, CGS, governance, OIDC, or cleanroom infrastructure needed.

## What It Validates

The `POST /key/release` call exercises the **entire SKR chain**:

```
CACI container group boots on SEV-SNP hardware
  → SKR sidecar gets attestation report from /dev/sev-guest
  → Sends to MAA (Microsoft Azure Attestation)
  → MAA validates and returns token with claims:
      x-ms-sevsnpvm-hostdata = CCE policy hash
      x-ms-compliance-status = azure-compliant-uvm
      x-ms-attestation-type  = sevsnpvm
  → SKR sidecar presents MAA token to Key Vault
  → KV checks token against SKR release policy
  → Key released (or rejected) → PASS / FAIL
```

If CACI changes anything that affects attestation, UVM compliance, or the SKR sidecar, the key release fails and the test catches it.

## Prerequisites

- Azure CLI
- Azure subscription with Confidential ACI capability
- Logged into Azure (`az login`)
- Docker (needed for `az confcom acipolicygen` during one-time setup)

## How to Run

### 1. One-Time Setup

Creates a permanent resource group with a Key Vault (Premium SKU), Managed Identity, generates the CCE policy from the ARM template, and imports a key with an SKR release policy:

```bash
pwsh test/onebox/skr-validation/setup-resources.ps1
```

Resources created in `cl-skr-validation-rg`:
- **Key Vault** (Premium, RBAC auth, purge protection) — stores keys with SKR release policies
- **Managed Identity** — attached to the CACI container group, has `Key Vault Crypto Officer` role
- **HSM-backed key** (`skr-test-key`) — with SKR release policy requiring the exact CCE policy hash
- **CCE Policy** — generated via `az confcom acipolicygen` and saved to `resources.json`

### 2. Run the Test

```bash
pwsh test/onebox/skr-validation/run-skr-validation.ps1
```

This script:
1. Deploys the CACI container group using the ARM template with the **generated CCE policy** passed as a parameter
2. Polls container logs for key release output (`{"key":"..."}`)
3. Reports PASS/FAIL based on successful key release

The CCE policy and KV key are pre-provisioned (by `setup-resources.ps1`), so no policy generation or key creation happens per run.

**Custom parameters:**
```bash
pwsh run-skr-validation.ps1 -location uksouth
```

### 3. Cleanup

```bash
# Delete the CACI container group (preserves KV resources):
pwsh test/onebox/skr-validation/cleanup.ps1

# Delete everything including the resource group:
pwsh test/onebox/skr-validation/cleanup.ps1 -deleteResourceGroup
```

## Files

| File | Purpose |
|------|---------|
| `aci-arm-template.json` | ARM template with parameterized CCE policy: SKR sidecar + test client, Confidential SKU |
| `setup-resources.ps1` | One-time: creates RG, KV Premium, MI, generates CCE policy, imports key with SKR policy |
| `run-skr-validation.ps1` | Deploys CACI container group with CCE policy → checks logs for key release |
| `cleanup.ps1` | Delete CACI container group, optionally delete RG |

## CI Integration

This test runs as part of the nightly BVT workflow (`release-and-test.yml`) as the `bvt-skr-validation` job. It uses pre-provisioned resources in `cl-skr-validation-eus-rg` (UK South), created by running `setup-resources.ps1` once.

The file `generated/cl-skr-validation-eus-rg/resources.json` is checked in for CI. This gives each workflow run a stable `ccePolicy` input even though the runner workspace is ephemeral.

Expected setup behavior in CI:
- If resources in `cl-skr-validation-eus-rg` are healthy (KV, MI, key, role assignment), `setup-resources.ps1` skips setup work.
- If resources are missing or drifted, `setup-resources.ps1` performs repair/provisioning and rewrites `resources.json`.

## How It Differs From the Full Cleanroom Test

The existing BVT tests (e.g., `nginx-hello-caci`, `ml-training-caci`) deploy the **full cleanroom stack**: CCF governance, CGS, contracts, OIDC federation, blobfuse, code-launcher, proxy, etc. Those tests validate the entire cleanroom E2E flow.

This test is **SKR-only** — it strips away everything except the core attestation → key release mechanism. It's faster, has no dependencies on cleanroom container builds, and isolates CACI platform regressions from cleanroom-specific issues.

## Notes

- The CCE policy is **generated during setup** via `az confcom acipolicygen` and stored in `resources.json`. If the ARM template containers change (e.g., new image version), re-run setup to regenerate the policy.
- For the nightly CI resource group (`cl-skr-validation-eus-rg`), `resources.json` is checked in at `generated/cl-skr-validation-eus-rg/resources.json` so the policy is available immediately on each run.
- The Microsoft `skr_client.sh` test script runs in a loop. The test detects success by scanning container logs for `{"key":"..."}` output.
- All images come from MCR — no container builds required.
