// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package stages

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"

	"github.com/azure/azure-cleanroom/src/k8s-node/cleanroom-boot/internal/runner"
	log "github.com/sirupsen/logrus"
)

// StartKubeletProxyStage runs the kubelet-proxy's configure.sh to generate
// TLS certs, install the API policy, and start the service. The binary,
// systemd unit, and kubelet port drop-in must already be installed by
// install.sh at image-prep time.
type StartKubeletProxyStage struct{}

func (s *StartKubeletProxyStage) Name() string { return "startKubeletProxy" }

func (s *StartKubeletProxyStage) Run(ctx *Context) error {
	kubeletProxyCfg := ctx.Config.KubeletProxyConfig

	// Write api-policy.json from config envelope.
	policyFile := filepath.Join(ctx.BootDir, "api-policy.json")
	if kubeletProxyCfg.ApiPolicy != nil {
		policyData, err := json.Marshal(kubeletProxyCfg.ApiPolicy)
		if err != nil {
			return fmt.Errorf("marshaling API policy: %w", err)
		}
		if err := os.WriteFile(policyFile, policyData, 0o644); err != nil {
			return fmt.Errorf("writing API policy: %w", err)
		}
	}

	// Build configure.sh arguments.
	configureScript := filepath.Join(ctx.KubeletProxyDir, "configure.sh")
	args := []string{"bash", configureScript}

	if kubeletProxyCfg.ApiPolicy != nil {
		args = append(args, "--api-policy-file", policyFile)
	}

	// Pass the api-server-proxy CA so the kubelet-proxy signs its client cert
	// with it. kubelet's --client-ca-file is the api-server-proxy cert, so the
	// signed client cert is accepted upstream.
	args = append(args,
		"--ca-cert-file", apiServerProxyCertPath,
		"--ca-key-file", apiServerProxyKeyPath,
	)

	log.Info("Running kubelet-proxy configure.sh ...")
	if err := runner.RunCmd(args, true, nil); err != nil {
		return err
	}

	log.Info("kubelet-proxy started successfully.")
	return nil
}
