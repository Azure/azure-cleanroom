# cvm-attestation-agent

REST API server that collects attestation evidence from an Azure Confidential VM (CVM). It reads the AMD SNP report, HCL report, TPM quote, AIK certificate, and PCR values from the local vTPM, and returns them as a single JSON payload suitable for verification by the `cvm-attestation-verifier`.

This service must run on a CVM with access to the TPM device (`/dev/tpmrm0`).

## Build

```powershell
# Using the build script (produces a Docker image)
pwsh build/cvm/build-cvm-attestation-agent.ps1
```

### Docker

The container requires access to the TPM device:

```bash
docker run -d \
    --name cvm-attestation-agent \
    --device /dev/tpmrm0:/dev/tpmrm0 \
    -p 8900:8900 \
    cvm/cvm-attestation-agent:latest
```

## API

### `POST /snp/attest`

Collects attestation evidence from the local CVM hardware and returns it as JSON.

#### Request

```json
{
  "reportData": "<base64>",
  "nonce": "<base64>",
  "pcrSelection": [0, 1, 2, 7]
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `reportData` | string | Yes | Base64-encoded 64-byte caller report data payload. The agent wraps this into a user data document and hashes the document into the SNP `report_data`. |
| `nonce` | string | Yes | Base64-encoded nonce for the TPM quote's `extraData` field. Maximum 32 bytes. |
| `pcrSelection` | int[] | No | List of PCR indices (0–23) to include in the quote. Defaults to all 24 PCRs if omitted. |

#### Response (200 OK)

```json
{
  "vtpm": {
    "evidence": {
      "tpmQuote": "<base64>",
      "hclReport": "<base64>",
      "snpReport": "<base64>",
      "aikCert": "<base64>",
      "pcrs": { "0": "<base64>", "...": "..." },
      "runtimeClaims": { "keys": [...], "vm-configuration": {...}, "user-data": "..." }
    },
    "nonce": "<base64>",
    "platformCertificates": "<PEM>",
    "imageReference": { "publisher": "...", "offer": "...", "sku": "...", "version": "..." }
  },
  "gpu": {
    "evidences": [{
      "evidence": "<base64>",
      "certificate": "<base64>",
      "arch": "...",
      "nonce": "..."
    }]
  },
  "userDataDocument": "<base64>"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `vtpm` | object | vTPM/SNP attestation evidence, nonce, platform certificates, and VM image reference. |
| `vtpm.evidence.tpmQuote` | string | Base64-encoded TPM quote blob (TPMS_ATTEST + TPMT_SIGNATURE). |
| `vtpm.evidence.hclReport` | string | Base64-encoded HCL report blob from vTPM NVRAM. |
| `vtpm.evidence.snpReport` | string | Base64-encoded 1184-byte AMD SNP attestation report. |
| `vtpm.evidence.aikCert` | string | Base64-encoded AIK x.509 certificate (DER). |
| `vtpm.evidence.pcrs` | object | SHA256 PCR values as `{ "index": "<base64 digest>", ... }`, keys sorted numerically. |
| `vtpm.evidence.runtimeClaims` | object | Parsed runtime claims from the HCL report. Contains `keys`, `vm-configuration`, and `user-data`. |
| `vtpm.platformCertificates` | string | PEM-encoded AMD cert chain (ARK, ASK, VCEK) from THIM. |
| `vtpm.imageReference` | object | VM image reference (publisher, offer, SKU, version) from IMDS. |
| `gpu` | object? | GPU attestation evidence, present only when `/dev/nvidia*` devices are detected. |
| `gpu.evidences` | array | One entry per GPU with SPDM report, certificate chain, architecture, and nonce. |
| `userDataDocument` | string | Base64-encoded canonical JSON user data document whose SHA-256 hash is bound to the runtime claims `user-data[0:32]`. |

#### Error Response

```json
{
  "error": {
    "code": "MissingReportData",
    "message": "reportData is required"
  }
}
```

| Error Code | HTTP Status | Description |
|------------|-------------|-------------|
| `InvalidRequestBody` | 400 | Malformed JSON body. |
| `MissingReportData` | 400 | `reportData` field is missing. |
| `InvalidReportData` | 400 | `reportData` is not valid base64. |
| `InvalidReportDataSize` | 400 | `reportData` is not exactly 64 bytes. |
| `MissingNonce` | 400 | `nonce` field is missing. |
| `InvalidNonce` | 400 | `nonce` is not valid base64. |
| `InvalidNonceSize` | 400 | `nonce` exceeds 32 bytes. |
| `InvalidPCRSelection` | 400 | `pcrSelection` contains values outside 0–23. |
| `AttestationFailed` | 500 | Failed to collect attestation evidence or prepare report_data. |
| `GpuAttestationFailed` | 500 | GPU count detection or GPU evidence collection failed. |
