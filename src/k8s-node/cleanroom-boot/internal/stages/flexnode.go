// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package stages

import (
	"encoding/base64"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"github.com/azure/azure-cleanroom/src/k8s-node/cleanroom-boot/internal/runner"
	log "github.com/sirupsen/logrus"
)

const (
	agentStatusFile = "/run/aks-flex-node/status.json"
	maxWait         = 300
	waitInterval    = 10
	containerdCfg   = "/etc/containerd/config.toml"
)

// StartFlexNodeAgentStage copies the flex-node config, starts the
// agent, and waits for kubelet to become ready.
type StartFlexNodeAgentStage struct{}

func (s *StartFlexNodeAgentStage) Name() string { return "flexNodeAgent" }

func (s *StartFlexNodeAgentStage) Run(ctx *Context) error {
	src := filepath.Join(ctx.BootDir, "flex-node-config.json")
	dstDir := "/etc/aks-flex-node"
	if err := os.MkdirAll(dstDir, 0o755); err != nil {
		return fmt.Errorf("creating flex-node config dir: %w", err)
	}

	data, err := os.ReadFile(src)
	if err != nil {
		return fmt.Errorf("reading flex-node config: %w", err)
	}

	// Inject proxy serverURL + caCertData so kubelet connects through the
	// api-server-proxy from its very first request. The proxy cert was written
	// to a well-known path by StartApiServerProxy's configure.sh.
	data, err = injectProxyConfig(data)
	if err != nil {
		return fmt.Errorf("injecting proxy config: %w", err)
	}
	log.Info("Injected proxy serverURL into flex-node config.")

	dst := filepath.Join(dstDir, "config.json")
	if err := os.WriteFile(dst, data, 0o644); err != nil {
		return fmt.Errorf("writing flex-node config: %w", err)
	}
	log.Infof("Config written to %s.", dst)

	log.Info("Enabling and starting aks-flex-node-agent service ...")
	if err := runner.RunCmd(
		[]string{"systemctl", "start", "aks-flex-node-agent"},
		true, nil,
	); err != nil {
		return err
	}

	log.Info("Waiting for kubelet to become ready ...")
	elapsed := 0
	for elapsed < maxWait {
		out, _ := runner.RunCmdOutput(
			[]string{"systemctl", "is-active", "aks-flex-node-agent"},
		)
		if out == "failed" || out == "inactive" {
			_ = runner.RunCmd([]string{
				"journalctl", "-u", "aks-flex-node-agent",
				"--since", "5 minutes ago", "--no-pager",
			}, false, nil)
			return fmt.Errorf(
				"aks-flex-node-agent service stopped (status: %s)", out,
			)
		}

		if _, statErr := os.Stat(agentStatusFile); statErr == nil {
			statusData, readErr := os.ReadFile(agentStatusFile)
			if readErr == nil {
				var statusMap map[string]any
				if json.Unmarshal(statusData, &statusMap) == nil {
					kubeletRunning, _ := statusMap["kubeletRunning"].(bool)
					kubeletReady, _ := statusMap["kubeletReady"].(string)
					log.Infof(
						"Status: kubeletRunning=%v, kubeletReady=%s",
						kubeletRunning, kubeletReady,
					)
					if kubeletRunning && kubeletReady == "Ready" {
						log.Info("Kubelet is ready.")
						migrateContainerdConfig()
						return nil
					}
				}
			}
		} else {
			log.Infof("Waiting for %s to appear ...", agentStatusFile)
		}

		time.Sleep(waitInterval * time.Second)
		elapsed += waitInterval
	}

	_ = runner.RunCmd([]string{
		"journalctl", "-u", "aks-flex-node-agent",
		"--since", "5 minutes ago", "--no-pager",
	}, false, nil)
	return fmt.Errorf(
		"kubelet did not become ready within %d seconds", maxWait,
	)
}

func migrateContainerdConfig() {
	if _, err := os.Stat(containerdCfg); err != nil {
		log.Info("No containerd config found, skipping migration.")
		return
	}

	log.Info("Migrating containerd config to match installed version...")
	_ = runner.RunCmd([]string{
		"bash", "-c",
		"containerd config migrate /etc/containerd/config.toml" +
			" > /tmp/containerd-config-migrated.toml" +
			" && mv /etc/containerd/config.toml" +
			" /etc/containerd/config.toml.bak" +
			" && mv /tmp/containerd-config-migrated.toml" +
			" /etc/containerd/config.toml",
	}, true, nil)
	_ = runner.RunCmd(
		[]string{"systemctl", "restart", "containerd"}, true, nil,
	)
	log.Info("containerd config migrated and restarted.")
}

// injectProxyConfig modifies the flex-node config JSON to set
// node.kubelet.serverURL and node.kubelet.caCertData so the flex-node
// agent writes a kubeconfig pointing kubelet at the api-server-proxy. The
// CA cert is read from the proxy's well-known cert path.
func injectProxyConfig(data []byte) ([]byte, error) {
	certPEM, err := os.ReadFile(apiServerProxyCertPath)
	if err != nil {
		return nil, fmt.Errorf("reading proxy cert %s: %w", apiServerProxyCertPath, err)
	}
	proxyCertBase64 := base64.StdEncoding.EncodeToString(certPEM)

	var flexConfig map[string]any
	if err := json.Unmarshal(data, &flexConfig); err != nil {
		return nil, fmt.Errorf("unmarshaling flex config: %w", err)
	}

	node, _ := flexConfig["node"].(map[string]any)
	if node == nil {
		node = map[string]any{}
		flexConfig["node"] = node
	}
	kubelet, _ := node["kubelet"].(map[string]any)
	if kubelet == nil {
		kubelet = map[string]any{}
		node["kubelet"] = kubelet
	}

	kubelet["serverURL"] = "https://127.0.0.1:6444"
	kubelet["caCertData"] = proxyCertBase64

	return json.MarshalIndent(flexConfig, "", "  ")
}
