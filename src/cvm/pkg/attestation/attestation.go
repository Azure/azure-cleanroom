// Package attestation provides core TPM attestation functionality for Azure
// Confidential VMs. It reads the Azure-provisioned AIK, generates TPM quotes,
// and retrieves the HCL/SNP attestation report from vTPM NVRAM.
package attestation

import (
	"bytes"
	"encoding/json"
	"fmt"
	"log"
	"time"

	"github.com/azure/azure-cleanroom/src/cvm/pkg/hcl"
	"github.com/google/go-tpm/tpm2"
	"github.com/google/go-tpm/tpm2/transport"
	"github.com/google/go-tpm/tpm2/transport/linuxtpm"
)

const (
	// TPMDevice is the default TPM resource manager device path.
	TPMDevice = "/dev/tpmrm0"

	// AIKPersistentHandle is the Azure CVM pre-provisioned AIK public key handle.
	AIKPersistentHandle = 0x81000003

	// AIKCertNVIndex is the NV index holding the AIK x.509 certificate.
	AIKCertNVIndex = 0x01C101D0

	// HCLReportNVIndex is the NV index where the HCL firmware stores
	// the SNP attestation report on Azure CVMs.
	HCLReportNVIndex = 0x01400001

	// ReportDataNVIndex is the NV index used to trigger SNP report regeneration
	// with custom report_data on Azure CVMs. Writing 64 bytes here causes the
	// HCL firmware to generate a fresh SNP report at HCLReportNVIndex.
	ReportDataNVIndex = 0x01400002

	// ReportDataSize is the size of the report_data field in an SNP report.
	ReportDataSize = 64

	// ReportDataRefreshDelay is the time to wait after writing report_data
	// for the HCL firmware to regenerate the SNP report.
	ReportDataRefreshDelay = 3 * time.Second
)

// RuntimeClaims represents the JSON runtime claims embedded in the HCL report's
// Runtime Data section. These claims are endorsed by the hardware report via
// the report_data hash binding.
// See: https://learn.microsoft.com/en-us/azure/confidential-computing/guest-attestation-confidential-virtual-machines-design
type RuntimeClaims struct {
	Keys            []json.RawMessage `json:"keys"`             // JWK keys (HCLAkPub, HCLEkPub)
	VMConfiguration *VMConfiguration  `json:"vm-configuration"` // Azure CVM configuration
	UserData        string            `json:"user-data"`        // 64-byte hex string from NV 0x01400002
}

// VMConfiguration holds selective Azure CVM configuration from the runtime claims.
type VMConfiguration struct {
	RootCertThumbprint string `json:"root-cert-thumbprint"`
	ConsoleEnabled     bool   `json:"console-enabled"`
	SecureBoot         bool   `json:"secure-boot"`
	TPMEnabled         bool   `json:"tpm-enabled"`
	TPMPersisted       bool   `json:"tpm-persisted"`
	VMUniqueID         string `json:"vmUniqueId"`
}

// DefaultPCRs is the default set of PCR slots (0-23), matching azure-cvm-tooling.
var DefaultPCRs = []int{0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23}

// PCRValues maps PCR indices to their SHA256 digest values.
type PCRValues map[int][]byte

// BuildPCRSelection creates a TPMLPCRSelection for the given PCR indices.
// If pcrSlots is nil or empty, DefaultPCRs (0-23) are selected.
func BuildPCRSelection(pcrSlots []int) tpm2.TPMLPCRSelection {
	if len(pcrSlots) == 0 {
		pcrSlots = DefaultPCRs
	}
	uints := make([]uint, len(pcrSlots))
	for i, s := range pcrSlots {
		uints[i] = uint(s)
	}
	return tpm2.TPMLPCRSelection{
		PCRSelections: []tpm2.TPMSPCRSelection{
			{
				Hash:      tpm2.TPMAlgSHA256,
				PCRSelect: tpm2.PCClientCompatible.PCRs(uints...),
			},
		},
	}
}

// Evidence holds the collected attestation artifacts from an Azure CVM.
type Evidence struct {
	TPMQuote      []byte         // Marshalled TPM quote (quoted + signature)
	HCLReport     []byte         // Full HCL report blob from NVRAM
	SNPReport     []byte         // Raw AMD SNP report extracted from HCL report
	AIKCert       []byte         // AIK x.509 certificate (DER)
	PCRs          PCRValues      // SHA256 PCR values for selected slots
	RuntimeClaims *RuntimeClaims // Parsed runtime claims from HCL report
}

// CollectEvidence opens the TPM, reads the Azure-provisioned AIK, generates
// a TPM Quote over the specified PCRs using the given nonce, and retrieves
// the HCL/SNP report from vTPM NVRAM.
// If pcrSlots is nil, the default PCRs (0-23) are included.
func CollectEvidence(nonce []byte, pcrSlots []int) (*Evidence, error) {
	// 1. Open TPM
	tpmDev, err := linuxtpm.Open(TPMDevice)
	if err != nil {
		return nil, fmt.Errorf("open TPM: %w", err)
	}
	defer tpmDev.Close()

	return collectEvidenceFromTPM(tpmDev, nonce, pcrSlots)
}

// CollectEvidenceWithReportData is like CollectEvidence but writes custom
// report_data to NV index 0x01400002 to trigger fresh SNP report generation.
// The reportData must be exactly 64 bytes. The resulting SNP report will have
// the provided reportData bound into its report_data field, creating a direct
// cryptographic binding between the caller's data and the hardware attestation.
// This follows the pattern from az-snp-vtpm: write to 0x01400002, wait for
// HCL firmware regeneration, then read the fresh report from 0x01400001.
// If pcrSlots is nil, the default PCRs (0-23) are included.
func CollectEvidenceWithReportData(nonce []byte, reportData []byte, pcrSlots []int) (*Evidence, error) {
	if len(reportData) != ReportDataSize {
		return nil, fmt.Errorf("reportData must be exactly %d bytes, got %d", ReportDataSize, len(reportData))
	}

	tpmDev, err := linuxtpm.Open(TPMDevice)
	if err != nil {
		return nil, fmt.Errorf("open TPM: %w", err)
	}
	defer tpmDev.Close()

	// Write report_data to trigger fresh SNP report generation
	if err := NVWriteData(tpmDev, tpm2.TPMHandle(ReportDataNVIndex), reportData); err != nil {
		return nil, fmt.Errorf("write report_data to 0x%08x: %w", ReportDataNVIndex, err)
	}

	// Wait for HCL firmware to regenerate the SNP report
	log.Printf("Waiting %v for HCL firmware to regenerate SNP report...", ReportDataRefreshDelay)
	time.Sleep(ReportDataRefreshDelay)

	return collectEvidenceFromTPM(tpmDev, nonce, pcrSlots)
}

// collectEvidenceFromTPM performs the attestation using an already-opened TPM.
// If pcrSlots is nil, the default PCRs (0-23) are selected.
func collectEvidenceFromTPM(tpm transport.TPM, nonce []byte, pcrSlots []int) (*Evidence, error) {
	akHandle := tpm2.TPMHandle(AIKPersistentHandle)

	// 2. Read the pre-provisioned AIK
	akName, exists := ReadPersistentHandle(tpm, akHandle)
	if !exists {
		return nil, fmt.Errorf("Azure-provisioned AIK not found at handle 0x%08x", AIKPersistentHandle)
	}
	log.Println("Azure-provisioned AIK found.")

	// 3. Read AIK certificate from NV index
	aikCert, err := NVRead(tpm, tpm2.TPMHandle(AIKCertNVIndex))
	if err != nil {
		return nil, fmt.Errorf("read AIK cert from NV 0x%08x: %w", AIKCertNVIndex, err)
	}
	log.Printf("AIK certificate: %d bytes", len(aikCert))

	// 4. Generate TPM Quote over selected PCRs
	pcrSelection := BuildPCRSelection(pcrSlots)
	log.Printf("PCR selection: %v", pcrSlots)
	quoteRsp, err := tpm2.Quote{
		SignHandle: tpm2.AuthHandle{
			Handle: akHandle,
			Name:   akName,
			Auth:   tpm2.PasswordAuth(nil),
		},
		QualifyingData: tpm2.TPM2BData{Buffer: nonce},
		InScheme: tpm2.TPMTSigScheme{
			Scheme: tpm2.TPMAlgNull,
		},
		PCRSelect: pcrSelection,
	}.Execute(tpm)
	if err != nil {
		return nil, fmt.Errorf("TPM Quote: %w", err)
	}

	quotedBytes := tpm2.Marshal(quoteRsp.Quoted)
	sigBytes := tpm2.Marshal(quoteRsp.Signature)

	var quoteBlob bytes.Buffer
	quoteBlob.Write(quotedBytes)
	quoteBlob.Write(sigBytes)

	log.Println("TPM Quote generated.")

	// 5. Read selected SHA256 PCR values to include in evidence
	pcrs, err := ReadPCRs(tpm, pcrSlots)
	if err != nil {
		return nil, fmt.Errorf("read PCRs: %w", err)
	}
	log.Printf("Read %d PCR values", len(pcrs))

	// 6. Read HCL report from vTPM NVRAM (contains SNP report)
	hclBlob, err := GetHCLReport(tpm)
	if err != nil {
		return nil, fmt.Errorf("read HCL report: %w", err)
	}
	log.Printf("HCL report size: %d bytes", len(hclBlob))

	snpReport, err := hcl.ExtractSNPReport(hclBlob)
	if err != nil {
		return nil, err
	}
	log.Printf("SNP report extracted: %d bytes", len(snpReport))

	// 7. Parse runtime claims from HCL report
	runtimeClaims, err := ParseRuntimeClaims(hclBlob)
	if err != nil {
		return nil, fmt.Errorf("parse runtime claims: %w", err)
	}
	log.Printf("Runtime claims parsed: %d keys, user-data length=%d",
		len(runtimeClaims.Keys), len(runtimeClaims.UserData))
	if runtimeClaims.VMConfiguration != nil {
		log.Printf("VM config: secure-boot=%v, tpm-enabled=%v, vmUniqueId=%s",
			runtimeClaims.VMConfiguration.SecureBoot,
			runtimeClaims.VMConfiguration.TPMEnabled,
			runtimeClaims.VMConfiguration.VMUniqueID)
	}

	return &Evidence{
		TPMQuote:      quoteBlob.Bytes(),
		HCLReport:     hclBlob,
		SNPReport:     snpReport,
		AIKCert:       aikCert,
		PCRs:          pcrs,
		RuntimeClaims: runtimeClaims,
	}, nil
}

// ReadPCRs reads all PCR values from the TPM for the given selection.
// The TPM may return PCRs in batches, so this loops until all are read.
// This matches the approach from azure-cvm-tooling which reads PCR values
// alongside the quote for verification.
func ReadPCRs(tpm transport.TPM, pcrSlots []int) (PCRValues, error) {
	if len(pcrSlots) == 0 {
		pcrSlots = DefaultPCRs
	}
	pcrs := make(PCRValues, len(pcrSlots))

	for _, i := range pcrSlots {
		pcrReadRsp, err := tpm2.PCRRead{
			PCRSelectionIn: tpm2.TPMLPCRSelection{
				PCRSelections: []tpm2.TPMSPCRSelection{
					{
						Hash:      tpm2.TPMAlgSHA256,
						PCRSelect: tpm2.PCClientCompatible.PCRs(uint(i)),
					},
				},
			},
		}.Execute(tpm)
		if err != nil {
			return nil, fmt.Errorf("PCRRead(%d): %w", i, err)
		}

		digests := pcrReadRsp.PCRValues.Digests
		if len(digests) > 0 {
			pcrs[i] = digests[0].Buffer
		}
	}

	return pcrs, nil
}

// ReadPersistentHandle reads the public area of a persistent TPM handle.
// Returns the name and true if the handle exists.
func ReadPersistentHandle(tpm transport.TPM, handle tpm2.TPMHandle) (tpm2.TPM2BName, bool) {
	readPub, err := tpm2.ReadPublic{
		ObjectHandle: handle,
	}.Execute(tpm)
	if err != nil {
		return tpm2.TPM2BName{}, false
	}
	return readPub.Name, true
}

// GetHCLReport reads the HCL report from vTPM NVRAM at the well-known NV index.
// The HCL firmware on Azure CVMs writes the SNP attestation report here at boot.
// The data has a 32-byte header followed by the raw 1184-byte AMD SNP report.
// Equivalent to: tpm2_nvread -C o 0x01400001
func GetHCLReport(tpm transport.TPM) ([]byte, error) {
	return NVRead(tpm, tpm2.TPMHandle(HCLReportNVIndex))
}

// GetHCLReportWithReportData writes custom report_data to the trigger NV index
// (0x01400002), waits for the HCL firmware to regenerate the SNP report, and
// reads the fresh HCL report from 0x01400001.
func GetHCLReportWithReportData(tpm transport.TPM, reportData []byte) ([]byte, error) {
	if len(reportData) != ReportDataSize {
		return nil, fmt.Errorf("reportData must be exactly %d bytes, got %d", ReportDataSize, len(reportData))
	}

	if err := NVWriteData(tpm, tpm2.TPMHandle(ReportDataNVIndex), reportData); err != nil {
		return nil, fmt.Errorf("write report_data: %w", err)
	}

	log.Printf("Waiting %v for HCL firmware to regenerate SNP report...", ReportDataRefreshDelay)
	time.Sleep(ReportDataRefreshDelay)

	return GetHCLReport(tpm)
}

// ParseRuntimeClaims extracts and parses the JSON runtime claims from an HCL
// report blob.  Uses the shared hcl package for binary layout parsing.
func ParseRuntimeClaims(hclBlob []byte) (*RuntimeClaims, error) {
	hdr, err := hcl.ParseRuntimeDataHeader(hclBlob)
	if err != nil {
		return nil, err
	}
	log.Printf("Runtime data: version=%d, reportType=%d, hashType=%d, claimSize=%d",
		hdr.Version, hdr.ReportType, hdr.HashType, hdr.ClaimSize)

	claimsJSON, err := hcl.ExtractRuntimeClaimsRaw(hclBlob)
	if err != nil {
		return nil, err
	}

	var claims RuntimeClaims
	if err := json.Unmarshal(claimsJSON, &claims); err != nil {
		return nil, fmt.Errorf("unmarshal runtime claims JSON: %w", err)
	}

	return &claims, nil
}

// NVRead reads the full contents of any NV index in chunks (TPM max NV buffer
// is typically 1024 bytes). Equivalent to: tpm2_nvread -C o <index>
func NVRead(tpm transport.TPM, nvIndex tpm2.TPMHandle) ([]byte, error) {
	// Read NV public to discover the data size and NV name
	nvPubRsp, err := tpm2.NVReadPublic{
		NVIndex: nvIndex,
	}.Execute(tpm)
	if err != nil {
		return nil, fmt.Errorf("NVReadPublic(0x%08x): %w", nvIndex, err)
	}

	nvPublic, err := nvPubRsp.NVPublic.Contents()
	if err != nil {
		return nil, fmt.Errorf("parse NV public: %w", err)
	}

	totalSize := int(nvPublic.DataSize)
	log.Printf("NV index 0x%08x: %d bytes", nvIndex, totalSize)

	// Read NV data in chunks
	const maxChunk = 1024
	data := make([]byte, 0, totalSize)

	for offset := 0; offset < totalSize; {
		chunkSize := totalSize - offset
		if chunkSize > maxChunk {
			chunkSize = maxChunk
		}

		readRsp, err := tpm2.NVRead{
			AuthHandle: tpm2.AuthHandle{
				Handle: tpm2.TPMRHOwner,
				Auth:   tpm2.PasswordAuth(nil),
			},
			NVIndex: tpm2.NamedHandle{
				Handle: nvIndex,
				Name:   nvPubRsp.NVName,
			},
			Size:   uint16(chunkSize),
			Offset: uint16(offset),
		}.Execute(tpm)
		if err != nil {
			return nil, fmt.Errorf("NVRead at offset %d: %w", offset, err)
		}

		data = append(data, readRsp.Data.Buffer...)
		offset += len(readRsp.Data.Buffer)
	}

	return data, nil
}

// NVWriteData writes data to a TPM NV index, creating or resizing the NV space
// as needed. This handles the full lifecycle: check existence, undefine if wrong
// size, define if needed, then write. Matches the write_nv_index pattern from
// az-snp-vtpm (kinvolk/azure-cvm-tooling).
func NVWriteData(tpm transport.TPM, nvIndex tpm2.TPMHandle, data []byte) error {
	needsDefine := false

	// Check if NV index already exists
	nvPubRsp, err := tpm2.NVReadPublic{
		NVIndex: nvIndex,
	}.Execute(tpm)
	if err != nil {
		// NV index doesn't exist — need to define it
		needsDefine = true
	} else {
		nvPublic, err := nvPubRsp.NVPublic.Contents()
		if err != nil {
			needsDefine = true
		} else if int(nvPublic.DataSize) != len(data) {
			// Size mismatch — undefine and redefine
			log.Printf("NV index 0x%08x exists with size %d, need %d — redefining",
				nvIndex, nvPublic.DataSize, len(data))
			_, undefErr := tpm2.NVUndefineSpace{
				AuthHandle: tpm2.AuthHandle{
					Handle: tpm2.TPMRHOwner,
					Auth:   tpm2.PasswordAuth(nil),
				},
				NVIndex: tpm2.NamedHandle{
					Handle: nvIndex,
					Name:   nvPubRsp.NVName,
				},
			}.Execute(tpm)
			if undefErr != nil {
				return fmt.Errorf("NVUndefineSpace(0x%08x): %w", nvIndex, undefErr)
			}
			needsDefine = true
		}
	}

	if needsDefine {
		log.Printf("Defining NV index 0x%08x with size %d", nvIndex, len(data))
		_, err := tpm2.NVDefineSpace{
			AuthHandle: tpm2.AuthHandle{
				Handle: tpm2.TPMRHOwner,
				Auth:   tpm2.PasswordAuth(nil),
			},
			PublicInfo: tpm2.New2B(tpm2.TPMSNVPublic{
				NVIndex: nvIndex,
				NameAlg: tpm2.TPMAlgSHA256,
				Attributes: tpm2.TPMANV{
					OwnerWrite: true,
					OwnerRead:  true,
				},
				DataSize: uint16(len(data)),
			}),
		}.Execute(tpm)
		if err != nil {
			return fmt.Errorf("NVDefineSpace(0x%08x): %w", nvIndex, err)
		}
	}

	// Re-read NV public to get the current name for the write command
	nvPubRsp, err = tpm2.NVReadPublic{
		NVIndex: nvIndex,
	}.Execute(tpm)
	if err != nil {
		return fmt.Errorf("NVReadPublic after define: %w", err)
	}

	// Write data to NV index
	_, err = tpm2.NVWrite{
		AuthHandle: tpm2.AuthHandle{
			Handle: tpm2.TPMRHOwner,
			Auth:   tpm2.PasswordAuth(nil),
		},
		NVIndex: tpm2.NamedHandle{
			Handle: nvIndex,
			Name:   nvPubRsp.NVName,
		},
		Data: tpm2.TPM2BMaxNVBuffer{
			Buffer: data,
		},
		Offset: 0,
	}.Execute(tpm)
	if err != nil {
		return fmt.Errorf("NVWrite(0x%08x): %w", nvIndex, err)
	}

	log.Printf("Wrote %d bytes to NV index 0x%08x", len(data), nvIndex)
	return nil
}
