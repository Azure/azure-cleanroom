// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package blobfuse

import (
	"fmt"
	"os"
	"os/exec"

	log "github.com/sirupsen/logrus"
)

// stagingDirPath returns the mountID-specific staging directory under the manager base path.
func (mountManager *Manager) stagingDirPath(mountID string) string {
	return fmt.Sprintf("%s/%s", mountManager.stagingBase, mountID)
}

// encryptedMountPath returns the encrypted mount path (`.../mount`) for a mountID.
func (mountManager *Manager) encryptedMountPath(mountID string) string {
	return fmt.Sprintf("%s/mount", mountManager.stagingDirPath(mountID))
}

// metadataFilePath returns the persisted metadata file path for a mountID.
func (mountManager *Manager) metadataFilePath(mountID string, fileName string) string {
	return fmt.Sprintf("%s/%s", mountManager.stagingDirPath(mountID), fileName)
}

// removeOrphanStagingDir tears down and removes a staging directory left over from a previous
// crash or aborted stage. All sub-steps are best-effort: errors are logged but never returned
// because partial cleanup is always better than aborting the overall recovery.
func removeOrphanStagingDir(mountID string, stagingDir string) {
	killOrphanBlobfuseProcesses(stagingDir)
	// Attempt a clean unmount of both sub-paths first.
	for _, sub := range []string{"/mount", "/mount-plain"} {
		_ = exec.Command("fusermount", "-u", stagingDir+sub).Run()
		_ = unmountBlobfuse(stagingDir + sub)
	}
	if err := os.RemoveAll(stagingDir); err != nil {
		// RemoveAll can fail if a mount is still busy (e.g. a process holds an open fd).
		// Escalate to lazy unmount (-uz) which defers the unmount until the last fd is closed,
		// then retry the directory removal.
		log.WithField("mountID", mountID).WithError(err).Warn("RemoveAll failed, trying lazy unmount")
		for _, sub := range []string{"/mount", "/mount-plain"} {
			_ = exec.Command("fusermount", "-uz", stagingDir+sub).Run()
		}
		_ = os.RemoveAll(stagingDir)
	}
}
