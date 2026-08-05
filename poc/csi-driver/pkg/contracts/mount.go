// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package contracts

import (
	"crypto/sha256"
	"fmt"
	"strconv"
	"strings"
)

type EncryptionMode string

const (
	EncryptionModeCPK EncryptionMode = "CPK"
	EncryptionModeCSE EncryptionMode = "CSE"
	EncryptionModeSSE EncryptionMode = "SSE"
)

type MountSpec struct {
	StorageAccount      string
	StorageContainer    string
	StorageBlobEndpoint string
	ClientID            string
	TenantID            string
	Subject             string
	ContractID          string
	EncryptionMode      string
	WrappedDekSecret    string
	WrappedDekAkvEP     string
	KID                 string
	AkvEndpoint         string
	MaaEndpoint         string
	ReadOnly            bool
	UseAdls             bool
	SubDirectory        string
	BlockSizeMB         int
	AccessName          string
}

func (s *MountSpec) Normalize() {
	if s.EncryptionMode == "" {
		s.EncryptionMode = string(EncryptionModeCPK)
	}
	if s.BlockSizeMB <= 0 {
		s.BlockSizeMB = 16
	}
}

func (s MountSpec) Validate() error {
	if strings.TrimSpace(s.StorageAccount) == "" {
		return fmt.Errorf("missing required attribute: storageAccount")
	}
	if strings.TrimSpace(s.StorageContainer) == "" {
		return fmt.Errorf("missing required attribute: storageContainer")
	}
	switch strings.ToUpper(s.EncryptionMode) {
	case string(EncryptionModeCPK), string(EncryptionModeCSE), string(EncryptionModeSSE):
	default:
		return fmt.Errorf("invalid encryptionMode %q", s.EncryptionMode)
	}
	return nil
}

// MountID returns a 16-character hex identifier for this mount configuration.
// It is used as both the in-memory map key and the staging directory name on disk.
//
// A null byte (\x00) is written after each field to prevent boundary collisions:
// without it, hash("ab" + "c") == hash("a" + "bc") because the hasher sees the
// same byte sequence. The null byte acts as an unambiguous field delimiter since
// Kubernetes volumeAttributes are always printable strings.
func (s MountSpec) MountID() string {
	h := sha256.New()
	fields := []string{
		s.StorageAccount,
		s.StorageContainer,
		s.StorageBlobEndpoint,
		s.ClientID,
		s.TenantID,
		s.ContractID,
		s.EncryptionMode,
		s.WrappedDekSecret,
		s.WrappedDekAkvEP,
		s.KID,
		s.AkvEndpoint,
		s.MaaEndpoint,
		fmt.Sprintf("%t", s.ReadOnly),
		fmt.Sprintf("%t", s.UseAdls),
		s.SubDirectory,
		fmt.Sprintf("%d", s.BlockSizeMB),
	}
	for _, f := range fields {
		h.Write([]byte(f))
		h.Write([]byte{0})
	}
	return fmt.Sprintf("%x", h.Sum(nil))[:16]
}

// StorageKey returns a 16-character hex identifier derived only from storage coordinates
// and encryption mode. It is used temporarily for write-conflict detection across mounts
// that share the same storage account/container but have different MountIDs.
//
// Deprecated: will be removed together with cross-ID write-conflict detection.
func (s MountSpec) StorageKey() string {
	h := sha256.New()
	fields := []string{
		s.StorageAccount,
		s.StorageContainer,
		s.SubDirectory,
		s.EncryptionMode,
	}
	for _, f := range fields {
		h.Write([]byte(f))
		h.Write([]byte{0})
	}
	return fmt.Sprintf("%x", h.Sum(nil))[:16]
}

// ParseNodeVolumeAttributes parses Kubernetes CSI volumeAttributes into a MountSpec.
//
// Attribute handling follows three tiers:
//   - Required fields (storageAccount, storageContainer): read directly from attrs;
//     an empty value is caught by Validate().
//   - Optional with non-zero default (encryptionMode → "CPK", blockSizeMb → 16):
//     use attrDefault / parseIntAttr so the default is explicit at the call site.
//   - Optional with zero/false default (useAdls, subDirectory, etc.):
//     read directly from attrs; missing key returns Go zero value naturally.
func ParseNodeVolumeAttributes(attrs map[string]string, readOnly bool) (MountSpec, error) {
	spec := MountSpec{
		StorageAccount:      attrs["storageAccount"],
		StorageContainer:    attrs["storageContainer"],
		StorageBlobEndpoint: attrs["storageBlobEndpoint"],
		ClientID:            attrs["clientId"],
		TenantID:            attrs["tenantId"],
		Subject:             attrs["subject"],
		ContractID:          attrs["contractId"],
		EncryptionMode:      attrDefault(attrs, "encryptionMode", string(EncryptionModeCPK)),
		WrappedDekSecret:    attrs["wrappedDekSecret"],
		WrappedDekAkvEP:     attrs["wrappedDekAkvEndpoint"],
		KID:                 attrs["kid"],
		AkvEndpoint:         attrs["akvEndpoint"],
		MaaEndpoint:         attrs["maaEndpoint"],
		ReadOnly:            readOnly,
		SubDirectory:        attrs["subDirectory"],
		AccessName:          attrs["accessName"],
	}

	useAdls, err := parseBoolAttr(attrs, "useAdls", false)
	if err != nil {
		return MountSpec{}, err
	}
	spec.UseAdls = useAdls

	blockSize, err := parseIntAttr(attrs, "blockSizeMb", 16)
	if err != nil {
		return MountSpec{}, err
	}
	spec.BlockSizeMB = blockSize

	spec.Normalize()
	if err := spec.Validate(); err != nil {
		return MountSpec{}, err
	}
	return spec, nil
}

func attrDefault(attrs map[string]string, key, defaultVal string) string {
	if val, ok := attrs[key]; ok && val != "" {
		return val
	}
	return defaultVal
}

func parseBoolAttr(attrs map[string]string, key string, defaultVal bool) (bool, error) {
	v, ok := attrs[key]
	if !ok || strings.TrimSpace(v) == "" {
		return defaultVal, nil
	}
	p, err := strconv.ParseBool(v)
	if err != nil {
		return false, fmt.Errorf("invalid boolean for %s: %w", key, err)
	}
	return p, nil
}

func parseIntAttr(attrs map[string]string, key string, defaultVal int) (int, error) {
	v, ok := attrs[key]
	if !ok || strings.TrimSpace(v) == "" {
		return defaultVal, nil
	}
	p, err := strconv.Atoi(v)
	if err != nil {
		return 0, fmt.Errorf("invalid integer for %s: %w", key, err)
	}
	if p <= 0 {
		return 0, fmt.Errorf("%s must be > 0", key)
	}
	return p, nil
}
