# Inferencing Call Chains

## Phase 0 — Pod Startup: ccr-governance Trust Bootstrap

**When:** Pod startup. The ccr-governance sidecar must complete this before serving any governance calls.

**Purpose:** Securely fetch the CCF `service_cert.pem` by calling the ccf-recovery-agent's
`/network/report`, verifying it via SNP attestation, and using the extracted cert for all CGS connections.

```
ccr-governance sidecar
  │
  ├─ GET <recovery-agent>/network/report  (insecure TLS — cert not yet known)
  │    └─ returns: { snpReport, reportDataPayload: {serviceCert, constitutionDigest, jsappBundleDigest} }
  │
  ├─ VerifySnpAttestation(snpReport)
  │    ├─ hostData   == expected CCE policy hash   (right container code)
  │    └─ reportData == SHA256(reportDataPayload)  (payload not tampered)
  │
  ├─ Verify constitutionDigest and jsappBundleDigest match configured values
  │
  └─ Use serviceCert as trusted CA for all future CGS HTTPS calls
```

---

## 1. Auditor: Verify Attestation (`GET /report`)

**Purpose:** Confirm the pod is genuinely attested, runs the approved policy, and holds a CGS-issued cert.

```
Auditor
  │  GET https://<cleanroom-endpoint>/report
  ▼
ccr-proxy (port 443) → kserve-inferencing-agent :8080  GET /report
  │
  ├─ Read service-cert.pem from /app/service/service-cert.pem
  ├─ POST localhost:8284/attest/combined  (SKR sidecar — AMD hardware)
  │    runtime_data = SHA256(serviceCert)
  │    └─ returns SNP attestation report
  │
  ├─ GetCACIHostData() → extract hostData from SNP report
  │
  └─ Return: { platform, serviceCert, hostData, snpReport }

Auditor checks:
  1. hostData == expected CCE policy hash
  2. serviceCert signed by cleanroomca.crt
  3. AMD Chain
```

The `cleanroomca.crt` trust anchor can be fetched via:

```bash
az cleanroom governance ca show \
    --contract-id <contract-id> \
    --governance-client <client-name> \
    --query "caCert" \
    --output tsv > cleanroomca.crt
```
---

## 2. Control Plane: Deploy Model (`POST /inferenceServices`)

**Purpose:** Deploy a model. Enforces governance authorization before submitting to KServe.

```
ISV / caller
  │  POST https://<cleanroom-endpoint>/inferenceServices
  │  Header: x-ms-cleanroom-authorization: Bearer <token>
  ▼
ccr-proxy (port 443) → kserve-inferencing-agent :8080
  │
  ├─ Validate token → ccr-governance sidecar → CGS (caller is active member)
  ├─ Check consortium membership → CGS
  ├─ Bootstrap trust in frontend via GET <frontend>/report + VerifySnpAttestation
  ├─ Fetch model document from CGS
  ├─ Check telemetry consent, set pod access policies, log audit event → CGS
  │
  └─ POST <frontend-endpoint>/inferencing/deployModel
       └─ kserve-inferencing-frontend → submit KServe InferenceService to Kubernetes
```

---

## 3. Data Plane: Inference Request (`POST /ai/...`)

**Purpose:** Route an inference request to the predictor pod, with per-request authorization.

```
ISV / application
  │  POST https://<cleanroom-endpoint>/ai/openai/v1/chat/completions
  │  Header: x-ms-cleanroom-authorization: Bearer <token>
  ▼
ccr-proxy (port 443) — Envoy
  │
  ├─ ext_authz filter → POST agent /authz/{path}
  │    ├─ Validate token → ccr-governance sidecar → CGS  (cached 15 min)
  │    └─ Return: x-model-name, x-model-endpoint headers
  │
  ├─ Lua filter: rewrite Host header to predictor service FQDN
  ├─ Dynamic forward proxy: resolve predictor DNS, verify TLS via CGS CA cert
  │
  └─ Predictor Pod :443 → run inference → response back to caller
```
---

## Key Components

| Component | Role |
|---|---|
| ccr-proxy (Envoy) | TLS termination, routing, ext_authz, dynamic forward proxy |
| kserve-inferencing-agent | Auth, governance calls, control plane |
| kserve-inferencing-frontend | Submits KServe InferenceService to Kubernetes |
| ccr-governance sidecar | Bridges agent ↔ CGS over HTTPS |
| ccf-recovery-agent | Serves `/network/report` for trust bootstrap |
| CGS | Governance policy enforcement on CCF |
| SKR sidecar (`localhost:8284`) | AMD SNP hardware attestation |
| `cleanroomca.crt` | Client trust anchor (`az cleanroom governance ca show`) |
