// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package config

import (
	"encoding/json"
	"testing"
)

func TestCleanroomConfigParsing(t *testing.T) {
	raw := `{
		"version": "1.0",
		"flexNodeConfig": {"key": "value"},
		"cniConfig": {"cniVersion": "0.3.1"},
		"netplan": {
			"network": {
				"version": 2,
				"ethernets": {
					"eth0": {"dhcp4": true}
				}
			}
		},
		"apiServerProxyConfig": {
			"proxyListenAddr": "127.0.0.1:6444",
			"insecure": false,
			"signingCert": "-----BEGIN CERT-----"
		},
		"kubeletProxyConfig": {
			"apiPolicy": {"rules": []}
		},
		"gpuConfig": {
			"driverVersion": "580"
		}
	}`

	var cfg CleanroomConfig
	if err := json.Unmarshal([]byte(raw), &cfg); err != nil {
		t.Fatalf("unmarshal failed: %v", err)
	}

	if cfg.Version != "1.0" {
		t.Errorf("expected version 1.0, got %s", cfg.Version)
	}
	if cfg.Netplan.Network.Version != 2 {
		t.Errorf(
			"expected netplan version 2, got %d",
			cfg.Netplan.Network.Version,
		)
	}
	if cfg.ApiServerProxyConfig.ProxyListenAddr != "127.0.0.1:6444" {
		t.Errorf(
			"expected proxy addr 127.0.0.1:6444, got %s",
			cfg.ApiServerProxyConfig.ProxyListenAddr,
		)
	}
	if cfg.GpuConfig == nil {
		t.Error("expected gpuConfig to be non-nil")
	}
}

func TestCleanroomConfigOptionalGpu(t *testing.T) {
	raw := `{
		"version": "2.0",
		"flexNodeConfig": {},
		"cniConfig": {},
		"netplan": {"network": {"version": 2, "ethernets": {}}},
		"apiServerProxyConfig": {
			"proxyListenAddr": "",
			"insecure": true,
			"signingCert": ""
		},
		"kubeletProxyConfig": {"apiPolicy": {}}
	}`

	var cfg CleanroomConfig
	if err := json.Unmarshal([]byte(raw), &cfg); err != nil {
		t.Fatalf("unmarshal failed: %v", err)
	}

	if cfg.GpuConfig != nil {
		t.Error("expected gpuConfig to be nil when omitted")
	}
	if !cfg.ApiServerProxyConfig.Insecure {
		t.Error("expected insecure to be true")
	}
}
