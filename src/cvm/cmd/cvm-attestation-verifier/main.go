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
//	  "vtpm": {
//	    "evidence": {
//	      "tpmQuote":      "…",   // base64
//	      "hclReport":     "…",   // base64
//	      "snpReport":     "…",   // base64
//	      "aikCert":       "…",   // base64
//	      "pcrs":          {"0":"…", …}
//	    },
//	    "nonce":                "…",      // base64 or raw string
//	    "product":              "Milan",  // AMD product name (optional)
//	    "platformCertificates": "…"       // PEM-encoded AMD cert chain from THIM
//	  },
//	  "gpu": {                            // optional, present on GPU nodes
//	    "evidences": [{
//	      "evidence":      "…",           // base64-encoded SPDM report
//	      "certificate":   "…",           // base64-encoded GPU cert chain
//	      "arch":          "…",           // NVAT file-evidence architecture
//	      "nonce":         "…"            // NVAT file-evidence nonce (hex)
//	    }]
//	  },
//	  "userDataDocument":   "…"         // base64-encoded canonical JSON user data document
//	}
//
// The verifier performs the following CPU checks:
//
//  1. platformCertsParsing – PEM platform certs decoded into VCEK, ASK, ARK
//  2. arkRootTrust         – provided ARK matches well-known AMD root for product
//  3. runtimeClaimsParsing – runtime claims extracted from HCL report
//  4. akKeyExtraction      – HCLAkPub RSA key extracted from runtime claims
//  5. aikCertBinding       – optional AIK cert public key matches HCLAkPub
//  6. quoteFormat          – TPM quote blob parsed (TPM2B_ATTEST + TPMT_SIGNATURE)
//  7. tpmQuoteSignature    – RSA-SHA256 quote signature verified with HCLAkPub
//  8. nonce                – extraData in quote matches expected nonce
//  9. pcrDigest            – SHA256(PCR values) matches quote digest
//  10. reportDataBinding   – SHA256(VarData) == SNP report_data[0:32]
//  11. snpReportFormat     – SNP report size validated (1184 bytes)
//  12. certChainValidation – ARK → ASK → VCEK chain valid
//  13. snpSignature        – AMD ECDSA-P384-SHA384 signature over SNP report
//
// When CPU verification succeeds, the verifier also checks:
//
//  14. metadataBinding    – SHA256(user data document) matches user-data[0:32]
//  15. gpuCountBinding    – document gpuCount matches supplied GPU evidence count
//
// When GPU evidence is present, the following GPU checks are also performed:
//
//  1. gpuInput            – fails if a GPU block is present but empty
//  2. certChainParsing    – GPU certificate chain parsed from PEM
//  3. rootTrust           – GPU root CA matches pinned NVIDIA Device Identity CA
//  4. certChainValidation – NVIDIA root → intermediates → leaf chain valid
//  5. reportSignature     – SPDM report signature verified with GPU leaf cert
//  6. nonceBinding        – SPDM nonce matches CPU SNP report_data[0:32]
//  7. gpuRIMAppraisal     – nvattest local verifier appraised the GPU bundle
//  8. rimAppraisal        – NVAT measured-state appraisal succeeded
//  9. secureBoot          – NVAT secure-boot claim is true
//  10. debugState         – NVAT debug-state claim is disabled
//  11. driverRIM          – NVAT driver RIM checks all passed
//  12. vbiosRIM           – NVAT VBIOS RIM checks all passed
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
// Accepts the combined attestation structure { vtpm: {...}, gpu?: {...}, userDataDocument: "..." }.
type VerifyRequest struct {
	VTpm             VTpmInput        `json:"vtpm"`                       // vTPM/SNP attestation evidence
	Gpu              *verify.GpuInput `json:"gpu,omitempty"`              // GPU attestation evidence (optional)
	UserDataDocument string           `json:"userDataDocument"` // base64-encoded canonical JSON user data document
}

// VTpmInput contains the vTPM/SNP evidence to verify.
type VTpmInput struct {
	Evidence             Evidence `json:"evidence"`             // attestation artifacts
	Nonce                string   `json:"nonce"`                // expected nonce (base64 or raw string)
	Product              string   `json:"product,omitempty"`    // AMD product: "Milan" (default), "Genoa"
	PlatformCertificates string   `json:"platformCertificates"` // PEM-encoded AMD cert chain (VCEK, ASK, ARK) from THIM
}

// Evidence mirrors the cvm-attestation-agent's vTPM evidence structure.
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

	tpmQuote, err := base64.StdEncoding.DecodeString(req.VTpm.Evidence.TPMQuote)
	if err != nil {
		httputil.WriteError(w, http.StatusBadRequest, "InvalidTPMQuote", fmt.Sprintf("invalid tpmQuote base64: %v", err))
		return
	}

	hclReport, err := base64.StdEncoding.DecodeString(req.VTpm.Evidence.HCLReport)
	if err != nil {
		httputil.WriteError(w, http.StatusBadRequest, "InvalidHCLReport", fmt.Sprintf("invalid hclReport base64: %v", err))
		return
	}

	snpReport, err := base64.StdEncoding.DecodeString(req.VTpm.Evidence.SNPReport)
	if err != nil {
		httputil.WriteError(w, http.StatusBadRequest, "InvalidSNPReport", fmt.Sprintf("invalid snpReport base64: %v", err))
		return
	}

	var aikCert []byte
	if req.VTpm.Evidence.AIKCert != "" {
		aikCert, err = base64.StdEncoding.DecodeString(req.VTpm.Evidence.AIKCert)
		if err != nil {
			httputil.WriteError(w, http.StatusBadRequest, "InvalidAIKCert", fmt.Sprintf("invalid aikCert base64: %v", err))
			return
		}
	}

	// ── Decode PCR values ────────────────────────────────────────────────

	pcrValues := make(map[int][]byte, len(req.VTpm.Evidence.PCRs))
	for k, v := range req.VTpm.Evidence.PCRs {
		idx, pcrErr := strconv.Atoi(k)
		if pcrErr != nil {
			httputil.WriteError(w, http.StatusBadRequest, "InvalidPCRIndex", fmt.Sprintf("invalid PCR index %q: %v", k, pcrErr))
			return
		}
		digest, decErr := base64.StdEncoding.DecodeString(v)
		if decErr != nil {
			httputil.WriteError(w, http.StatusBadRequest, "InvalidPCRValue", fmt.Sprintf("invalid base64 for PCR %d: %v", idx, decErr))
			return
		}
		pcrValues[idx] = digest
	}

	// ── Decode nonce ─────────────────────────────────────────────────────
	// Accept base64-encoded bytes or a raw string.

	nonce, err := base64.StdEncoding.DecodeString(req.VTpm.Nonce)
	if err != nil {
		nonce = []byte(req.VTpm.Nonce)
	}

	// ── Run vTPM/SNP verification ────────────────────────────────────────

	result := verify.VerifyAll(&verify.EvidenceInput{
		TPMQuote:             tpmQuote,
		HCLReport:            hclReport,
		SNPReport:            snpReport,
		AIKCert:              aikCert,
		PCRValues:            pcrValues,
		Nonce:                nonce,
		AMDProduct:           req.VTpm.Product,
		PlatformCertificates: req.VTpm.PlatformCertificates,
	})

	if len(result.RuntimeClaims) > 0 {
		docCheck := verify.VerifyMetadataDocumentBinding(
			req.UserDataDocument, req.Gpu, result.RuntimeClaims)
		result.Checks = append(result.Checks, docCheck.Checks...)
		if docCheck.Document != nil {
			result.GPUClaims = &verify.GPUClaims{
				GPUCount: docCheck.Document.GPUCount,
			}
			result.ReportData = docCheck.Document.ReportData
		}
		if !docCheck.Passed {
			result.Verified = false
		}
	}

	// ── Run GPU verification when GPU evidence is present ────────────────

	if req.Gpu != nil {
		log.Printf("GPU attestation block present: %d GPU(s), running verification",
			len(req.Gpu.Evidences))

		gpuChecks := verify.VerifyGPU(req.Gpu, snpReport)
		verify.PopulateGPUClaimsFromChecks(result.GPUClaims, gpuChecks)
		result.Checks = append(result.Checks, gpuChecks...)

		for _, check := range gpuChecks {
			if !check.Result.Passed {
				result.Verified = false
			}
		}
	}

	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(result)
}
