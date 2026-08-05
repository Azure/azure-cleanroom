// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Package config defines the cleanroom boot configuration envelope.
package config

// CleanroomConfig is the unified boot configuration envelope.
// Written to /etc/cleanroom-boot/cleanroom-config.json by cloud-init
// at VM creation time.
type CleanroomConfig struct {
	Version              string                `json:"version"`
	FlexNodeConfig       map[string]any        `json:"flexNodeConfig"`
	CniConfig            map[string]any        `json:"cniConfig"`
	Netplan              NetplanConfig         `json:"netplan"`
	ApiServerProxyConfig ApiServerProxyConfig  `json:"apiServerProxyConfig"`
	KubeletProxyConfig   KubeletProxyConfig    `json:"kubeletProxyConfig"`
	GpuConfig            map[string]any        `json:"gpuConfig,omitempty"`
}

// ApiServerProxyConfig holds settings consumed by the api-server-proxy.
type ApiServerProxyConfig struct {
	ProxyListenAddr string `json:"proxyListenAddr"`
	Insecure        bool   `json:"insecure"`
	SigningCert     string `json:"signingCert"`
}

// KubeletProxyConfig holds settings consumed by the kubelet-proxy.
type KubeletProxyConfig struct {
	ApiPolicy map[string]any `json:"apiPolicy"`
}

// NetplanConfig is the netplan configuration written at boot.
type NetplanConfig struct {
	Network NetplanNetwork `json:"network"`
}

// NetplanNetwork is the inner network block of a netplan configuration.
type NetplanNetwork struct {
	Version   int            `json:"version"`
	Ethernets map[string]any `json:"ethernets"`
}
