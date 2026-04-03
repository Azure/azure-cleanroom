package main

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"net/http"
	"runtime/debug"
	"sort"

	"github.com/azure/azure-cleanroom/src/cvm/pkg/attestation"
	"github.com/azure/azure-cleanroom/src/cvm/pkg/httputil"
	"github.com/azure/azure-cleanroom/src/cvm/pkg/imds"
	"github.com/azure/azure-cleanroom/src/cvm/pkg/thim"
)

// OrderedMap is an int-keyed map that serializes JSON keys in
// numerically ascending order (0, 1, 2, … 23).
type OrderedMap struct {
	Data map[int]string
}

func (o OrderedMap) MarshalJSON() ([]byte, error) {
	keys := make([]int, 0, len(o.Data))
	for k := range o.Data {
		keys = append(keys, k)
	}
	sort.Ints(keys)

	var buf bytes.Buffer
	buf.WriteString("{")
	for i, k := range keys {
		if i > 0 {
			buf.WriteString(",")
		}
		fmt.Fprintf(&buf, `"%d":"%s"`, k, o.Data[k])
	}
	buf.WriteString("}")
	return buf.Bytes(), nil
}

// AttestRequest is the JSON body expected by the /snp/attest endpoint.
type AttestRequest struct {
	ReportData   string `json:"reportData"`             // base64-encoded 64-byte report_data for SNP report
	Nonce        string `json:"nonce"`                  // base64-encoded nonce for TPM quote (max 32 bytes)
	PCRSelection []int  `json:"pcrSelection,omitempty"` // optional list of PCR indices (0-23); defaults to all 24
}

// AttestResponse is the JSON response returned by the /snp/attest endpoint.
type AttestResponse struct {
	Evidence             AttestEvidence       `json:"evidence"`             // collected attestation evidence
	Nonce                string               `json:"nonce"`                // base64-encoded nonce echoed from the request
	PlatformCertificates string               `json:"platformCertificates"` // PEM-encoded AMD cert chain (ARK, ASK, VCEK) from THIM
	ImageReference       *imds.ImageReference `json:"imageReference"`       // VM image reference (publisher, offer, SKU, version) from IMDS
}

// AttestEvidence contains the attestation artifacts collected from the platform.
type AttestEvidence struct {
	TPMQuote      string                     `json:"tpmQuote"`      // base64-encoded TPM quote (quoted + signature)
	HCLReport     string                     `json:"hclReport"`     // base64-encoded HCL report blob
	SNPReport     string                     `json:"snpReport"`     // base64-encoded AMD SNP attestation report from HCL report
	AIKCert       string                     `json:"aikCert"`       // base64-encoded AIK x.509 certificate (DER)
	PCRs          OrderedMap                 `json:"pcrs"`          // SHA256 PCR values (index -> base64-encoded digest), numerically sorted
	RuntimeClaims *attestation.RuntimeClaims `json:"runtimeClaims"` // parsed runtime claims from HCL report
}

func main() {
	addr := flag.String("addr", ":8900", "listen address (host:port)")
	flag.Parse()

	http.HandleFunc("/snp/attest", attestHandler)

	fmt.Printf("cvm-attestation-agent listening on %s\n", *addr)
	log.Fatal(http.ListenAndServe(*addr, nil))
}

func attestHandler(w http.ResponseWriter, r *http.Request) {
	defer func() {
		if r := recover(); r != nil {
			log.Printf("panic in attestHandler: %v\n%s", r, debug.Stack())
			httputil.WriteError(w, http.StatusInternalServerError,
				"InternalError", fmt.Sprintf("internal error: %v", r))
		}
	}()

	if r.Method != http.MethodPost {
		httputil.WriteError(w, http.StatusMethodNotAllowed, "MethodNotAllowed", "use POST")
		return
	}

	var req AttestRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		httputil.WriteError(w, http.StatusBadRequest, "InvalidRequestBody", fmt.Sprintf("invalid JSON body: %v", err))
		return
	}

	if req.ReportData == "" {
		httputil.WriteError(w, http.StatusBadRequest, "MissingReportData", "reportData is required")
		return
	}

	rdBytes, err := base64.StdEncoding.DecodeString(req.ReportData)
	if err != nil {
		httputil.WriteError(w, http.StatusBadRequest, "InvalidReportData", fmt.Sprintf("invalid base64 reportData: %v", err))
		return
	}
	if len(rdBytes) != attestation.ReportDataSize {
		httputil.WriteError(w, http.StatusBadRequest, "InvalidReportDataSize",
			fmt.Sprintf("reportData must be exactly %d bytes, got %d", attestation.ReportDataSize, len(rdBytes)))
		return
	}

	// Decode and validate the nonce (required, max 32 bytes).
	if req.Nonce == "" {
		httputil.WriteError(w, http.StatusBadRequest, "MissingNonce", "nonce is required")
		return
	}
	nonce, err := base64.StdEncoding.DecodeString(req.Nonce)
	if err != nil {
		httputil.WriteError(w, http.StatusBadRequest, "InvalidNonce", fmt.Sprintf("invalid base64 nonce: %v", err))
		return
	}
	if len(nonce) > 32 {
		httputil.WriteError(w, http.StatusBadRequest, "InvalidNonceSize",
			fmt.Sprintf("nonce must be at most 32 bytes, got %d", len(nonce)))
		return
	}
	// Validate PCR selection if provided
	for _, pcr := range req.PCRSelection {
		if pcr < 0 || pcr > 23 {
			httputil.WriteError(w, http.StatusBadRequest, "InvalidPCRSelection",
				fmt.Sprintf("pcrSelection values must be 0-23, got %d", pcr))
			return
		}
	}

	evidence, err := attestation.CollectEvidenceWithReportData(nonce, rdBytes, req.PCRSelection)
	if err != nil {
		log.Printf("attestation failed: %v", err)
		httputil.WriteError(w, http.StatusInternalServerError, "AttestationFailed", fmt.Sprintf("attestation failed: %v", err))
		return
	}

	// Fetch AMD platform certificates (ARK, ASK, VCEK) from THIM.
	platformCerts, err := thim.FetchPlatformCertificates()
	if err != nil {
		log.Printf("THIM fetch failed: %v", err)
		httputil.WriteError(w, http.StatusInternalServerError,
			"THIMFetchFailed", fmt.Sprintf("failed to fetch platform certificates: %v", err))
		return
	}

	// Fetch VM image reference from IMDS.
	imageRef, err := imds.FetchImageReference()
	if err != nil {
		log.Printf("IMDS image reference fetch failed: %v", err)
		httputil.WriteError(w, http.StatusInternalServerError,
			"IMDSFetchFailed", fmt.Sprintf("failed to fetch image reference: %v", err))
		return
	}

	// Encode PCR values into an OrderedMap for numerically-sorted JSON keys
	pcrMap := make(map[int]string, len(evidence.PCRs))
	for idx, digest := range evidence.PCRs {
		pcrMap[idx] = base64.StdEncoding.EncodeToString(digest)
	}

	resp := AttestResponse{
		Evidence: AttestEvidence{
			TPMQuote:      base64.StdEncoding.EncodeToString(evidence.TPMQuote),
			HCLReport:     base64.StdEncoding.EncodeToString(evidence.HCLReport),
			SNPReport:     base64.StdEncoding.EncodeToString(evidence.SNPReport),
			AIKCert:       base64.StdEncoding.EncodeToString(evidence.AIKCert),
			PCRs:          OrderedMap{Data: pcrMap},
			RuntimeClaims: evidence.RuntimeClaims,
		},
		Nonce:                req.Nonce,
		PlatformCertificates: platformCerts,
		ImageReference:       imageRef,
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(resp)
}
