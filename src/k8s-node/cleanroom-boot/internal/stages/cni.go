// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package stages

import (
	"fmt"
	"os"
	"path/filepath"

	log "github.com/sirupsen/logrus"
)

// ApplyCniStage copies the CNI bridge config into /etc/cni/net.d/.
type ApplyCniStage struct{}

func (s *ApplyCniStage) Name() string { return "cni" }

func (s *ApplyCniStage) Run(ctx *Context) error {
	src := filepath.Join(ctx.BootDir, "cni-bridge.conf")
	dstDir := "/etc/cni/net.d"
	dst := filepath.Join(dstDir, "10-bridge.conf")

	log.Info("Applying CNI bridge config ...")
	if err := os.MkdirAll(dstDir, 0o755); err != nil {
		return fmt.Errorf("creating CNI dir: %w", err)
	}

	data, err := os.ReadFile(src)
	if err != nil {
		return fmt.Errorf("reading CNI source: %w", err)
	}
	if err := os.WriteFile(dst, data, 0o644); err != nil {
		return fmt.Errorf("writing CNI dest: %w", err)
	}

	log.Info("CNI bridge config applied.")
	return nil
}
