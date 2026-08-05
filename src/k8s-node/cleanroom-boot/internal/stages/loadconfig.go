// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package stages

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"

	"github.com/azure/azure-cleanroom/src/k8s-node/cleanroom-boot/internal/config"
	log "github.com/sirupsen/logrus"
	"gopkg.in/yaml.v3"
)

// LoadConfigStage reads the config envelope, validates required
// fields, and extracts individual config files into the boot
// directory.
type LoadConfigStage struct{}

func (s *LoadConfigStage) Name() string { return "loadConfig" }

func (s *LoadConfigStage) Run(ctx *Context) error {
	// ── Validate ────────────────────────────────────────────
	log.Infof(
		"Validating config envelope at %s ...", ctx.ConfigPath,
	)

	data, err := os.ReadFile(ctx.ConfigPath)
	if err != nil {
		return fmt.Errorf("config file not found: %w", err)
	}

	var cfg config.CleanroomConfig
	if err := json.Unmarshal(data, &cfg); err != nil {
		return fmt.Errorf(
			"config file is not valid JSON: %w", err,
		)
	}

	if cfg.Version == "" {
		return fmt.Errorf(
			"config validation failed: version is required",
		)
	}
	if cfg.FlexNodeConfig == nil {
		return fmt.Errorf(
			"config validation failed: " +
				"flexNodeConfig is required",
		)
	}
	if cfg.CniConfig == nil {
		return fmt.Errorf(
			"config validation failed: " +
				"cniConfig is required",
		)
	}

	ctx.BootStatus.ConfigVersion = cfg.Version
	ctx.Config = &cfg
	log.Infof("Config version: %s", cfg.Version)
	log.Infof(
		"Image version: %s", ctx.BootStatus.ImageVersion,
	)

	// ── Extract ─────────────────────────────────────────────
	bootDir := ctx.BootDir
	log.Infof(
		"Extracting config sections to %s ...", bootDir,
	)
	if err := os.MkdirAll(bootDir, 0o755); err != nil {
		return fmt.Errorf("creating boot dir: %w", err)
	}

	if err := writeJSON(
		filepath.Join(bootDir, "flex-node-config.json"),
		cfg.FlexNodeConfig,
	); err != nil {
		return err
	}

	if err := writeJSON(
		filepath.Join(bootDir, "cni-bridge.conf"),
		cfg.CniConfig,
	); err != nil {
		return err
	}

	// Netplan as YAML.
	netplanData := map[string]any{
		"network": map[string]any{
			"version":   cfg.Netplan.Network.Version,
			"ethernets": cfg.Netplan.Network.Ethernets,
		},
	}
	netplanBytes, err := yaml.Marshal(netplanData)
	if err != nil {
		return fmt.Errorf("marshalling netplan: %w", err)
	}
	if err := os.WriteFile(
		filepath.Join(bootDir, "netplan.yaml"),
		netplanBytes, 0o644,
	); err != nil {
		return fmt.Errorf("writing netplan.yaml: %w", err)
	}

	// Boot config (proxy settings without signing cert).
	bootConfig := map[string]any{
		"proxyListenAddr": cfg.ApiServerProxyConfig.ProxyListenAddr,
		"insecure":        cfg.ApiServerProxyConfig.Insecure,
	}
	if err := writeJSON(
		filepath.Join(bootDir, "boot-config.json"),
		bootConfig,
	); err != nil {
		return err
	}

	// Signing cert.
	if err := os.WriteFile(
		filepath.Join(bootDir, "signing-cert.pem"),
		[]byte(cfg.ApiServerProxyConfig.SigningCert),
		0o644,
	); err != nil {
		return fmt.Errorf(
			"writing signing-cert.pem: %w", err,
		)
	}

	// API policy.
	if err := writeJSON(
		filepath.Join(bootDir, "api-policy.json"),
		cfg.KubeletProxyConfig.ApiPolicy,
	); err != nil {
		return err
	}

	log.Info("Config loaded and extracted.")
	return nil
}

func writeJSON(path string, data any) error {
	jsonBytes, err := json.MarshalIndent(data, "", "  ")
	if err != nil {
		return fmt.Errorf("marshalling %s: %w", path, err)
	}
	jsonBytes = append(jsonBytes, '\n')
	if err := os.WriteFile(path, jsonBytes, 0o644); err != nil {
		return fmt.Errorf("writing %s: %w", path, err)
	}
	return nil
}
