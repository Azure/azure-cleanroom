// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package blobfuse

import (
	"fmt"
	"os"
	"os/exec"
	"syscall"
	"time"

	"github.com/azure/azure-cleanroom/poc/csi-driver/pkg/contracts"
	log "github.com/sirupsen/logrus"
)

// nextWriterKey returns the mountID to use for a new writable mount.
// The first writer for a mountID uses mountID directly. Subsequent concurrent
// writers use mountID-1, mountID-2, etc. — each gets its own isolated staging
// directory and blobfuse2 cache. Must be called with the manager lock held.
func (mountManager *Manager) nextWriterKey(mountID string) string {
	if _, exists := mountManager.mounts[mountID]; !exists {
		return mountID
	}
	for i := 1; ; i++ {
		key := fmt.Sprintf("%s-%d", mountID, i)
		if _, exists := mountManager.mounts[key]; !exists {
			return key
		}
	}
}

// Stage is safe for concurrent calls; different mounts are staged in parallel.
// It computes the mountID from config, ensures a healthy blobfuse2 mount exists
// for it, and returns the mountID to use for subsequent Publish/Unstage calls.
func (mountManager *Manager) Stage(config *MountConfig, targetPath string) (string, error) {
	mountID := config.MountID()

	mountManager.mu.Lock()
	// Opportunistically clear stale zero-target entries before processing this request.
	mountManager.cleanupOrphanMountsLocked()

	// Fast path: if this targetPath is already tracked, either wait, reuse, or refresh.
	if existingKey, alreadyTracked := mountManager.targetIndex[targetPath]; alreadyTracked {
		if entry, ok := mountManager.mounts[existingKey]; ok {
			// Another goroutine is currently staging this key. Wait and then retry.
			if entry.staging != nil {
				staging := entry.staging
				mountManager.mu.Unlock()
				//TODO(sakshamgarg): Add a timeout here or look at when channel is closed to add a defer there so that in case of a panic we don't block forever.
				<-staging.done
				if staging.err != nil {
					return "", staging.err
				}
				return mountManager.Stage(config, targetPath)
			}
			if isMountHealthy(entry.stagingPath) {
				// Idempotent re-publish: this target already has a healthy staged mount.
				mountManager.mu.Unlock()
				return existingKey, nil
			}
			// Stale entry for this target; clean up and re-stage below.
			log.WithField("mountID", mountID).Warn("Detected stale mount, will re-stage")
			mountManager.cleanupMountLocked(existingKey, entry)
		}
	} else if entry, ok := mountManager.mounts[mountID]; ok && config.ReadOnly {
		// Read-only path: share an existing healthy reader mount across targets.
		if entry.staging != nil {
			staging := entry.staging
			mountManager.mu.Unlock()
			//TODO(sakshamgarg): Add a timeout here or look at when channel is closed to add a defer there so that in case of a panic we don't block forever.
			<-staging.done
			if staging.err != nil {
				return "", staging.err
			}
			return mountManager.Stage(config, targetPath)
		}
		if isMountHealthy(entry.stagingPath) {
			// Read-only mount can be reused and shared with multiple targets.
			entry.refCount++
			entry.targets = append(entry.targets, targetPath)
			mountManager.targetIndex[targetPath] = mountID
			log.WithFields(log.Fields{
				"mountID":  mountID,
				"refCount": entry.refCount,
			}).Info("Reusing existing read-only mount")
			mountManager.mu.Unlock()
			return mountID, nil
		}
		// Stale reader entry; clean up and re-stage below.
		log.WithField("mountID", mountID).Warn("Detected stale mount, will re-stage")
		mountManager.cleanupMountLocked(mountID, entry)
	}

	// Writers resolve to the next available slot (mountID, mountID-1, mountID-2,
	// ...) so each concurrent writer is fully isolated. Readers keep mountID as-is
	// (shared across targets).
	if !config.ReadOnly {
		mountID = mountManager.nextWriterKey(mountID)
	}

	// Register in-progress state so concurrent calls for this key can wait.
	state := &stagingState{done: make(chan struct{})}
	mountManager.mounts[mountID] = &mountEntry{
		contractID: config.ContractID,
		readOnly:   config.ReadOnly,
		staging:    state,
	}
	mountManager.mu.Unlock()

	// Perform the expensive stage work outside the lock.
	path, pid, err := mountManager.doStage(mountID, config)

	mountManager.mu.Lock()
	entry := mountManager.mounts[mountID]
	entry.staging = nil
	// Stage failed: remove temporary entry, wake waiters, and propagate error.
	if err != nil {
		delete(mountManager.mounts, mountID)
		state.err = err
		close(state.done)
		mountManager.mu.Unlock()
		return "", err
	}

	// Stage succeeded: finalize tracked state and reverse indexes.
	entry.stagingPath = path
	entry.contractID = config.ContractID
	entry.refCount = 1
	entry.pid = pid
	entry.targets = []string{targetPath}
	mountManager.targetIndex[targetPath] = mountID
	close(state.done)

	// Persist metadata required for recovery and identity replay after restarts.
	mountManager.persistRecoveryMetadata(mountID, entry, config)

	log.WithFields(log.Fields{
		"mountID":     mountID,
		"stagingPath": path,
		"targetPath":  targetPath,
	}).Info("Staged new blobfuse mount")
	mountManager.dumpStateLocked("post-stage")
	mountManager.mu.Unlock()
	return mountID, nil
}

// cleanupOrphanMountsLocked removes mounts with no active targets from manager state.
// The manager mutex must be held by the caller.
func (mountManager *Manager) cleanupOrphanMountsLocked() {
	for mountID, entry := range mountManager.mounts {
		if entry.staging != nil || len(entry.targets) > 0 {
			continue
		}
		log.WithFields(log.Fields{
			"mountID": mountID,
			"pid":     entry.pid,
		}).Info("Lazy cleanup: orphan mount with no active targets")
		mountManager.cleanupMountLocked(mountID, entry)
	}
}

// cleanupMountLocked tears down a tracked mount via the proxy and removes persisted state.
// If the proxy unmount fails (e.g. proxy not running during startup), the function falls back
// to direct blobfuse2/fusermount commands so cleanup always completes.
// The manager mutex must be held by the caller.
func (mountManager *Manager) cleanupMountLocked(mountID string, entry *mountEntry) {
	if entry.stagingPath != "" {
		if err := callProxy(contracts.ProxyMountRequest{
			Op:        "unmount",
			MountPath: entry.stagingPath,
		}); err != nil {
			// Fallback: proxy unavailable or reported an error — clean up directly.
			log.WithError(err).WithField("mountID", mountID).Warn(
				"Proxy unmount failed, falling back to direct cleanup")
			if entry.pid > 0 {
				_ = syscall.Kill(entry.pid, syscall.SIGKILL)
			} else {
				killOrphanBlobfuseProcesses(mountManager.stagingDirPath(mountID))
			}
			_ = unmountBlobfuse(entry.stagingPath)
			_ = exec.Command("fusermount", "-uz", entry.stagingPath).Run()
			// For CSE mounts, also unmount the plain stage-1 companion (best-effort).
			plainPath := entry.stagingPath + "-plain"
			_ = unmountBlobfuse(plainPath)
			_ = exec.Command("fusermount", "-uz", plainPath).Run()
		}
	}
	// Clean up cache dir (best-effort).
	_ = os.RemoveAll(fmt.Sprintf("/tmp/blobfuse_cache_%s", mountID))
	for _, t := range entry.targets {
		delete(mountManager.targetIndex, t)
	}
	_ = os.RemoveAll(mountManager.stagingDirPath(mountID))
	delete(mountManager.mounts, mountID)
}

// doStage creates the staging directory, builds a ProxyMountRequest from config,
// and dispatches it to the blobfuse-proxy host service via the Unix socket.
// The proxy is responsible for all identity registration, DEK unwrap, and
// blobfuse2 process lifecycle — including the CSE two-stage mount sequence.
// Returns the encrypted staging path and the blobfuse2 process PID.
func (mountManager *Manager) doStage(mountID string, config *MountConfig) (string, int, error) {
	// Apply manager-level default subject so the proxy can use it for OIDC routing.
	if config.Subject == "" && mountManager.defaultSubject != "" {
		// Shallow copy and change the subject so we don't mutate the caller's config.
		configCopy := *config
		configCopy.Subject = mountManager.defaultSubject
		config = &configCopy
	}

	// If the staging directory already exists on disk, it is a leftover from a previous crash
	// or aborted stage where the manager's in-memory state was lost but the filesystem was not
	// cleaned up. Tear it down completely before creating a fresh one.
	stagingDir := mountManager.stagingDirPath(mountID)
	stagingPath := mountManager.encryptedMountPath(mountID)
	if _, err := os.Stat(stagingDir); err == nil {
		log.WithField("mountID", mountID).Warn("Found orphan staging dir, cleaning up")
		removeOrphanStagingDir(mountID, stagingDir)
	}

	if err := os.MkdirAll(stagingPath, stagingDirPerms); err != nil {
		return "", 0, fmt.Errorf("failed to create staging dir: %w", err)
	}

	// Storage env vars are always pre-set; auth and encryption are resolved by
	// the proxy using the semantic fields below.
	env := map[string]string{
		"AZURE_STORAGE_ACCOUNT":           config.StorageAccount,
		"AZURE_STORAGE_ACCOUNT_CONTAINER": config.StorageContainer,
		"AZURE_STORAGE_BLOB_ENDPOINT":     config.StorageBlobEndpoint,
		"BLOBFUSE_CACHE_PATH":             fmt.Sprintf("/tmp/blobfuse_cache_%s", mountID),
		"BLOBFUSE_PLUGIN_PATH":            "/opt/cleanroom/lib/encryptor.so",
	}
	if config.AccessName != "" {
		env["ACCESS_NAME"] = config.AccessName
	}

	req := contracts.ProxyMountRequest{
		Op:             "mount",
		MountPath:      stagingPath,
		StagingDir:     stagingDir,
		ContractID:     config.ContractID,
		EncryptionMode: config.EncryptionMode,
		Encrypted:      config.EncryptionMode == "CSE",
		ReadOnly:       config.ReadOnly,
		SubDirectory:   config.SubDirectory,
		UseAdls:        config.UseAdls,
		CpkEnabled:     config.EncryptionMode == "CPK",
		BlockSizeMB:    config.BlockSizeMB,
		TelemetryPath:  "/tmp/telemetry",
		Env:            env,

		// Semantic identity fields — proxy calls identity sidecar to resolve.
		IdentityClientID: config.ClientID,
		IdentityTenantID: config.TenantID,
		IdentitySubject:  config.Subject,

		// Semantic DEK fields — proxy calls secrets sidecar to resolve (CSE only).
		WrappedDekAkvEndpoint: config.WrappedDekAkvEP,
		WrappedDekSecret:      config.WrappedDekSecret,
		KID:                   config.KID,
		AkvEndpoint:           config.AkvEndpoint,
		MaaEndpoint:           config.MaaEndpoint,
	}

	log.WithFields(log.Fields{
		"mountPath":      stagingPath,
		"encryptionMode": config.EncryptionMode,
		"account":        config.StorageAccount,
		"container":      config.StorageContainer,
		"clientId":       config.ClientID,
	}).Info("Sending mount request to blobfuse-proxy")

	if err := callProxy(req); err != nil {
		_ = os.RemoveAll(stagingDir)
		return "", 0, fmt.Errorf("blobfuse-proxy mount failed: %w", err)
	}

	time.Sleep(500 * time.Millisecond)

	if !isMountpoint(stagingPath) {
		_ = os.RemoveAll(stagingDir)
		return "", 0, fmt.Errorf("blobfuse-proxy reported success but %s is not a mountpoint", stagingPath)
	}

	pid := findBlobfusePid(stagingPath)
	return stagingPath, pid, nil
}

// Unstage decrements the mount reference count and removes the mount at zero references.
func (mountManager *Manager) Unstage(mountID string) error {
	mountManager.mu.Lock()
	defer mountManager.mu.Unlock()

	entry, ok := mountManager.mounts[mountID]
	if !ok {
		log.WithField("mountID", mountID).Warn("Unstage called for unknown mount key")
		return nil
	}

	entry.refCount--
	log.WithFields(log.Fields{
		"mountID": mountID,
		"refCount": entry.refCount,
	}).Info("Decremented mount refcount")

	if entry.refCount > 0 {
		return nil
	}

	mountManager.cleanupMountLocked(mountID, entry)
	log.WithField("mountID", mountID).Info("Fully unstaged and cleaned up blobfuse mount")
	mountManager.dumpStateLocked("post-unstage")
	return nil
}

// Publish bind-mounts the staging path for mountID into the pod target path.
func (mountManager *Manager) Publish(mountID string, targetPath string) error {
	mountManager.mu.Lock()
	defer mountManager.mu.Unlock()

	entry, ok := mountManager.mounts[mountID]
	if !ok {
		return fmt.Errorf("no staged mount for mountID %s", mountID)
	}

	if isMountpoint(targetPath) {
		log.WithField("targetPath", targetPath).Info("Target already mounted, skipping")
		return nil
	}

	if err := os.MkdirAll(targetPath, 0755); err != nil {
		return fmt.Errorf("failed to create target dir: %w", err)
	}

	cmd := exec.Command("mount", "--bind", entry.stagingPath, targetPath)
	if out, err := cmd.CombinedOutput(); err != nil {
		return fmt.Errorf("bind mount failed: %s: %w", string(out), err)
	}

	log.WithFields(log.Fields{
		"mountID":    mountID,
		"targetPath": targetPath,
		"staging":    entry.stagingPath,
	}).Info("Published bind mount")

	return nil
}

// Unpublish unmounts the pod target and returns the owning mount key if known.
func (mountManager *Manager) Unpublish(targetPath string) (string, error) {
	mountManager.mu.Lock()
	defer mountManager.mu.Unlock()

	mountID, ok := mountManager.targetIndex[targetPath]
	if !ok {
		log.WithField("targetPath", targetPath).Warn("Unpublish called for unknown target")
		_ = exec.Command("umount", targetPath).Run()
		return "", nil
	}

	if isMountpoint(targetPath) {
		cmd := exec.Command("umount", targetPath)
		if out, err := cmd.CombinedOutput(); err != nil {
			return mountID, fmt.Errorf("umount failed: %s: %w", string(out), err)
		}
	}

	delete(mountManager.targetIndex, targetPath)

	if entry, ok := mountManager.mounts[mountID]; ok {
		for i, t := range entry.targets {
			if t == targetPath {
				entry.targets = append(entry.targets[:i], entry.targets[i+1:]...)
				break
			}
		}
	}

	log.WithFields(log.Fields{
		"mountID":    mountID,
		"targetPath": targetPath,
	}).Info("Unpublished bind mount")

	return mountID, nil
}

// RecoverMounts rebuilds in-memory manager state from staging metadata and procfs.
func (mountManager *Manager) RecoverMounts() error {
	mountManager.mu.Lock()

	entries, err := os.ReadDir(mountManager.stagingBase)
	if err != nil {
		mountManager.mu.Unlock()
		if os.IsNotExist(err) {
			return nil
		}
		return fmt.Errorf("failed to read staging dir: %w", err)
	}

	activeMounts := parseProcMounts()

	deviceToStaging, deviceToTargets := parseMountInfo()
	stagingToTargets := make(map[string][]string)
	for device, stagingPath := range deviceToStaging {
		if targets, ok := deviceToTargets[device]; ok {
			stagingToTargets[stagingPath] = targets
		}
	}

	for _, dirEntry := range entries {
		if !dirEntry.IsDir() {
			continue
		}
		mountID := dirEntry.Name()
		mountPath := mountManager.encryptedMountPath(mountID)

		if info, active := activeMounts[mountPath]; active {
			pid := findBlobfusePid(mountPath)
			if pid > 0 {
				targets := stagingToTargets[mountPath]
				refCount := len(targets)
				if refCount == 0 {
					refCount = 1
				}

				contractID := mountManager.loadRecoveryMetadata(mountID)

				mountManager.mounts[mountID] = &mountEntry{
					stagingPath: mountPath,
					contractID:  contractID,
					refCount:    refCount,
					readOnly:    info.readOnly,
					pid:         pid,
					targets:     targets,
				}

				for _, t := range targets {
					mountManager.targetIndex[t] = mountID
				}

				log.WithFields(log.Fields{
					"mountID":  mountID,
					"pid":      pid,
					"refCount": refCount,
					"targets":  len(targets),
				}).Info("Recovered existing mount")
			} else {
				// PID 0: mount is active but process not found (zombie/orphaned FUSE). Attempt cleanup.
				log.WithField("mountID", mountID).Warn("Cleaning up stale FUSE mount")
				dirPath := mountManager.stagingDirPath(mountID)
				killOrphanBlobfuseProcesses(dirPath)
				_ = unmountBlobfuse(mountPath)
				_ = os.RemoveAll(dirPath)
			}
		} else {
			log.WithField("mountID", mountID).Info("Cleaning up orphaned staging dir")
			dirPath := mountManager.stagingDirPath(mountID)
			killOrphanBlobfuseProcesses(dirPath)
			_ = exec.Command("fusermount", "-uz", dirPath+"/mount").Run()
			_ = exec.Command("fusermount", "-uz", dirPath+"/mount-plain").Run()
			_ = os.RemoveAll(dirPath)
		}
	}

	mountManager.dumpStateLocked("post-recovery")
	mountManager.mu.Unlock()

	return nil
}

// dumpStateLocked logs manager mount and target indexes for recovery diagnostics.
// The manager mutex must be held by the caller.
func (mountManager *Manager) dumpStateLocked(context string) {
	for mountID, entry := range mountManager.mounts {
		log.WithFields(log.Fields{
			"context":     context,
			"mountID":     mountID,
			"stagingPath": entry.stagingPath,
			"contractId":  entry.contractID,
			"refCount":    entry.refCount,
			"readOnly":    entry.readOnly,
			"pid":         entry.pid,
			"targets":     entry.targets,
			"staging":     entry.staging != nil,
		}).Info("State dump: mount entry")
	}
	for target, mountID := range mountManager.targetIndex {
		log.WithFields(log.Fields{
			"context": context,
			"target":  target,
			"mountID": mountID,
		}).Info("State dump: target index")
	}
}
