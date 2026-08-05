package verify

import (
	"bytes"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/sha256"
	"crypto/sha512"
	"crypto/x509"
	"encoding/base64"
	"encoding/pem"
	"fmt"
	"math/big"

	"github.com/azure/azure-cleanroom/src/cvm/pkg/hcl"
)

// GpuDeviceInput holds the attestation evidence for a single GPU device.
type GpuDeviceInput struct {
	Evidence    string `json:"evidence"`    // base64-encoded SPDM attestation report
	Certificate string `json:"certificate"` // base64-encoded GPU certificate chain (PEM)
	Arch        string `json:"arch"`        // raw NVAT file-evidence architecture
	Nonce       string `json:"nonce"`       // raw NVAT file-evidence nonce
}

// GpuInput holds GPU attestation evidence for all GPUs on the node.
type GpuInput struct {
	Evidences []GpuDeviceInput `json:"evidences"`
}

// VerifyGPU performs GPU attestation verification for each GPU device:
//  1. Parse and validate the GPU certificate chain (PEM).
//  2. Verify the chain roots to the pinned NVIDIA Device Identity CA.
//  3. Verify the SPDM report signature using the GPU leaf certificate.
//  4. Extract CPU report_data from the SNP report.
//  5. Verify the SPDM nonce matches CPU report_data (session binding).
//  6. Appraise GPU measured state against NVIDIA-signed RIMs via
//     the NVAT local verifier (`nvattest`), including secure-boot
//     and debug-state checks.
//
// TODO: Session binding (step 5) proves the GPU evidence was requested
// in the same attestation session as the CPU evidence. It does not
// prove the GPU and CPU are on the same physical host. True physical
// binding requires platform support (e.g. TPM PCR extend for GPU).
func VerifyGPU(gpuInput *GpuInput, snpReport []byte) []NamedCheck {
	var results []NamedCheck

	if gpuInput == nil {
		return results
	}

	if len(gpuInput.Evidences) == 0 {
		return []NamedCheck{{
			ID: "gpuInput",
			Result: CheckResult{
				Passed: false,
				Error:  "gpu attestation block is present but contains no GPU evidences",
			},
		}}
	}

	cpuReportData, cpuReportDataErr := extractCpuReportDataForGpuNonce(snpReport)

	for i, gpu := range gpuInput.Evidences {
		prefix := fmt.Sprintf("gpu%d", i)
		results = append(results, verifyGpuDevice(prefix, &gpu, cpuReportData, cpuReportDataErr)...)
	}

	results = append(results, verifyGPURIMAppraisal(gpuInput, cpuReportData, cpuReportDataErr)...)

	return results
}

// verifyGpuDevice verifies a single GPU's attestation evidence.
func verifyGpuDevice(
	prefix string,
	gpu *GpuDeviceInput,
	cpuReportData []byte,
	cpuReportDataErr error,
) []NamedCheck {
	var results []NamedCheck
	addCheck := func(id string, check CheckResult) {
		results = append(results, NamedCheck{ID: fmt.Sprintf("%s.%s", prefix, id), Result: check})
	}

	// 1. Decode and parse GPU certificate chain.
	certPEM, err := base64.StdEncoding.DecodeString(gpu.Certificate)
	if err != nil {
		addCheck("certChainParsing", CheckResult{
			Passed: false,
			Error:  fmt.Sprintf("failed to base64-decode GPU certificate: %v", err),
		})
		return results
	}

	certs, err := parseGpuCertificates(certPEM)
	if err != nil {
		addCheck("certChainParsing", CheckResult{
			Passed: false,
			Error:  fmt.Sprintf("failed to parse GPU certificates: %v", err),
		})
		return results
	}

	if len(certs) < 2 {
		addCheck("certChainParsing", CheckResult{
			Passed: false,
			Error:  fmt.Sprintf("expected at least 2 certificates (GPU leaf + root), got %d", len(certs)),
		})
		return results
	}

	gpuLeaf, intermediates, root, err := classifyGpuCerts(certs)
	if err != nil {
		addCheck("certChainParsing", CheckResult{Passed: false, Error: err.Error()})
		return results
	}
	addCheck("certChainParsing", CheckResult{
		Passed: true,
		Detail: fmt.Sprintf("parsed %d certificate(s): leaf=%s, root=%s",
			len(certs), gpuLeaf.Subject.CommonName, root.Subject.CommonName),
	})

	// 2. Verify root matches well-known NVIDIA root CA.
	if err = verifyNvidiaRoot(root); err != nil {
		addCheck("rootTrust", CheckResult{Passed: false, Error: err.Error()})
	} else {
		addCheck("rootTrust", CheckResult{
			Passed: true,
			Detail: fmt.Sprintf("GPU root CA matches well-known NVIDIA root (%s)", root.Subject.CommonName),
		})
	}

	// 3. Validate certificate chain: root → intermediates → leaf.
	if err = verifyGpuCertChain(gpuLeaf, intermediates, root); err != nil {
		addCheck("certChainValidation", CheckResult{Passed: false, Error: err.Error()})
	} else {
		addCheck("certChainValidation", CheckResult{
			Passed: true,
			Detail: "NVIDIA certificate chain valid",
		})
	}

	// 4. Decode and verify SPDM report signature.
	reportBytes, err := base64.StdEncoding.DecodeString(gpu.Evidence)
	if err != nil {
		addCheck("reportSignature", CheckResult{
			Passed: false,
			Error:  fmt.Sprintf("failed to base64-decode SPDM report: %v", err),
		})
		return results
	}

	if err = verifyGpuReportSignature(reportBytes, gpuLeaf); err != nil {
		addCheck("reportSignature", CheckResult{Passed: false, Error: err.Error()})
	} else {
		addCheck("reportSignature", CheckResult{
			Passed: true,
			Detail: fmt.Sprintf("SPDM report signature valid (%d bytes)", len(reportBytes)),
		})
	}

	// 5. Verify SPDM nonce matches CPU report_data (session binding).
	if cpuReportDataErr != nil {
		addCheck("nonceBinding", CheckResult{Passed: false, Error: cpuReportDataErr.Error()})
		return results
	}

	gpuNonce, nonceErr := extractSPDMNonce(reportBytes)
	if nonceErr != nil {
		addCheck("nonceBinding", CheckResult{Passed: false, Error: nonceErr.Error()})
	} else if bytes.Equal(gpuNonce, cpuReportData) {
		addCheck("nonceBinding", CheckResult{
			Passed: true,
			Detail: "SPDM nonce matches CPU report_data (session binding)",
		})
	} else {
		addCheck("nonceBinding", CheckResult{
			Passed: false,
			Error: fmt.Sprintf(
				"nonce mismatch: got %x, expected %x", gpuNonce, cpuReportData),
		})
	}

	return results
}

func extractCpuReportDataForGpuNonce(snpReport []byte) ([]byte, error) {
	if len(snpReport) < hcl.SNPReportDataOffset+spdmNonceSize {
		return nil, fmt.Errorf("CPU SNP report too small for report_data extraction: %d bytes", len(snpReport))
	}

	return snpReport[hcl.SNPReportDataOffset : hcl.SNPReportDataOffset+spdmNonceSize], nil
}

// parseGpuCertificates parses X.509 certificates from a PEM or DER bundle.
// Tries PEM first; falls back to a single DER certificate.
func parseGpuCertificates(data []byte) ([]*x509.Certificate, error) {
	// Try PEM parsing first.
	var certs []*x509.Certificate
	rest := data
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
	if len(certs) > 0 {
		return certs, nil
	}

	// Fallback: try parsing as a single DER certificate.
	c, err := x509.ParseCertificate(data)
	if err != nil {
		return nil, fmt.Errorf("data is neither valid PEM nor DER: %w", err)
	}
	return []*x509.Certificate{c}, nil
}

// classifyGpuCerts identifies the leaf, intermediate(s), and root from a set
// of GPU certificates. The root is the self-signed certificate. The leaf is
// the certificate not used to sign any other certificate in the set.
func classifyGpuCerts(certs []*x509.Certificate) (
	leaf *x509.Certificate,
	intermediates []*x509.Certificate,
	root *x509.Certificate,
	err error,
) {
	// Find the self-signed root.
	for _, c := range certs {
		if checkErr := c.CheckSignatureFrom(c); checkErr == nil {
			root = c
			break
		}
	}
	if root == nil {
		return nil, nil, nil, fmt.Errorf("no self-signed root found in GPU certificate chain")
	}

	// Find which certs are used to sign other certs (these are issuers).
	isIssuer := make(map[*x509.Certificate]bool)
	isIssuer[root] = true
	for _, candidate := range certs {
		if candidate == root {
			continue
		}
		for _, other := range certs {
			if other == candidate {
				continue
			}
			if checkErr := other.CheckSignatureFrom(candidate); checkErr == nil {
				isIssuer[candidate] = true
				break
			}
		}
	}

	// Leaf is the cert that is not an issuer (and not the root).
	for _, c := range certs {
		if c == root {
			continue
		}
		if isIssuer[c] {
			intermediates = append(intermediates, c)
		} else {
			if leaf != nil {
				return nil, nil, nil, fmt.Errorf("multiple leaf certificates found in GPU chain")
			}
			leaf = c
		}
	}

	if leaf == nil {
		return nil, nil, nil, fmt.Errorf("no leaf certificate found in GPU chain")
	}

	return leaf, intermediates, root, nil
}

// verifyNvidiaRoot checks that the provided root certificate's public key
// matches the well-known NVIDIA Device Identity CA (pre-parsed at init).
func verifyNvidiaRoot(root *x509.Certificate) error {
	actualDER, err := x509.MarshalPKIXPublicKey(root.PublicKey)
	if err != nil {
		return fmt.Errorf("marshal provided root public key: %w", err)
	}

	if !bytes.Equal(actualDER, nvidiaDeviceIdentityRootPublicKeyDER) {
		return fmt.Errorf(
			"GPU root CA public key (%s) does not match well-known NVIDIA Device Identity CA",
			root.Subject.CommonName)
	}

	return nil
}

// verifyGpuCertChain validates the GPU certificate chain:
// root (self-signed) → intermediate(s) → leaf.
// NVIDIA chains follow: Root CA → Model → Provisioner → Device → Attestation.
// Intermediates may not be in order, so we walk the chain by finding which
// cert each one was signed by.
func verifyGpuCertChain(leaf *x509.Certificate, intermediates []*x509.Certificate, root *x509.Certificate) error {
	// Root must be self-signed.
	if err := root.CheckSignatureFrom(root); err != nil {
		return fmt.Errorf("GPU root CA is not self-signed: %w", err)
	}

	if len(intermediates) == 0 {
		if err := leaf.CheckSignatureFrom(root); err != nil {
			return fmt.Errorf("GPU leaf not signed by root: %w", err)
		}
		return nil
	}

	// Build an ordered chain from root to leaf by finding the signer of each cert.
	// Start from the leaf and walk up to the root.
	current := leaf
	remaining := make([]*x509.Certificate, len(intermediates))
	copy(remaining, intermediates)

	for len(remaining) > 0 {
		found := false
		for i, candidate := range remaining {
			if err := current.CheckSignatureFrom(candidate); err == nil {
				current = candidate
				remaining = append(remaining[:i], remaining[i+1:]...)
				found = true
				break
			}
		}
		if !found {
			// Try root as the signer of current.
			if err := current.CheckSignatureFrom(root); err == nil {
				break
			}
			return fmt.Errorf("GPU cert (%s) has no signer among remaining intermediates or root",
				current.Subject.CommonName)
		}
	}

	// Final cert in the walk must be signed by root.
	if err := current.CheckSignatureFrom(root); err != nil {
		return fmt.Errorf("GPU chain does not terminate at root: %s not signed by %s: %w",
			current.Subject.CommonName, root.Subject.CommonName, err)
	}

	return nil
}

// SPDM request message layout (DMTF DSP0274 1.1):
//
//	Offset 0: SPDMVersion (1 byte)
//	Offset 1: RequestResponseCode (1 byte)
//	Offset 2: Param1 (1 byte)
//	Offset 3: Param2 (1 byte)
//	Offset 4: Nonce (32 bytes)
//	Offset 36: SlotIDParam (1 byte)
//	Total: 37 bytes
const (
	spdmNonceOffset = 4
	spdmNonceSize   = 32
	spdmRequestSize = 37
)

// extractSPDMNonce extracts the 32-byte nonce from an SPDM GET_MEASUREMENT
// request message (the first 37 bytes of the attestation report).
func extractSPDMNonce(report []byte) ([]byte, error) {
	if len(report) < spdmRequestSize {
		return nil, fmt.Errorf("SPDM report too small for nonce extraction: %d bytes (need >= %d)",
			len(report), spdmRequestSize)
	}
	return report[spdmNonceOffset : spdmNonceOffset+spdmNonceSize], nil
}

// verifyGpuReportSignature verifies the SPDM attestation report signature
// using the GPU leaf certificate's public key.
//
// NVIDIA GPU attestation reports use SPDM (DMTF DSP0274) format. The report
// consists of a GET_MEASUREMENT request (37 bytes) followed by the response.
// The signature covers: request + response[:-signature_size].
//
// Verified against NVIDIA nvtrust (github.com/NVIDIA/nvtrust):
//   - HopperSettings: HashFunction=sha384, signature_length=96
//   - AttestationReport.concatenate(): data = request + response[:-sig_len]
//
// The signature algorithm is determined by the GPU leaf certificate's key:
//   - Hopper (H100): ECDSA-P384-SHA384, signature = 96 bytes (R || S, 48 each)
//   - P-256 curves: ECDSA-P256-SHA256, signature = 64 bytes (R || S, 32 each)
func verifyGpuReportSignature(report []byte, leaf *x509.Certificate) error {
	ecKey, ok := leaf.PublicKey.(*ecdsa.PublicKey)
	if !ok {
		return fmt.Errorf("GPU leaf certificate public key is not ECDSA (CN: %s)", leaf.Subject.CommonName)
	}

	// Determine signature size and hash function based on curve.
	var hashFunc func([]byte) []byte
	var sigComponentBytes int

	switch ecKey.Curve {
	case elliptic.P384():
		sigComponentBytes = 48
		hashFunc = func(data []byte) []byte {
			h := sha512.Sum384(data)
			return h[:]
		}
	case elliptic.P256():
		sigComponentBytes = 32
		hashFunc = func(data []byte) []byte {
			h := sha256.Sum256(data)
			return h[:]
		}
	default:
		return fmt.Errorf("unsupported GPU cert EC curve: %s", ecKey.Curve.Params().Name)
	}

	sigSize := sigComponentBytes * 2 // R || S

	// SPDM report layout (per NVIDIA nvtrust):
	//   [request_message (37 bytes)] [response_message]
	// Signature is the last sigSize bytes of the response.
	// Data to verify = request + response_without_signature
	// i.e., data = report[:len(report) - sigSize]
	if len(report) <= sigSize {
		return fmt.Errorf("SPDM report too small for signature: %d bytes (need > %d)", len(report), sigSize)
	}

	dataToVerify := report[:len(report)-sigSize]
	signature := report[len(report)-sigSize:]

	hash := hashFunc(dataToVerify)

	// SPDM signatures use DER-encoded or raw R||S format.
	// NVIDIA uses raw R||S (big-endian, each sigComponentBytes long).
	rBytes := signature[:sigComponentBytes]
	sBytes := signature[sigComponentBytes:]

	r := new(big.Int).SetBytes(rBytes)
	s := new(big.Int).SetBytes(sBytes)

	if !ecdsa.Verify(ecKey, hash, r, s) {
		return fmt.Errorf("ECDSA signature verification failed on SPDM report (%d bytes, %s curve)",
			len(report), ecKey.Curve.Params().Name)
	}

	return nil
}
