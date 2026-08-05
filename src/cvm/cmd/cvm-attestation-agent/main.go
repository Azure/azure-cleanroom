package main

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"net/http"
	"os/exec"
	"path/filepath"
	"runtime/debug"
	"sort"
	"strconv"
	"strings"
	"time"

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
	ReportData   string `json:"reportData"`             // base64-encoded caller report data payload
	Nonce        string `json:"nonce"`                  // base64-encoded nonce for TPM quote (max 32 bytes)
	PCRSelection []int  `json:"pcrSelection,omitempty"` // optional list of PCR indices (0-23); defaults to all 24
}

// AttestResponse is the JSON response returned by the /snp/attest endpoint.
// The response is structured as { vtpm: {...}, gpu: {...}, userDataDocument: "..." }
// where gpu is present only when a GPU is detected on the node.
type AttestResponse struct {
	Vtpm             VtpmEvidence       `json:"vtpm"`                       // vTPM/SNP attestation evidence
	Gpu              *GpuAttestEvidence `json:"gpu,omitempty"`              // GPU attestation evidence (optional)
	UserDataDocument string             `json:"userDataDocument"` // base64-encoded canonical JSON user data document bound to report_data
}

// VtpmEvidence contains the vTPM/SNP attestation artifacts.
type VtpmEvidence struct {
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

// GpuAttestEvidence contains GPU attestation artifacts collected via NVAT
// when a GPU is detected on the node.
type GpuAttestEvidence struct {
	Evidences []GpuDeviceEvidence `json:"evidences"` // one entry per GPU
}

// GpuDeviceEvidence contains attestation evidence for a single GPU.
type GpuDeviceEvidence struct {
	Evidence    string `json:"evidence"`    // base64-encoded SPDM attestation report
	Certificate string `json:"certificate"` // base64-encoded GPU certificate chain
	Arch        string `json:"arch"`        // raw NVAT file-evidence architecture
	Nonce       string `json:"nonce"`       // raw NVAT file-evidence nonce
}

// gpuCollectOutput is the JSON structure emitted by
// `nvattest collect-evidence --format json`.
type gpuCollectOutput struct {
	Evidences []struct {
		Evidence    string `json:"evidence"`
		Certificate string `json:"certificate"`
		Arch        string `json:"arch"`
		Nonce       string `json:"nonce"`
	} `json:"evidences"`
	ResultCode    int    `json:"result_code"`
	ResultMessage string `json:"result_message"`
}

const (
	nvidiaDeviceGlob = "/dev/nvidia[0-9]*"
	nvatBinaryPath   = "nvattest"

	// nvatCollectTimeout bounds the NVAT evidence collection subprocess.
	nvatCollectTimeout = 60 * time.Second
)

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

	gpuCount, err := detectGPUCount()
	if err != nil {
		log.Printf("GPU count detection failed: %v", err)
		httputil.WriteError(w, http.StatusInternalServerError,
			"GpuAttestationFailed", fmt.Sprintf("failed to detect GPU count: %v", err))
		return
	}

	doc := &attestation.UserDataDocument{
		Version:  attestation.CurrentDocumentVersion,
		ReportData: base64.StdEncoding.EncodeToString(rdBytes),
		GPUCount: gpuCount,
	}

	reportData, docJSON, err := attestation.BuildReportData(doc)
	if err != nil {
		log.Printf("report_data preparation failed: %v", err)
		httputil.WriteError(w, http.StatusInternalServerError,
			"AttestationFailed", fmt.Sprintf("failed to prepare report_data: %v", err))
		return
	}

	log.Printf("Binding user data document into report_data: %s", string(docJSON))

	evidence, err := attestation.CollectEvidenceWithReportData(nonce, reportData, req.PCRSelection)
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

	// Use the first 32 bytes of the actual SNP report_data as the GPU
	// attestation nonce. The verifier binds GPU evidence to the CPU SNP
	// report, not to the caller-provided report_data template.
	gpuNonce, err := attestation.GPUAttestationNonce(evidence.SNPReport)
	if err != nil {
		log.Printf("GPU nonce derivation failed: %v", err)
		httputil.WriteError(w, http.StatusInternalServerError,
			"GpuAttestationFailed", fmt.Sprintf("failed to derive GPU nonce: %v", err))
		return
	}

	gpuEvidence, err := collectGpuEvidence(gpuCount, gpuNonce)
	if err != nil {
		log.Printf("GPU attestation failed: %v", err)
		httputil.WriteError(w, http.StatusInternalServerError,
			"GpuAttestationFailed", fmt.Sprintf("GPU attestation failed: %v", err))
		return
	}

	resp := AttestResponse{
		Vtpm: VtpmEvidence{
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
		},
		Gpu:                gpuEvidence,
		UserDataDocument: base64.StdEncoding.EncodeToString(docJSON),
	}

	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(resp)
}

// detectGPUCount counts numeric /dev/nvidia<N> device nodes on the host.
func detectGPUCount() (int, error) {
	devicePaths, err := filepath.Glob(nvidiaDeviceGlob)
	if err != nil {
		return 0, fmt.Errorf("error enumerating %s: %w", nvidiaDeviceGlob, err)
	}

	gpuCount := 0
	for _, devicePath := range devicePaths {
		deviceName := filepath.Base(devicePath)
		suffix := strings.TrimPrefix(deviceName, "nvidia")
		if suffix == "" {
			continue
		}

		if _, err := strconv.Atoi(suffix); err != nil {
			continue
		}

		gpuCount++
	}

	return gpuCount, nil
}

// collectGpuEvidence uses the same signed GPU count that was stamped into
// report_data, then runs nvattest for GPU nodes.
// Returns (nil, nil) on non-GPU nodes.
func collectGpuEvidence(gpuCount int, nonce string) (*GpuAttestEvidence, error) {
	if gpuCount == 0 {
		log.Printf("No GPU detected (no %s devices found), skipping GPU attestation.", nvidiaDeviceGlob)
		return nil, nil
	}

	currentGPUCount, err := detectGPUCount()
	if err != nil {
		return nil, fmt.Errorf("failed to re-check GPU count: %w", err)
	}
	if currentGPUCount != gpuCount {
		return nil, fmt.Errorf(
			"GPU count changed after report_data binding: signed %d, current %d",
			gpuCount,
			currentGPUCount)
	}

	log.Printf("GPU count %d detected, collecting GPU attestation evidence (nonce=%s...)",
		gpuCount,
		nonce[:16])

	if len(nonce) != 64 {
		return nil, fmt.Errorf("GPU nonce must be exactly 64 hex chars (32 bytes), got %d", len(nonce))
	}

	result, err := runNVATCollectEvidence(nonce)
	if err != nil {
		return nil, err
	}

	if result.ResultCode != 0 {
		return nil, fmt.Errorf("GPU evidence collection failed: code=%d, msg=%s",
			result.ResultCode, result.ResultMessage)
	}

	if len(result.Evidences) == 0 {
		return nil, fmt.Errorf("GPU evidence collection returned no evidences")
	}
	if len(result.Evidences) != gpuCount {
		return nil, fmt.Errorf(
			"GPU evidence collection returned %d evidence(s), expected %d",
			len(result.Evidences),
			gpuCount)
	}

	var evidences []GpuDeviceEvidence
	for i, gpu := range result.Evidences {
		log.Printf("GPU %d: evidence=%d chars, certificate=%d chars",
			i, len(gpu.Evidence), len(gpu.Certificate))
		evidences = append(evidences, GpuDeviceEvidence{
			Evidence:    gpu.Evidence,
			Certificate: gpu.Certificate,
			Arch:        gpu.Arch,
			Nonce:       gpu.Nonce,
		})
	}

	log.Printf("GPU attestation evidence collected successfully: %d GPU(s)", len(evidences))
	return &GpuAttestEvidence{Evidences: evidences}, nil
}

func runNVATCollectEvidence(nonce string) (*gpuCollectOutput, error) {
	ctx, cancel := context.WithTimeout(context.Background(), nvatCollectTimeout)
	defer cancel()

	cmd := exec.CommandContext(ctx,
		nvatBinaryPath,
		"collect-evidence",
		"--device", "gpu",
		"--gpu-evidence-source", "nvml",
		"--nonce", nonce,
		"--format", "json",
		"--log-level", "error",
	)

	var stdout bytes.Buffer
	var stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr

	err := cmd.Run()
	output := bytes.TrimSpace(stdout.Bytes())
	stderrText := strings.TrimSpace(stderr.String())
	if len(output) == 0 {
		if err != nil {
			return nil, fmt.Errorf("%s collect-evidence failed: %w (stderr: %s)",
				nvatBinaryPath, err, stderrText)
		}

		if stderrText != "" {
			return nil, fmt.Errorf("%s returned no JSON output (stderr: %s)",
				nvatBinaryPath, stderrText)
		}

		return nil, fmt.Errorf("%s returned no JSON output", nvatBinaryPath)
	}

	var result gpuCollectOutput
	if unmarshalErr := json.Unmarshal(output, &result); unmarshalErr != nil {
		if stderrText != "" {
			return nil, fmt.Errorf("failed to parse %s JSON output: %w (stderr: %s, raw: %s)",
				nvatBinaryPath, unmarshalErr, stderrText, string(output))
		}

		return nil, fmt.Errorf("failed to parse %s JSON output: %w (raw: %s)",
			nvatBinaryPath, unmarshalErr, string(output))
	}

	return &result, nil
}
