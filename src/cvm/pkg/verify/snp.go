package verify

import (
	"bytes"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/sha512"
	"crypto/x509"
	"encoding/pem"
	"fmt"
	"math/big"
	"strings"

	"github.com/azure/azure-cleanroom/src/cvm/pkg/hcl"
)

// ──────────────────────────────────────────────────────────────────────────────
// SNP report byte offsets & sizes (AMD SEV-SNP ABI, Table 21)
// All multi-byte fields are little-endian.
// ──────────────────────────────────────────────────────────────────────────────

const (
	snpReportDataOffset  = 0x50 // report_data field (64 bytes)
	snpReportDataSize    = 64
	snpSignatureOffset   = 0x2A0 // ECDSA signature (512 bytes)
	snpSignedRegionEnd   = 0x2A0 // bytes [0, 0x2A0) are signed
	snpSigComponentBytes = 48    // P-384 = 48 bytes per ECDSA component
	snpSigPaddedSize     = 72    // each R/S padded to 72 bytes in report
)

// ──────────────────────────────────────────────────────────────────────────────
// Platform certificate parsing
// ──────────────────────────────────────────────────────────────────────────────

// parsePEMCertificates parses all X.509 certificates from a PEM bundle.
func parsePEMCertificates(pemData string) ([]*x509.Certificate, error) {
	var certs []*x509.Certificate
	rest := []byte(pemData)
	for {
		var block *pem.Block
		block, rest = pem.Decode(rest)
		if block == nil {
			break
		}
		c, err := x509.ParseCertificate(block.Bytes)
		if err != nil {
			return nil, fmt.Errorf("parse cert from PEM: %w", err)
		}
		certs = append(certs, c)
	}
	return certs, nil
}

// classifyCerts identifies VCEK, ASK, and ARK from a set of certificates.
// The ARK is the self-signed certificate, the VCEK is the leaf (signed by
// neither itself nor any other intermediate), and the ASK is the intermediate.
func classifyCerts(certs []*x509.Certificate) (vcek, ask, ark *x509.Certificate, err error) {
	if len(certs) < 3 {
		return nil, nil, nil, fmt.Errorf(
			"expected at least 3 certificates (VCEK, ASK, ARK), got %d", len(certs))
	}

	// Identify the self-signed root (ARK).
	for _, c := range certs {
		if err := c.CheckSignatureFrom(c); err == nil {
			ark = c
			break
		}
	}
	if ark == nil {
		return nil, nil, nil, fmt.Errorf("no self-signed root (ARK) found in platform certificates")
	}

	// Identify ASK (signed by ARK, not self-signed) and VCEK (signed by ASK).
	for _, c := range certs {
		if c == ark {
			continue
		}
		if err := c.CheckSignatureFrom(ark); err == nil {
			ask = c
		}
	}
	if ask == nil {
		return nil, nil, nil, fmt.Errorf("no intermediate (ASK) signed by ARK found")
	}

	for _, c := range certs {
		if c == ark || c == ask {
			continue
		}
		if err := c.CheckSignatureFrom(ask); err == nil {
			vcek = c
		}
	}
	if vcek == nil {
		return nil, nil, nil, fmt.Errorf("no leaf (VCEK) signed by ASK found")
	}

	return vcek, ask, ark, nil
}

// ──────────────────────────────────────────────────────────────────────────────
// Certificate chain validation
// ──────────────────────────────────────────────────────────────────────────────

// VerifySNPCertChain validates  ARK (self-signed) → ASK → VCEK.
func VerifySNPCertChain(vcek, ask, ark *x509.Certificate) error {
	// ARK must be self-signed.
	if err := ark.CheckSignatureFrom(ark); err != nil {
		return fmt.Errorf("ARK is not self-signed: %w", err)
	}
	// ASK signed by ARK.
	if err := ask.CheckSignatureFrom(ark); err != nil {
		return fmt.Errorf("ASK not signed by ARK: %w", err)
	}
	// VCEK signed by ASK.
	if err := vcek.CheckSignatureFrom(ask); err != nil {
		return fmt.Errorf("VCEK not signed by ASK: %w", err)
	}
	return nil
}

// detectAMDProduct infers the AMD product name from the ARK certificate's
// CommonName. THIM platform certificates use the naming convention
// "ARK-<product>" (e.g. "ARK-Milan", "ARK-Genoa", "ARK-Turin"). This
// allows the verifier to select the correct well-known root key without
// requiring the caller to know which AMD CPU generation the VM runs on.
// Falls back to "Milan" if the CN does not follow the expected pattern.
func detectAMDProduct(ark *x509.Certificate) string {
	cn := ark.Subject.CommonName
	if strings.HasPrefix(cn, "ARK-") {
		return cn[len("ARK-"):]
	}
	return "Milan"
}

// verifyARKMatchesWellKnown checks that the public key of the provided ARK
// certificate matches a well-known AMD root public key for the given product.
// This mirrors the C# SnpReport.ComparePublicKey approach: only the RSA public
// key material is compared, not the full certificate.
func verifyARKMatchesWellKnown(ark *x509.Certificate, product string) error {
	wellKnownPEM, ok := wellKnownARKPublicKeys[product]
	if !ok {
		return fmt.Errorf("no well-known ARK public key for product %q", product)
	}

	block, _ := pem.Decode([]byte(wellKnownPEM))
	if block == nil {
		return fmt.Errorf("failed to decode well-known ARK public key PEM for %q", product)
	}

	wellKnownPub, err := x509.ParsePKIXPublicKey(block.Bytes)
	if err != nil {
		return fmt.Errorf("parse well-known ARK public key for %q: %w", product, err)
	}

	// Marshal both public keys to PKIX DER for a byte-level comparison.
	expectedDER, err := x509.MarshalPKIXPublicKey(wellKnownPub)
	if err != nil {
		return fmt.Errorf("marshal well-known ARK public key: %w", err)
	}

	actualDER, err := x509.MarshalPKIXPublicKey(ark.PublicKey)
	if err != nil {
		return fmt.Errorf("marshal provided ARK public key: %w", err)
	}

	if !bytes.Equal(actualDER, expectedDER) {
		return fmt.Errorf(
			"provided ARK public key (%s) does not match well-known key for %q",
			ark.Subject.CommonName, product)
	}

	return nil
}

// ──────────────────────────────────────────────────────────────────────────────
// SNP report signature verification
// ──────────────────────────────────────────────────────────────────────────────

// VerifySNPReportSignature checks the ECDSA-P384-SHA384 signature over the
// signed region (bytes 0–0x29F) of the SNP report using the VCEK public key.
//
// The signature components R and S are stored in little-endian format in the
// report, each padded from 48 to 72 bytes.
func VerifySNPReportSignature(snpReport []byte, vcek *x509.Certificate) error {
	if len(snpReport) < hcl.SNPReportSize {
		return fmt.Errorf("SNP report too small: %d bytes", len(snpReport))
	}

	ecKey, ok := vcek.PublicKey.(*ecdsa.PublicKey)
	if !ok {
		return fmt.Errorf("VCEK public key is not ECDSA")
	}
	if ecKey.Curve != elliptic.P384() {
		return fmt.Errorf("VCEK key curve is not P-384")
	}

	// Hash the signed region.
	hash := sha512.Sum384(snpReport[:snpSignedRegionEnd])

	// Extract R (little-endian, 48 useful bytes).
	rBytes := make([]byte, snpSigComponentBytes)
	copy(rBytes, snpReport[snpSignatureOffset:snpSignatureOffset+snpSigComponentBytes])
	reverseBytes(rBytes) // LE → BE

	// Extract S.
	sBytes := make([]byte, snpSigComponentBytes)
	copy(sBytes, snpReport[snpSignatureOffset+snpSigPaddedSize:snpSignatureOffset+snpSigPaddedSize+snpSigComponentBytes])
	reverseBytes(sBytes)

	r := new(big.Int).SetBytes(rBytes)
	s := new(big.Int).SetBytes(sBytes)

	if !ecdsa.Verify(ecKey, hash[:], r, s) {
		return fmt.Errorf("ECDSA-P384-SHA384 signature invalid")
	}
	return nil
}

// ──────────────────────────────────────────────────────────────────────────────
// VerifySNPReport – orchestrates all SNP checks
// ──────────────────────────────────────────────────────────────────────────────

// VerifySNPReport performs the SNP report verification using pre-parsed and
// pre-validated platform certificates:
//  1. Validate ARK → ASK → VCEK chain
//  2. Verify SNP report signature with VCEK
//
// The platform certificates must already be parsed and the ARK verified against
// the well-known AMD root before calling this function (done in VerifyAll).
//
// Returns a slice of NamedCheck so individual failures are visible in order.
func VerifySNPReport(
	snpReport []byte,
	vcek, ask, ark *x509.Certificate,
) []NamedCheck {
	var results []NamedCheck

	addCheck := func(id string, check CheckResult) {
		results = append(results, NamedCheck{ID: id, Result: check})
	}

	if len(snpReport) < hcl.SNPReportSize {
		addCheck("snpReportFormat", CheckResult{
			Passed: false,
			Error:  fmt.Sprintf("SNP report too small: %d bytes (need %d)", len(snpReport), hcl.SNPReportSize),
		})
		return results
	}
	addCheck("snpReportFormat", CheckResult{
		Passed: true,
		Detail: fmt.Sprintf("%d bytes, signed region 0x000–0x%03X", len(snpReport), snpSignedRegionEnd-1),
	})

	// 1. Validate cert chain.
	if err := VerifySNPCertChain(vcek, ask, ark); err != nil {
		addCheck("certChainValidation", CheckResult{Passed: false, Error: err.Error()})
	} else {
		addCheck("certChainValidation", CheckResult{
			Passed: true,
			Detail: "ARK → ASK → VCEK chain valid",
		})
	}

	// 2. Verify SNP report signature.
	if err := VerifySNPReportSignature(snpReport, vcek); err != nil {
		addCheck("snpSignature", CheckResult{Passed: false, Error: err.Error()})
	} else {
		addCheck("snpSignature", CheckResult{
			Passed: true,
			Detail: "ECDSA-P384-SHA384 signature valid",
		})
	}

	return results
}

// ──────────────────────────────────────────────────────────────────────────────
// Helpers
// ──────────────────────────────────────────────────────────────────────────────

// reverseBytes reverses a byte slice in-place (little-endian ↔ big-endian).
func reverseBytes(b []byte) {
	for i, j := 0, len(b)-1; i < j; i, j = i+1, j-1 {
		b[i], b[j] = b[j], b[i]
	}
}
