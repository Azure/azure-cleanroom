// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package stages

import (
	"fmt"
	"os"
	"path/filepath"

	"github.com/azure/azure-cleanroom/src/k8s-node/cleanroom-boot/internal/clusterconfig"
	"github.com/azure/azure-cleanroom/src/k8s-node/cleanroom-boot/internal/runner"
	log "github.com/sirupsen/logrus"
)

// StartApiServerProxyStage resolves the target cluster's details (via ARM) and
// runs the api-server-proxy's configure.sh to generate TLS certs, write the
// upstream kubeconfig, and start the service. The binary and systemd unit must
// already be installed by install.sh at image-prep time.
type StartApiServerProxyStage struct{}

const (
	// apiServerProxyCertPath is where this stage's configure.sh writes the
	// proxy's self-signed serving cert. These paths are the seam to later
	// stages (flex-node config injection, kubelet-proxy) — no shared in-memory
	// state is threaded through Context.
	apiServerProxyCertPath = "/etc/api-server-proxy/api-server-proxy.crt"
	// apiServerProxyKeyPath is the matching private key. The kubelet-proxy
	// signs its client cert with this CA so kubelet (whose --client-ca-file is
	// the api-server-proxy cert) accepts the proxy's client connections.
	apiServerProxyKeyPath = "/etc/api-server-proxy/api-server-proxy.key"
)

func (s *StartApiServerProxyStage) Name() string { return "startApiServerProxy" }

func (s *StartApiServerProxyStage) Run(ctx *Context) error {
	proxyCfg := ctx.Config.ApiServerProxyConfig

	// Resolve the API server URL + CA and MSI/tenant from ARM. Consumed only
	// here, so it stays a local value rather than shared pipeline state.
	details, err := clusterconfig.Resolve(ctx.Config)
	if err != nil {
		return fmt.Errorf("resolving cluster config: %w", err)
	}

	// Write API server CA to boot dir for configure.sh.
	caFile := filepath.Join(ctx.BootDir, "api-server-ca.pem")
	if err := os.WriteFile(caFile, details.ApiServerCACert, 0o644); err != nil {
		return fmt.Errorf("writing API server CA: %w", err)
	}

	// Write signing cert from config envelope.
	signingCertFile := filepath.Join(ctx.BootDir, "signing-cert.pem")
	if proxyCfg.SigningCert != "" {
		if err := os.WriteFile(
			signingCertFile, []byte(proxyCfg.SigningCert), 0o644,
		); err != nil {
			return fmt.Errorf("writing signing cert: %w", err)
		}
	}

	// Build configure.sh arguments.
	configureScript := filepath.Join(ctx.ApiServerProxyDir, "configure.sh")
	args := []string{
		"bash", configureScript,
		"--upstream-api-server", details.ApiServerURL,
		"--upstream-ca-file", caFile,
	}

	if proxyCfg.SigningCert != "" {
		args = append(args, "--signing-cert-file", signingCertFile)
	}

	if details.MsiClientID != "" {
		args = append(args, "--msi-client-id", details.MsiClientID)
	}
	if details.TenantID != "" {
		args = append(args, "--msi-tenant-id", details.TenantID)
	}

	if proxyCfg.Insecure {
		args = append(args, "--insecure")
	}

	log.Info("Running api-server-proxy configure.sh ...")
	if err := runner.RunCmd(args, true, nil); err != nil {
		return err
	}

	log.Info("api-server-proxy started successfully.")
	return nil
}
