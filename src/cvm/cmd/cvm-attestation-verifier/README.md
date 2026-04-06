# cvm-attestation-verifier

REST API server that verifies attestation evidence produced by the `cvm-attestation-agent`'s `/snp/attest` endpoint. It validates the full Azure CVM trust chain, mirroring the checks performed by the [azure-cvm-tooling](https://github.com/kinvolk/azure-cvm-tooling) Rust crate.

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
  "evidence": {
    "tpmQuote":      "<base64>",
    "hclReport":     "<base64>",
    "snpReport":     "<base64>",
    "aikCert":       "<base64>",
    "pcrs": {
      "0": "<base64>",
      "1": "<base64>",
      "...": "..."
    }
  },
  "nonce":   "<base64 or raw string>",
  "product": "Milan"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `evidence` | object | Yes | The full JSON response from `cvm-attestation-agent`'s `POST /snp/attest` endpoint. Only `tpmQuote`, `hclReport`, `snpReport`, and `pcrs` are used; `runtimeClaims` is extracted automatically from the HCL report. |
| `evidence.tpmQuote` | string | Yes | Base64-encoded TPM quote blob (TPM2B_ATTEST + TPMT_SIGNATURE). |
| `evidence.hclReport` | string | Yes | Base64-encoded HCL report blob from vTPM NVRAM. Runtime claims (including HCLAkPub) are parsed from this automatically. |
| `evidence.snpReport` | string | Yes | Base64-encoded 1184-byte AMD SNP attestation report. |
| `evidence.aikCert` | string | No | Base64-encoded AIK x.509 certificate (DER). |
| `evidence.pcrs` | object | Yes | SHA256 PCR values as `{ "index": "<base64 digest>", ... }`. |
| `nonce` | string | Yes | Expected TPM quote nonce (must match the nonce sent to the cvm-attestation-agent). Base64-encoded bytes or a raw string. |
| `product` | string | No | AMD product name for KDS certificate lookup. Defaults to `"Milan"`. Use `"Genoa"` for 4th-gen EPYC. |

#### Response

```json
{
  "verified": true,
  "checks": {
    "runtimeClaimsParsing": { "passed": true, "detail": "runtime claims extracted from HCL report" },
    "akKeyExtraction":    { "passed": true, "detail": "HCLAkPub RSA-2048 extracted from runtime claims" },
    "quoteFormat":        { "passed": true, "detail": "TPM quote parsed successfully" },
    "tpmQuoteSignature":  { "passed": true, "detail": "RSA-SHA256 signature valid" },
    "nonce":              { "passed": true, "detail": "nonce matches expected value" },
    "pcrDigest":          { "passed": true, "detail": "SHA256(PCR values) matches quote digest (24 PCRs)" },
    "reportDataBinding":  { "passed": true, "detail": "SHA256(VarData) == report_data[0:32]" },
    "snpReportFormat":    { "passed": true, "detail": "1184 bytes, signed region 0x000–0x29F" },
    "vcekFetch":          { "passed": true, "detail": "VCEK fetched for Milan (bl=3 tee=0 snp=8 ucode=115)" },
    "certChainFetch":     { "passed": true, "detail": "ASK (SEV-Milan) + ARK (ARK-Milan) fetched" },
    "certChainValidation":{ "passed": true, "detail": "ARK → ASK → VCEK chain valid" },
    "snpSignature":       { "passed": true, "detail": "ECDSA-P384-SHA384 signature valid" }
  }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `verified` | boolean | `true` only if **every** check passed. |
| `checks` | object | Map of check name → result. Each result has `passed` (bool), and either `detail` (on success) or `error` (on failure). |

#### Failed check example

```json
{
  "verified": false,
  "checks": {
    "nonce": { "passed": false, "error": "nonce mismatch: got 6162..., expected 7879..." },
    "tpmQuoteSignature": { "passed": true, "detail": "RSA-SHA256 signature valid" },
    "...": "..."
  }
}
```

## Verification checks

The verifier performs these independent checks, all of which must pass for `verified: true`:

| # | Check | What it validates |
|---|-------|-------------------|
| 0 | `runtimeClaimsParsing` | Runtime claims JSON extracted from the HCL report's runtime data section (offset 1216). |
| 1 | `akKeyExtraction` | HCLAkPub RSA public key extracted from the runtime claims JWK `keys` array. |
| 2 | `quoteFormat` | TPM quote blob successfully parsed into TPMS_ATTEST + TPMT_SIGNATURE (using go-tpm). |
| 3 | `tpmQuoteSignature` | RSA-PKCS1v15-SHA256 signature over the TPMS_ATTEST bytes is valid against HCLAkPub. |
| 4 | `nonce` | `extraData` field in the TPM quote matches the expected nonce. |
| 5 | `pcrDigest` | SHA256 of the concatenated PCR values (sorted by index) matches the PCR digest in the quote. |
| 6 | `reportDataBinding` | SHA256 of VarData (runtime data section of HCL report) equals `report_data[0:32]` in the SNP report. This binds the TPM's AK key to the hardware attestation. |
| 7 | `snpReportFormat` | SNP report is the expected 1184 bytes. |
| 8 | `vcekFetch` | VCEK certificate fetched from AMD KDS using the chip ID and TCB version from the SNP report. |
| 9 | `certChainFetch` | ASK + ARK certificates fetched from AMD KDS. |
| 10 | `certChainValidation` | AMD certificate chain is valid: ARK (self-signed) → ASK → VCEK. |
| 11 | `snpSignature` | ECDSA-P384-SHA384 signature over the SNP report's signed region (bytes 0x000–0x29F) is valid against the VCEK public key. |

## Trust chain

```
AMD Root Key (ARK)          ← self-signed, fetched from AMD KDS
  └─ AMD Signing Key (ASK)  ← signed by ARK
      └─ VCEK               ← signed by ASK, chip+TCB specific
          └─ SNP Report      ← ECDSA-P384 signed by VCEK
              └─ report_data[0:32] = SHA256(VarData)
                  └─ VarData contains HCLAkPub (JWK)
                      └─ TPM Quote ← RSA signed by HCLAkPub (AIK)
                          ├─ nonce (extraData)
                          └─ PCR digest
```