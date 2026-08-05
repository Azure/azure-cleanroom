// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package blobfuse

import (
	"os"
	"strings"

	log "github.com/sirupsen/logrus"
)

// readTrimmedFile reads a file and returns its trimmed string content.
func readTrimmedFile(path string) (string, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return "", err
	}
	return strings.TrimSpace(string(data)), nil
}

// persistMetadataFile writes one recovery metadata value for later restart handling.
func (mountManager *Manager) persistMetadataFile(mountID, fileName, value, failureMessage string) {
	filePath := mountManager.metadataFilePath(mountID, fileName)
	if err := os.WriteFile(filePath, []byte(value), 0644); err != nil {
		log.WithError(err).WithField("mountID", mountID).Warn(failureMessage)
	}
}

// persistRecoveryMetadata stores the contract ID needed to restore mount state after restarts.
// The proxy now owns identity registration and DEK resolution; the driver only needs the
// contract ID for governance routing on the proxy.
func (mountManager *Manager) persistRecoveryMetadata(mountID string, _ *mountEntry, config *MountConfig) {
	if config.ContractID != "" {
		mountManager.persistMetadataFile(mountID, "contract-id", config.ContractID,
			"Failed to persist contract ID")
	}
}

// loadRecoveryMetadata reads the persisted contract ID for a recovered mount.
func (mountManager *Manager) loadRecoveryMetadata(mountID string) (contractID string) {
	if val, err := readTrimmedFile(mountManager.metadataFilePath(mountID, "contract-id")); err == nil {
		contractID = val
	}
	return contractID
}
