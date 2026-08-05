// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package blobfuse

import (
	"encoding/json"
	"fmt"
	"net"
	"sync"

	"github.com/azure/azure-cleanroom/poc/csi-driver/pkg/contracts"
)

// ProxyMountRequest and ProxyResponse are the canonical wire types.
// Both are defined in pkg/contracts/proxy.go and shared by the driver and proxy.

const (
	stagingDirPerms = 0755
	configFilePerms = 0644
	proxySocketPath = "/run/blobfuse-proxy/blobfuse-proxy.sock"
)

// callProxy dials the host blobfuse-proxy Unix socket, sends req, and returns any error.
// req must have Op set to "mount" or "unmount".
func callProxy(req contracts.ProxyMountRequest) error {
	conn, err := net.Dial("unix", proxySocketPath)
	if err != nil {
		return fmt.Errorf("dial blobfuse-proxy: %w", err)
	}
	defer func() { _ = conn.Close() }()

	if err := json.NewEncoder(conn).Encode(req); err != nil {
		return fmt.Errorf("send proxy request: %w", err)
	}

	var resp contracts.ProxyResponse
	if err := json.NewDecoder(conn).Decode(&resp); err != nil {
		return fmt.Errorf("decode proxy response: %w", err)
	}

	if !resp.Success {
		return fmt.Errorf("proxy error: %s", resp.Error)
	}
	return nil
}

// MountConfig is an alias over shared mount contracts.
type MountConfig = contracts.MountSpec

// mountEntry tracks one managed blobfuse mount and its published targets.
type mountEntry struct {
	stagingPath string
	contractID  string
	refCount    int
	readOnly    bool
	pid         int      // blobfuse2 PID (for targeted kill on cleanup).
	targets     []string // Pod target paths bound to this mount.
	staging     *stagingState
}

// stagingState tracks an in-progress mount operation for per-key synchronization.
type stagingState struct {
	done chan struct{} // Closed when staging completes.
	err  error         // Set before closing done.
}

// Manager manages ref-counted blobfuse2 mounts via the blobfuse-proxy host service.
type Manager struct {
	stagingBase    string
	defaultSubject string
	mu             sync.Mutex
	mounts         map[string]*mountEntry // mount key → entry
	targetIndex    map[string]string      // target path → mount key (reverse index for Unpublish)
}

// NewManager returns a manager configured with a staging base and default subject.
func NewManager(stagingBase, defaultSubject string) *Manager {
	return &Manager{
		stagingBase:    stagingBase,
		defaultSubject: defaultSubject,
		mounts:         make(map[string]*mountEntry),
		targetIndex:    make(map[string]string),
	}
}
