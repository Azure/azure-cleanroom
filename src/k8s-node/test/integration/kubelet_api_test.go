// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

//go:build integration && kubelet_proxy

package integration

import (
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestKubeletAPIPolicy(t *testing.T) {
	if env.KubeletProxyInstallMode() == "insecure" {
		t.Fatal("TestKubeletAPIPolicy requires default (non-insecure) install mode")
	}

	type apiTest struct {
		name string
		path string
	}

	t.Run("Allowed", func(t *testing.T) {
		allowed := []apiTest{
			{"pods", "/pods"},
			{"healthz", "/healthz"},
			{"metrics", "/metrics"},
			{"metrics_cadvisor", "/metrics/cadvisor"},
			{"metrics_resource", "/metrics/resource"},
			{"metrics_probes", "/metrics/probes"},
			{"stats", "/stats"},
			{"stats_summary", "/stats/summary"},
			{"checkpoint", "/checkpoint"},
		}

		for _, tc := range allowed {
			t.Run(tc.name, func(t *testing.T) {
				t.Logf("Verifying kubelet API %s is allowed through proxy", tc.path)
				code, body, err := env.CurlKubeletAPI(tc.path)
				if err != nil {
					t.Fatalf("curl failed: %v", err)
				}
				t.Logf("Response: HTTP %d", code)
				if code == 403 && strings.Contains(body, "rejected by policy") {
					t.Fatalf("expected allowed, got policy rejection (HTTP %d): %s",
						code, body)
				}
				if code >= 500 {
					t.Fatalf("expected allowed, got server error (HTTP %d): %s",
						code, body)
				}
				t.Logf("✓ API %s allowed (HTTP %d)", tc.path, code)
			})
		}
	})

	t.Run("Forbidden", func(t *testing.T) {
		forbidden := []apiTest{
			{"exec", "/exec"},
			{"run", "/run"},
			{"attach", "/attach"},
			{"portForward", "/portForward"},
			{"runningpods", "/runningpods"},
			{"debug_pprof", "/debug/pprof"},
			{"debug_flags_v", "/debug/flags/v"},
			{"containerLogs", "/containerLogs"},
			{"logs", "/logs"},
		}

		for _, tc := range forbidden {
			t.Run(tc.name, func(t *testing.T) {
				t.Logf("Verifying kubelet API %s is blocked by policy", tc.path)
				code, body, err := env.CurlKubeletAPI(tc.path)
				if err != nil {
					t.Fatalf("curl failed: %v", err)
				}
				t.Logf("Response: HTTP %d, body: %s", code, truncate(body, 100))
				if code != 403 || !strings.Contains(body, "rejected by policy") {
					t.Fatalf("expected 403 with 'rejected by policy', got HTTP %d: %s",
						code, body)
				}
				t.Logf("✓ API %s blocked (HTTP 403, rejected by policy)", tc.path)
			})
		}
	})
}

// truncate shortens a string for log output.
func truncate(s string, max int) string {
	if len(s) <= max {
		return s
	}
	return s[:max] + "..."
}

// TestKubeletAPIPolicy_InsecureMode switches the kubelet-proxy to insecure policy,
// verifies /containerLogs and /logs are allowed, then restores the default policy.
func TestKubeletAPIPolicy_InsecureMode(t *testing.T) {
	enableKubeletInsecureMode(t)
	defer disableKubeletInsecureMode(t)

	t.Run("ContainerLogs_Allowed", func(t *testing.T) {
		t.Log("Verifying /containerLogs is allowed in insecure mode")
		code, body, err := env.CurlKubeletAPI("/containerLogs")
		if err != nil {
			t.Fatalf("curl failed: %v", err)
		}
		t.Logf("Response: HTTP %d", code)
		if code == 403 && strings.Contains(body, "rejected by policy") {
			t.Fatalf("expected allowed in insecure mode, got policy rejection: %s", body)
		}
		t.Log("✓ /containerLogs allowed in insecure mode")
	})

	t.Run("Logs_Allowed", func(t *testing.T) {
		t.Log("Verifying /logs is allowed in insecure mode")
		code, body, err := env.CurlKubeletAPI("/logs")
		if err != nil {
			t.Fatalf("curl failed: %v", err)
		}
		t.Logf("Response: HTTP %d", code)
		if code == 403 && strings.Contains(body, "rejected by policy") {
			t.Fatalf("expected allowed in insecure mode, got policy rejection: %s", body)
		}
		t.Log("✓ /logs allowed in insecure mode")
	})

	t.Run("Exec_Allowed", func(t *testing.T) {
		t.Log("Verifying /exec is allowed in insecure mode")
		code, body, err := env.CurlKubeletAPI("/exec")
		if err != nil {
			t.Fatalf("curl failed: %v", err)
		}
		if code == 403 && strings.Contains(body, "rejected by policy") {
			t.Fatalf("expected allowed in insecure mode, got policy rejection: %s", body)
		}
		t.Log("✓ /exec allowed in insecure mode")
	})

	t.Run("Attach_Blocked", func(t *testing.T) {
		t.Log("Verifying /attach is still blocked in insecure mode")
		code, body, err := env.CurlKubeletAPI("/attach")
		if err != nil {
			t.Fatalf("curl failed: %v", err)
		}
		if code != 403 || !strings.Contains(body, "rejected by policy") {
			t.Fatalf("expected /attach to still be blocked in insecure mode, got HTTP %d: %s",
				code, body)
		}
		t.Log("✓ /attach still blocked in insecure mode (HTTP 403, rejected by policy)")
	})
}

// insecurePolicyPath returns the path to the insecure API policy file.
func insecurePolicyPath() string {
	return filepath.Join(k8sNodeDir, "kubelet-proxy", "scripts", "api-policies",
		"insecure-api-policy.json")
}

// enableKubeletInsecureMode copies the insecure policy to the node and restarts
// the kubelet-proxy.
func enableKubeletInsecureMode(t *testing.T) {
	t.Helper()
	t.Log("Switching kubelet-proxy to insecure policy (allows /containerLogs, /logs)")

	policyPath := insecurePolicyPath()
	if err := env.CopyFileToNode(t, policyPath, "/etc/kubelet-proxy/api-policy.json"); err != nil {
		t.Fatalf("failed to copy insecure policy: %v", err)
	}

	if _, err := env.ExecOnNode("systemctl", "restart", "kubelet-proxy"); err != nil {
		t.Fatalf("failed to restart kubelet-proxy: %v", err)
	}
	waitForServiceActive(t, "kubelet-proxy", 10*time.Second)
	t.Log("kubelet-proxy restarted with insecure policy")
}

// disableKubeletInsecureMode restores the default policy and restarts.
func disableKubeletInsecureMode(t *testing.T) {
	t.Helper()
	t.Log("Restoring kubelet-proxy to default policy")

	defaultPolicy := filepath.Join(k8sNodeDir, "kubelet-proxy", "scripts",
		"api-policies", "default-api-policy.json")
	if err := env.CopyFileToNode(t, defaultPolicy, "/etc/kubelet-proxy/api-policy.json"); err != nil {
		t.Logf("Warning: failed to restore default policy: %v", err)
	}

	if _, err := env.ExecOnNode("systemctl", "restart", "kubelet-proxy"); err != nil {
		t.Logf("Warning: failed to restart kubelet-proxy: %v", err)
	}
	waitForServiceActive(t, "kubelet-proxy", 10*time.Second)
	t.Log("kubelet-proxy restored to default policy")
}
