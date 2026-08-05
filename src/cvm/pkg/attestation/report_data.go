package attestation

import (
	"bytes"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"fmt"
)

const (
	// sha256Size is the size of a SHA-256 digest in bytes.
	sha256Size = 32
)

// UserDataDocument is the user data document whose SHA-256 hash occupies
// report_data[0:32]. It captures the caller's report data, GPU count,
// and a schema version. Fields are serialized in struct declaration order
// with compact JSON (no whitespace) to ensure deterministic hashing.
type UserDataDocument struct {
	Version  int    `json:"version"`
	ReportData string `json:"reportData"` // base64-encoded caller report data payload from /snp/attest request
	GPUCount int    `json:"gpuCount"`
}

// CurrentDocumentVersion is the only accepted document schema version.
const CurrentDocumentVersion = 1

// BuildReportData constructs the full 64-byte report_data from a populated
// user data document. The layout is:
//
//	report_data[0:32]  = SHA256(canonical JSON)
//	report_data[32:64] = zeros
//
// The caller must populate all fields (Version, ReportData, GPUCount)
// before calling this function. Returns the report_data and the canonical
// JSON bytes used for hashing.
func BuildReportData(doc *UserDataDocument) ([]byte, []byte, error) {
	canonical, err := json.Marshal(doc)
	if err != nil {
		return nil, nil, fmt.Errorf("marshal user data document: %w", err)
	}

	docHash := sha256.Sum256(canonical)

	reportData := make([]byte, ReportDataSize)
	copy(reportData[0:sha256Size], docHash[:])
	// report_data[32:64] remains zeros.

	return reportData, canonical, nil
}

// VerifyUserDataDocumentBinding checks that SHA256(userDataDocumentJSON)
// matches user-data[0:32] and that user-data[32:64] are all zeros. Returns
// the parsed and validated document on success.
func VerifyUserDataDocumentBinding(userData string, userDataDocumentJSON []byte) (*UserDataDocument, error) {
	if len(userData) != ReportDataSize*2 {
		return nil, fmt.Errorf("user-data must be exactly %d hex chars, got %d",
			ReportDataSize*2, len(userData))
	}

	reportData, err := hex.DecodeString(userData)
	if err != nil {
		return nil, fmt.Errorf("decode user-data hex: %w", err)
	}

	// Verify SHA256(user data document) matches user-data[0:32].
	computedHash := sha256.Sum256(userDataDocumentJSON)
	if !bytes.Equal(computedHash[:], reportData[0:sha256Size]) {
		return nil, fmt.Errorf(
			"hash mismatch: user-data[0:32]=%x, SHA256(document)=%x",
			reportData[0:sha256Size], computedHash[:])
	}

	// Verify user-data[32:64] are all zeros.
	if !bytes.Equal(reportData[sha256Size:], make([]byte, sha256Size)) {
		return nil, fmt.Errorf(
			"user-data[32:64] must be all zeros, got %x",
			reportData[sha256Size:])
	}

	var doc UserDataDocument
	if unmarshalErr := json.Unmarshal(userDataDocumentJSON, &doc); unmarshalErr != nil {
		return nil, fmt.Errorf("parse user data document: %w", unmarshalErr)
	}

	if doc.Version != CurrentDocumentVersion {
		return nil, fmt.Errorf("unsupported document version %d, expected %d",
			doc.Version, CurrentDocumentVersion)
	}

	if doc.GPUCount < 0 {
		return nil, fmt.Errorf("invalid gpuCount %d: must be non-negative",
			doc.GPUCount)
	}

	// Validate ReportData: must be present, valid base64, exactly 64 bytes.
	if doc.ReportData == "" {
		return nil, fmt.Errorf("user data document missing reportData field")
	}

	rdBytes, err := base64.StdEncoding.DecodeString(doc.ReportData)
	if err != nil {
		return nil, fmt.Errorf("invalid base64 in reportData: %w", err)
	}

	if len(rdBytes) != ReportDataSize {
		return nil, fmt.Errorf("reportData must decode to exactly %d bytes, got %d",
			ReportDataSize, len(rdBytes))
	}

	return &doc, nil
}
