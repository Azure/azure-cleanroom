// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package blobfuse

import (
	"bufio"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"

	log "github.com/sirupsen/logrus"
)

// unmountBlobfuse requests a clean unmount for the provided blobfuse mount path.
func unmountBlobfuse(path string) error {
	cmd := exec.Command("blobfuse2", "unmount", path)
	if out, err := cmd.CombinedOutput(); err != nil {
		return fmt.Errorf("blobfuse2 unmount %s failed: %s: %w", path, string(out), err)
	}
	return nil
}

// findBlobfusePid scans /proc for a blobfuse2 process whose cmdline references
// the given mount path. Uses exact word boundary matching to avoid false positives
// (e.g., "/mount" matching "/mount-plain").
func findBlobfusePid(mountPath string) int {
	entries, err := os.ReadDir("/proc")
	if err != nil {
		return 0
	}
	for _, entry := range entries {
		pid, err := strconv.Atoi(entry.Name())
		if err != nil {
			continue
		}
		cmdline, err := os.ReadFile(fmt.Sprintf("/proc/%d/cmdline", pid))
		if err != nil {
			continue
		}
		args := strings.Split(string(cmdline), "\x00")
		if len(args) < 3 {
			continue
		}
		if filepath.Base(args[0]) != "blobfuse2" || args[1] != "mount" {
			continue
		}
		if args[2] == mountPath {
			return pid
		}
	}
	return 0
}

// parseMountInfo parses /proc/self/mountinfo and returns a map of device (major:minor)
// to mount point paths. This is used to correlate bind mounts (pod targets) with
// their source staging paths by matching device numbers.
func parseMountInfo() (deviceToStaging map[string]string, deviceToTargets map[string][]string) {
	deviceToStaging = make(map[string]string)
	deviceToTargets = make(map[string][]string)

	f, err := os.Open("/proc/self/mountinfo")
	if err != nil {
		log.WithError(err).Warn("Failed to read /proc/self/mountinfo")
		return
	}
	defer func() { _ = f.Close() }()

	scanner := bufio.NewScanner(f)
	for scanner.Scan() {
		fields := strings.Fields(scanner.Text())
		if len(fields) < 5 {
			continue
		}
		device := fields[2]
		mountPt := fields[4]

		if strings.Contains(mountPt, "/var/lib/csi/cleanroom/staging/") &&
			(strings.HasSuffix(mountPt, "/mount") || strings.HasSuffix(mountPt, "/mount-plain")) {
			deviceToStaging[device] = mountPt
		} else if strings.Contains(mountPt, "/var/lib/kubelet/pods/") &&
			strings.Contains(mountPt, "kubernetes.io~csi") {
			deviceToTargets[device] = append(deviceToTargets[device], mountPt)
		}
	}
	return
}

// isMountpoint uses mountpoint(1) with /proc/mounts fallback for broken FUSE mounts.
func isMountpoint(path string) bool {
	cmd := exec.Command("mountpoint", "-q", path)
	if cmd.Run() == nil {
		return true
	}
	mounts := parseProcMounts()
	_, found := mounts[path]
	return found
}

// isMountHealthy checks if a FUSE mount is alive by verifying it is still
// registered in /proc/self/mounts.
func isMountHealthy(path string) bool {
	log.WithField("path", path).Debug("Checking if path is a valid mount")
	return isMountpoint(path)
}

// killOrphanBlobfuseProcesses finds and kills any blobfuse2 processes whose
// mount path contains the given staging directory.
func killOrphanBlobfuseProcesses(stagingDir string) {
	entries, err := os.ReadDir("/proc")
	if err != nil {
		log.WithError(err).Warn("Failed to read /proc for orphan process cleanup")
		return
	}

	for _, entry := range entries {
		pid, err := strconv.Atoi(entry.Name())
		if err != nil {
			continue
		}

		cmdline, err := os.ReadFile(fmt.Sprintf("/proc/%d/cmdline", pid))
		if err != nil {
			continue
		}

		cmdStr := strings.ReplaceAll(string(cmdline), "\x00", " ")
		if strings.Contains(cmdStr, "blobfuse2") && strings.Contains(cmdStr, stagingDir) {
			log.WithFields(log.Fields{
				"pid":        pid,
				"stagingDir": stagingDir,
				"cmdline":    strings.TrimSpace(cmdStr),
			}).Warn("Killing orphan blobfuse2 process")
			_ = syscall.Kill(pid, syscall.SIGKILL)
		}
	}
}

// mountInfo describes parsed mount properties used by recovery checks.
type mountInfo struct {
	readOnly bool
}

// parseProcMounts returns a mount-point map with read-only state for each path.
func parseProcMounts() map[string]mountInfo {
	result := make(map[string]mountInfo)

	f, err := os.Open("/proc/mounts")
	if err != nil {
		log.WithError(err).Warn("Failed to read /proc/mounts")
		return result
	}
	defer func() { _ = f.Close() }()

	scanner := bufio.NewScanner(f)
	for scanner.Scan() {
		fields := strings.Fields(scanner.Text())
		if len(fields) >= 4 {
			opts := fields[3]
			ro := strings.HasPrefix(opts, "ro,") || opts == "ro"
			result[fields[1]] = mountInfo{readOnly: ro}
		}
	}
	log.WithField("count", len(result)).Debug("Parsed /proc/mounts")

	return result
}
