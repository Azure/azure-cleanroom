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
| `reportData` | string | Yes | Base64-encoded 64-byte value to embed in the SNP report's `report_data` field. Eg. `SHA256(public_key_PEM_UTF8) \|\| 32_zero_bytes`. |
| `nonce` | string | Yes | Base64-encoded nonce for the TPM quote's `extraData` field. Maximum 32 bytes. |
| `pcrSelection` | int[] | No | List of PCR indices (0–23) to include in the quote. Defaults to all 24 PCRs if omitted. |

#### Response (200 OK)

```json
{
  "tpmQuote": "<base64>",
  "hclReport": "<base64>",
  "snpReport": "<base64>",
  "aikCert": "<base64>",
  "pcrs": {
    "0": "<base64>",
    "1": "<base64>",
    "...": "..."
  },
  "runtimeClaims": {
    "keys": [ ... ],
    "vm-configuration": { ... },
    "user-data": "..."
  }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `tpmQuote` | string | Base64-encoded TPM quote blob (TPMS_ATTEST + TPMT_SIGNATURE). |
| `hclReport` | string | Base64-encoded HCL report blob from vTPM NVRAM. |
| `snpReport` | string | Base64-encoded 1184-byte AMD SNP attestation report. |
| `aikCert` | string | Base64-encoded AIK x.509 certificate (DER). |
| `pcrs` | object | SHA256 PCR values as `{ "index": "<base64 digest>", ... }`, keys sorted numerically. |
| `runtimeClaims` | object | Parsed runtime claims extracted from the HCL report. Contains `keys` (JWK array with HCLAkPub and HCLEkPub), `vm-configuration`, and `user-data`. |

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
| `AttestationFailed` | 500 | Failed to collect attestation evidence from hardware. |
