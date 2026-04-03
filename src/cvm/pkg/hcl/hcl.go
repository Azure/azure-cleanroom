// Package hcl provides constants and helpers for parsing Azure HCL
// (Host Compatibility Layer) attestation report blobs from vTPM NVRAM.
//
// The HCL report layout is:
//
//	[0:32]    HCL header
//	[32:1216] AMD SNP attestation report (1184 bytes)
//	[1216:]   Runtime Data section (20-byte header + JSON claims)
package hcl

import (
	"bytes"
	"encoding/binary"
	"fmt"
)

// ──────────────────────────────────────────────────────────────────────────────
// HCL report layout constants
// ──────────────────────────────────────────────────────────────────────────────

const (
	// HeaderSize is the size of the HCL header before the SNP report.
	HeaderSize = 32

	// SNPReportSize is the size of the raw AMD SNP attestation report.
	SNPReportSize = 1184

	// RuntimeDataHeaderSize is the size of the Runtime Data binary header
	// that precedes the JSON claims (5 × uint32).
	RuntimeDataHeaderSize = 20

	// RuntimeDataOffset is the byte offset where the Runtime Data section
	// starts within the HCL blob.
	RuntimeDataOffset = HeaderSize + SNPReportSize
)

// RuntimeDataHeader is the binary header of the Runtime Data section in the
// HCL report. It immediately follows the SNP report payload at offset 1216.
type RuntimeDataHeader struct {
	DataSize   uint32
	Version    uint32
	ReportType uint32
	HashType   uint32
	ClaimSize  uint32
}

// ExtractSNPReport returns the raw 1184-byte SNP report from an HCL blob.
func ExtractSNPReport(hclBlob []byte) ([]byte, error) {
	minSize := HeaderSize + SNPReportSize
	if len(hclBlob) < minSize {
		return nil, fmt.Errorf("HCL report too small (%d bytes), expected at least %d", len(hclBlob), minSize)
	}
	return hclBlob[HeaderSize : HeaderSize+SNPReportSize], nil
}

// ParseRuntimeDataHeader reads the RuntimeDataHeader from an HCL blob.
func ParseRuntimeDataHeader(hclBlob []byte) (*RuntimeDataHeader, error) {
	if len(hclBlob) < RuntimeDataOffset+RuntimeDataHeaderSize {
		return nil, fmt.Errorf("HCL report too small for runtime data header: %d bytes", len(hclBlob))
	}
	var hdr RuntimeDataHeader
	r := bytes.NewReader(hclBlob[RuntimeDataOffset : RuntimeDataOffset+RuntimeDataHeaderSize])
	if err := binary.Read(r, binary.LittleEndian, &hdr); err != nil {
		return nil, fmt.Errorf("read runtime data header: %w", err)
	}
	return &hdr, nil
}

// ExtractRuntimeClaimsRaw returns the raw JSON claims bytes from an HCL blob.
// The claims start after the 20-byte runtime data header and are ClaimSize bytes long.
func ExtractRuntimeClaimsRaw(hclBlob []byte) ([]byte, error) {
	hdr, err := ParseRuntimeDataHeader(hclBlob)
	if err != nil {
		return nil, err
	}
	claimsStart := RuntimeDataOffset + RuntimeDataHeaderSize
	claimsEnd := claimsStart + int(hdr.ClaimSize)
	if claimsEnd > len(hclBlob) {
		return nil, fmt.Errorf("runtime claims exceed HCL blob: need %d, have %d", claimsEnd, len(hclBlob))
	}
	return hclBlob[claimsStart:claimsEnd], nil
}
