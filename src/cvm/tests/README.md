# CVM Attestation Tests

End-to-end tests for the CVM attestation agent and verifier running on an
Azure Confidential VM.

## Prerequisites

- Azure CLI (`az`) logged in with a subscription that can create VMs.
- Access to the `azcleanroompublickv` Key Vault (SSH keys are downloaded from it).
- Docker, `jq`, `openssl`, and `pwsh` installed locally.

## Quick start

### 1. Deploy a CVM

```powershell
./src/cvm/tests/deploy-cvm.ps1
```

- Auto-generate a VM name and resource group based on your `$env:USER`
  (e.g. `cvm-admin` / `rg-cvm-admin`) value.
- Download SSH keys from the `azcleanroompublickv` Key Vault.
- Create an Azure Confidential VM (`Standard_DC4as_v5`, Ubuntu 22.04 CVM).
- Write deployment info to `generated/cvm-deploy.json`.

### 2. Run the attestation test

```powershell
./src/cvm/tests/test-cvm-attestation-agent.ps1
```

The test script reads `generated/cvm-deploy.json` (produced by the deploy
step) to obtain the VM host and SSH key. It then:

1. Builds the `cvm-attestation-agent` and `cvm-attestation-verifier` Docker
   images locally.
2. Uploads the agent image to the CVM and runs it with TPM device access.
3. Calls `POST /snp/attest` on the agent, validates the response.
4. Runs the verifier locally and calls `POST /snp/verify` to verify the
   collected evidence.

Test artifacts are written to `generated/attest/`.

### 3. Clean up

Delete the resource group when you're done:

```powershell
az group delete --name rg-cvm-$env:USER --yes --no-wait
```