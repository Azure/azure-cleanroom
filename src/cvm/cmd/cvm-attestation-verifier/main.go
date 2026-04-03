// cvm-attestation-verifier runs a REST API server that verifies attestation
// evidence produced by the cvm-attestation-agent's /snp/attest endpoint.
//
// Usage:
//
//	cvm-attestation-verifier [-addr :8901]
//
// Endpoint:
//
//	POST /snp/verify
//
// Request body (JSON):
//
//	{
//	  "evidence": {              // the full response from /snp/attest
//	    "tpmQuote":      "…",   // base64
//	    "hclReport":     "…",   // base64
//	    "snpReport":     "…",   // base64
//	    "aikCert":       "…",   // base64
//	    "pcrs":          {"0":"…", …},  // index → base64 digest
//	  },
//	  "nonce":                "…",      // base64 or raw string: expected TPM quote nonce
//	  "product":              "Milan",  // AMD product name (optional, default "Milan")
//	  "platformCertificates": "…"       // PEM-encoded AMD cert chain (VCEK, ASK, ARK) from THIM
//	}
//
// The verifier performs 12 independent checks that mirror the trust chain
// validated by the azure-cvm-tooling Rust crate:
//
//  1. platformCertsParsing – PEM platform certs decoded into VCEK, ASK, ARK
//  2. arkRootTrust         – provided ARK matches well-known AMD root for product
//  3. runtimeClaimsParsing – runtime claims extracted from HCL report
//  4. akKeyExtraction      – HCLAkPub RSA key extracted from runtime claims
//  5. quoteFormat          – TPM quote blob parsed (TPM2B_ATTEST + TPMT_SIGNATURE)
//  6. tpmQuoteSignature    – RSA-SHA256 quote signature verified with HCLAkPub
//  7. nonce                – extraData in quote matches expected nonce
//  8. pcrDigest            – SHA256(PCR values) matches quote digest
//  9. reportDataBinding    – SHA256(VarData) == SNP report_data[0:32]
//  10. snpReportFormat      – SNP report size validated (1184 bytes)
//  11. certChainValidation  – ARK → ASK → VCEK chain valid
//  12. snpSignature         – AMD ECDSA-P384-SHA384 signature over SNP report
package main

import (
	"encoding/base64"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"net/http"
	"runtime/debug"
	"strconv"

	"github.com/azure/azure-cleanroom/src/cvm/pkg/httputil"
	"github.com/azure/azure-cleanroom/src/cvm/pkg/verify"
)

// ──────────────────────────────────────────────────────────────────────────────
// Request / Response types
// ──────────────────────────────────────────────────────────────────────────────

// VerifyRequest is the JSON body expected by the /snp/verify endpoint.
type VerifyRequest struct {
	Evidence             Evidence `json:"evidence"`             // cvm-attestation-agent /snp/attest response
	Nonce                string   `json:"nonce"`                // expected nonce (base64 or raw string)
	Product              string   `json:"product,omitempty"`    // AMD product: "Milan" (default), "Genoa"
	PlatformCertificates string   `json:"platformCertificates"` // PEM-encoded AMD cert chain (VCEK, ASK, ARK) from THIM
}

// Evidence mirrors the cvm-attestation-agent's AttestResponse.
// RuntimeClaims are extracted automatically from the HCL report and do not need
// to be supplied by the caller.
type Evidence struct {
	TPMQuote  string            `json:"tpmQuote"`
	HCLReport string            `json:"hclReport"`
	SNPReport string            `json:"snpReport"`
	AIKCert   string            `json:"aikCert"`
	PCRs      map[string]string `json:"pcrs"`
}

// ──────────────────────────────────────────────────────────────────────────────
// main
// ──────────────────────────────────────────────────────────────────────────────

func main() {
	addr := flag.String("addr", ":8901", "listen address (host:port)")
	flag.Parse()

	http.HandleFunc("/snp/verify", verifyHandler)

	fmt.Printf("cvm-attestation-verifier listening on %s\n", *addr)
	log.Fatal(http.ListenAndServe(*addr, nil))
}

// ──────────────────────────────────────────────────────────────────────────────
// Handler
// ──────────────────────────────────────────────────────────────────────────────

func verifyHandler(w http.ResponseWriter, r *http.Request) {
	defer func() {
		if r := recover(); r != nil {
			log.Printf("panic in verifyHandler: %v\n%s", r, debug.Stack())
			httputil.WriteError(w, http.StatusInternalServerError,
				"InternalError", fmt.Sprintf("internal error: %v", r))
		}
	}()

	if r.Method != http.MethodPost {
		httputil.WriteError(w, http.StatusMethodNotAllowed, "MethodNotAllowed", "use POST")
		return
	}

	var req VerifyRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		httputil.WriteError(w, http.StatusBadRequest, "InvalidRequestBody", fmt.Sprintf("invalid JSON body: %v", err))
		return
	}

	// ── Decode base64 fields ─────────────────────────────────────────────

	tpmQuote, err := base64.StdEncoding.DecodeString(req.Evidence.TPMQuote)
	if err != nil {
		httputil.WriteError(w, http.StatusBadRequest, "InvalidTPMQuote", fmt.Sprintf("invalid tpmQuote base64: %v", err))
		return
	}

	hclReport, err := base64.StdEncoding.DecodeString(req.Evidence.HCLReport)
	if err != nil {
		httputil.WriteError(w, http.StatusBadRequest, "InvalidHCLReport", fmt.Sprintf("invalid hclReport base64: %v", err))
		return
	}

	snpReport, err := base64.StdEncoding.DecodeString(req.Evidence.SNPReport)
	if err != nil {
		httputil.WriteError(w, http.StatusBadRequest, "InvalidSNPReport", fmt.Sprintf("invalid snpReport base64: %v", err))
		return
	}

	var aikCert []byte
	if req.Evidence.AIKCert != "" {
		aikCert, err = base64.StdEncoding.DecodeString(req.Evidence.AIKCert)
		if err != nil {
			httputil.WriteError(w, http.StatusBadRequest, "InvalidAIKCert", fmt.Sprintf("invalid aikCert base64: %v", err))
			return
		}
	}

	// ── Decode PCR values ────────────────────────────────────────────────

	pcrValues := make(map[int][]byte, len(req.Evidence.PCRs))
	for k, v := range req.Evidence.PCRs {
		idx, err := strconv.Atoi(k)
		if err != nil {
			httputil.WriteError(w, http.StatusBadRequest, "InvalidPCRIndex", fmt.Sprintf("invalid PCR index %q: %v", k, err))
			return
		}
		digest, err := base64.StdEncoding.DecodeString(v)
		if err != nil {
			httputil.WriteError(w, http.StatusBadRequest, "InvalidPCRValue", fmt.Sprintf("invalid base64 for PCR %d: %v", idx, err))
			return
		}
		pcrValues[idx] = digest
	}

	// ── Decode nonce ─────────────────────────────────────────────────────
	// Accept base64-encoded bytes or a raw string.

	nonce, err := base64.StdEncoding.DecodeString(req.Nonce)
	if err != nil {
		nonce = []byte(req.Nonce)
	}

	// ── Run verification ─────────────────────────────────────────────────

	result := verify.VerifyAll(&verify.EvidenceInput{
		TPMQuote:             tpmQuote,
		HCLReport:            hclReport,
		SNPReport:            snpReport,
		AIKCert:              aikCert,
		PCRValues:            pcrValues,
		Nonce:                nonce,
		AMDProduct:           req.Product,
		PlatformCertificates: req.PlatformCertificates,
	})

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(result)
}
