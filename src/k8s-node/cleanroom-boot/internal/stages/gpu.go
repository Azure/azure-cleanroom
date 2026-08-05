// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package stages

import (
	"embed"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"os"

	"github.com/azure/azure-cleanroom/src/k8s-node/cleanroom-boot/internal/runner"
	log "github.com/sirupsen/logrus"
)

//go:embed scripts/install-gpu-runtime.sh
var gpuScripts embed.FS

// ConfigureGpuStage activates GPU CC mode and installs the NVIDIA
// container runtime. Must run AFTER StartFlexNodeAgentStage.
type ConfigureGpuStage struct{}

func (s *ConfigureGpuStage) Name() string { return "gpu" }

func (s *ConfigureGpuStage) Run(ctx *Context) error {
	if _, err := os.Stat("/dev/nvidia0"); err != nil {
		log.Info("No GPU detected, skipping GPU configuration.")
		return nil
	}

	// Build environment for the runtime install script.
	env := os.Environ()
	if ctx.Config != nil && ctx.Config.GpuConfig != nil {
		gpuJSON, err := json.Marshal(ctx.Config.GpuConfig)
		if err != nil {
			return fmt.Errorf("marshalling GPU config: %w", err)
		}
		b64 := base64.StdEncoding.EncodeToString(gpuJSON)
		env = append(env, "FLEX_NODE_GPU_CONFIG_B64="+b64)
		log.Infof("GPU config: %s", string(gpuJSON))
	}

	// Extract embedded script to a temp file.
	scriptData, err := gpuScripts.ReadFile(
		"scripts/install-gpu-runtime.sh",
	)
	if err != nil {
		return fmt.Errorf("reading embedded GPU script: %w", err)
	}
	tmpScript := "/tmp/install-gpu-runtime.sh"
	if err := os.WriteFile(tmpScript, scriptData, 0o755); err != nil {
		return fmt.Errorf("writing GPU script: %w", err)
	}
	defer os.Remove(tmpScript)

	// Install NVIDIA container toolkit, device plugin, and GFD.
	log.Info("Installing GPU container runtime ...")
	if err := runner.RunCmd(
		[]string{"bash", tmpScript}, true, env,
	); err != nil {
		return err
	}
	log.Info("GPU configuration completed.")

	// Activate CC mode (requires physical GPU).
	log.Info(
		"GPU detected. Configuring persistence mode and" +
			" CC secure reset ...",
	)
	if err := runner.RunCmd(
		[]string{"nvidia-smi", "-pm", "1"}, true, nil,
	); err != nil {
		return err
	}
	if err := runner.RunCmd(
		[]string{"nvidia-smi", "conf-compute", "-srs", "1"}, true, nil,
	); err != nil {
		return err
	}
	if err := runner.RunCmd(
		[]string{"nvidia-smi"}, true, nil,
	); err != nil {
		return err
	}
	return runner.RunCmd(
		[]string{"nvidia-smi", "conf-compute", "-f"}, true, nil,
	)
}
