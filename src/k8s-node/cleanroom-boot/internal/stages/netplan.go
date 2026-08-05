// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package stages

import (
	"fmt"
	"os"
	"path/filepath"

	"github.com/azure/azure-cleanroom/src/k8s-node/cleanroom-boot/internal/runner"
	log "github.com/sirupsen/logrus"
)

// ApplyNetplanStage copies netplan YAML into /etc/netplan/ and applies.
type ApplyNetplanStage struct{}

func (s *ApplyNetplanStage) Name() string { return "netplan" }

func (s *ApplyNetplanStage) Run(ctx *Context) error {
	src := filepath.Join(ctx.BootDir, "netplan.yaml")
	dst := "/etc/netplan/99-static-eth0.yaml"

	log.Info("Applying netplan config ...")

	data, err := os.ReadFile(src)
	if err != nil {
		return fmt.Errorf("reading netplan source: %w", err)
	}
	if err := os.WriteFile(dst, data, 0o600); err != nil {
		return fmt.Errorf("writing netplan dest: %w", err)
	}

	// Backup cloud-init netplan if present.
	cloudInitNetplan := "/etc/netplan/50-cloud-init.yaml"
	if _, err := os.Stat(cloudInitNetplan); err == nil {
		if err := os.Rename(
			cloudInitNetplan, cloudInitNetplan+".bak",
		); err != nil {
			return fmt.Errorf("backing up cloud-init netplan: %w", err)
		}
	}

	if err := runner.RunCmd(
		[]string{"netplan", "apply"}, true, nil,
	); err != nil {
		return err
	}
	log.Info("Netplan config applied.")
	return nil
}
