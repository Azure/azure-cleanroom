# cvm-attestation-verifier

REST API server that verifies attestation evidence produced by the `cvm-attestation-agent`'s `/snp/attest` endpoint. It validates the full Azure CVM trust chain including vTPM/SNP, user data document binding, and optional GPU attestation.

## Build

```powershell
# Using the build script (produces a Docker image)
pwsh build/cvm/build-cvm-attestation-verifier.ps1
```

## API

### `POST /snp/verify`

Verifies attestation evidence and returns per-check results.

#### Request

```json
{
  "vtpm": {
    "evidence": {
      "tpmQuote": "<base64>",
      "hclReport": "<base64>",
      "snpReport": "<base64>",
      "aikCert": "<base64>",
      "pcrs": { "0": "<base64>", "...": "..." }
    },
    "nonce": "<base64 or raw string>",
    "product": "Milan",
    "platformCertificates": "<PEM>"
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

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `vtpm.evidence.tpmQuote` | string | Yes | Base64-encoded TPM quote blob (TPM2B_ATTEST + TPMT_SIGNATURE). |
| `vtpm.evidence.hclReport` | string | Yes | Base64-encoded HCL report blob from vTPM NVRAM. Runtime claims (including HCLAkPub) are parsed from this automatically. |
| `vtpm.evidence.snpReport` | string | Yes | Base64-encoded 1184-byte AMD SNP attestation report. |
| `vtpm.evidence.aikCert` | string | No | Base64-encoded AIK x.509 certificate (DER). |
| `vtpm.evidence.pcrs` | object | Yes | SHA256 PCR values as `{ "index": "<base64 digest>", ... }`. |
| `vtpm.nonce` | string | Yes | Expected TPM quote nonce. Base64-encoded bytes or a raw string. |
| `vtpm.product` | string | No | AMD product name for certificate lookup. Auto-detected from ARK CN if omitted. |
| `vtpm.platformCertificates` | string | Yes | PEM-encoded AMD cert chain (VCEK, ASK, ARK) from THIM. |
| `gpu` | object | No | GPU attestation evidence, present only when GPUs are available. |
| `gpu.evidences` | array | Yes (if gpu) | One entry per GPU with SPDM report, certificate chain, architecture, and nonce. |
| `userDataDocument` | string | Yes | Base64-encoded canonical JSON user data document whose SHA-256 hash is bound to user-data[0:32]. |

#### Response (200 OK)

```json
{
  "verified": true,
  "checks": [
    { "id": "platformCertsParsing", "result": { "passed": true, "detail": "..." } },
    { "id": "arkRootTrust", "result": { "passed": true, "detail": "..." } },
    { "id": "runtimeClaimsParsing", "result": { "passed": true, "detail": "..." } },
    { "id": "akKeyExtraction", "result": { "passed": true, "detail": "..." } },
    { "id": "aikCertBinding", "result": { "passed": true, "detail": "..." } },
    { "id": "quoteFormat", "result": { "passed": true, "detail": "..." } },
    { "id": "tpmQuoteSignature", "result": { "passed": true, "detail": "..." } },
    { "id": "nonce", "result": { "passed": true, "detail": "..." } },
    { "id": "pcrDigest", "result": { "passed": true, "detail": "..." } },
    { "id": "reportDataBinding", "result": { "passed": true, "detail": "..." } },
    { "id": "snpReportFormat", "result": { "passed": true, "detail": "..." } },
    { "id": "certChainValidation", "result": { "passed": true, "detail": "..." } },
    { "id": "snpSignature", "result": { "passed": true, "detail": "..." } },
    { "id": "metadataBinding", "result": { "passed": true, "detail": "..." } },
    { "id": "gpuCountBinding", "result": { "passed": true, "detail": "..." } }
  ],
  "runtimeClaims": { "keys": [...], "vm-configuration": {...}, "user-data": "..." },
  "gpuClaims": { "gpuCount": 0 },
  "reportData": "<base64>"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `verified` | boolean | `true` only if **every** check passed. |
| `checks` | array | Ordered list of named check results. Each has `id`, and a `result` with `passed` (bool) and either `detail` or `error`. |
| `runtimeClaims` | object | Parsed runtime claims from the HCL report (present on successful verification). |
| `gpuClaims` | object? | GPU claims extracted from the validated user data document. Contains `gpuCount`. |
| `reportData` | string? | Base64-encoded report data payload extracted from the validated user data document. |

## Verification checks

### CPU checks

All CPU checks must pass for `verified: true`:

| # | Check | What it validates |
|---|-------|-------------------|
| 1 | `platformCertsParsing` | PEM platform certs decoded into VCEK, ASK, ARK. |
| 2 | `arkRootTrust` | Provided ARK matches well-known AMD root for the detected product. |
| 3 | `runtimeClaimsParsing` | Runtime claims JSON extracted from the HCL report's runtime data section. |
| 4 | `akKeyExtraction` | HCLAkPub RSA public key extracted from the runtime claims JWK `keys` array. |
| 5 | `aikCertBinding` | Optional AIK certificate public key matches HCLAkPub. |
| 6 | `quoteFormat` | TPM quote blob parsed into TPMS_ATTEST + TPMT_SIGNATURE (using go-tpm). |
| 7 | `tpmQuoteSignature` | RSA-PKCS1v15-SHA256 signature over the TPMS_ATTEST bytes is valid against HCLAkPub. |
| 8 | `nonce` | `extraData` field in the TPM quote matches the expected nonce. |
| 9 | `pcrDigest` | SHA256 of the concatenated PCR values (sorted by index) matches the PCR digest in the quote. |
| 10 | `reportDataBinding` | SHA256 of VarData (runtime claims JSON from HCL report) equals `report_data[0:32]` in the SNP report. Binds the TPM's AK key to the hardware attestation. |
| 11 | `snpReportFormat` | SNP report is the expected 1184 bytes. |
| 12 | `certChainValidation` | AMD certificate chain is valid: ARK (self-signed) → ASK → VCEK. |
| 13 | `snpSignature` | ECDSA-P384-SHA384 signature over the SNP report's signed region (bytes 0x000–0x29F) is valid against the VCEK public key. |

### User data document checks

When CPU verification succeeds:

| # | Check | What it validates |
|---|-------|-------------------|
| 14 | `metadataBinding` | SHA256 of the user data document matches the runtime claims `user-data[0:32]`, and `user-data[32:64]` are all zeros. |
| 15 | `gpuCountBinding` | `gpuCount` in the user data document matches the number of supplied GPU evidences. |

### GPU checks

When GPU evidence is present:

| # | Check | What it validates |
|---|-------|-------------------|
| 1 | `gpuInput` | GPU evidence block is non-empty. |
| 2 | `certChainParsing` | GPU certificate chain parsed from PEM. |
| 3 | `rootTrust` | GPU root CA matches pinned NVIDIA Device Identity CA. |
| 4 | `certChainValidation` | NVIDIA root → intermediates → leaf chain valid. |
| 5 | `reportSignature` | SPDM report signature verified with GPU leaf certificate. |
| 6 | `nonceBinding` | SPDM nonce matches CPU SNP report_data[0:32]. |
| 7–12 | `gpuRIMAppraisal`, etc. | NVAT measured-state appraisal, secure boot, debug state, driver/VBIOS RIM checks. |

## Trust chain

```
AMD Root Key (ARK)          ← self-signed, well-known AMD root
  └─ AMD Signing Key (ASK)  ← signed by ARK
      └─ VCEK               ← signed by ASK, chip+TCB specific
          └─ SNP Report      ← ECDSA-P384 signed by VCEK
              └─ report_data[0:32] = SHA256(runtime claims JSON)
                  ├─ runtime claims contain HCLAkPub (JWK)
                  │   └─ TPM Quote ← RSA signed by HCLAkPub (AIK)
                  │       ├─ nonce (extraData)
                  │       └─ PCR digest
                  └─ user-data[0:32] = SHA256(user data document)
                      └─ user data document
                          ├─ reportData (caller's report data payload)
                          └─ gpuCount → GPU evidence binding
```