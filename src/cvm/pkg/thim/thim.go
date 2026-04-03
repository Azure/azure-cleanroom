// Package thim provides helpers for fetching AMD SEV-SNP platform certificates
// from the Azure Instance Metadata Service (IMDS) THIM endpoint. On Azure CVMs
// the THIM endpoint returns the VCEK, ASK and ARK certificates that form the
// AMD certificate chain needed to verify an SNP attestation report.
//
// Endpoint:
//
//	GET http://169.254.169.254/metadata/THIM/amd/certification
//	Header: Metadata: true
//
// Response (JSON):
//
//	{
//	  "vcekCert":         "<PEM>",
//	  "certificateChain": "<PEM>",   // ASK + ARK concatenated
//	  ...
//	}
//
// FetchPlatformCertificates returns all three certificates concatenated as a
// single PEM string (VCEK + ASK + ARK) suitable for passing to a verifier.
package thim

import (
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/http"
	"strings"
	"time"
)

const (
	// thimURL is the Azure IMDS THIM endpoint for AMD SEV-SNP certificates.
	thimURL = "http://169.254.169.254/metadata/THIM/amd/certification"
)

// thimResponse is the JSON structure returned by the THIM endpoint.
type thimResponse struct {
	VcekCert         string `json:"vcekCert"`
	CertificateChain string `json:"certificateChain"`
}

// FetchPlatformCertificates queries the Azure IMDS THIM endpoint and returns
// the AMD platform certificate chain as a single PEM string containing the
// VCEK, ASK and ARK certificates (in that order).
func FetchPlatformCertificates() (string, error) {
	client := &http.Client{Timeout: 30 * time.Second}

	req, err := http.NewRequest(http.MethodGet, thimURL, nil)
	if err != nil {
		return "", fmt.Errorf("create THIM request: %w", err)
	}
	req.Header.Set("Metadata", "true")

	resp, err := client.Do(req)
	if err != nil {
		return "", fmt.Errorf("THIM HTTP GET: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		body, _ := io.ReadAll(resp.Body)
		return "", fmt.Errorf("THIM returned HTTP %d: %s", resp.StatusCode, string(body))
	}

	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return "", fmt.Errorf("read THIM response: %w", err)
	}

	var thimResp thimResponse
	if err := json.Unmarshal(body, &thimResp); err != nil {
		return "", fmt.Errorf("parse THIM JSON: %w", err)
	}

	if thimResp.VcekCert == "" {
		return "", fmt.Errorf("THIM response missing vcekCert")
	}
	if thimResp.CertificateChain == "" {
		return "", fmt.Errorf("THIM response missing certificateChain")
	}

	// Concatenate VCEK + certificate chain (ASK + ARK) into a single PEM bundle.
	var sb strings.Builder
	sb.WriteString(strings.TrimSpace(thimResp.VcekCert))
	sb.WriteString("\n")
	sb.WriteString(strings.TrimSpace(thimResp.CertificateChain))
	sb.WriteString("\n")

	log.Printf("THIM: fetched platform certificates (%d bytes)", sb.Len())
	return sb.String(), nil
}
