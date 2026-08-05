// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

//go:build integration

package integration

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// Paths resolved relative to the repo root.
var (
	repoRoot   string
	k8sNodeDir string
)

func TestMain(m *testing.M) {
	// Resolve repo root (go test sets cwd to the package directory).
	var err error
	repoRoot, err = filepath.Abs(filepath.Join("..", "..", "..", ".."))
	if err != nil {
		fmt.Fprintf(os.Stderr, "failed to resolve repo root: %v\n", err)
		os.Exit(1)
	}
	k8sNodeDir = filepath.Join(repoRoot, "src", "k8s-node")

	fmt.Println("=== Integration Test Setup ===")
	fmt.Printf("  Repo root:   %s\n", repoRoot)
	fmt.Printf("  k8s-node:    %s\n", k8sNodeDir)
	fmt.Printf("  Environment: %s\n", env.Name())

	if err := setup(); err != nil {
		fmt.Fprintf(os.Stderr, "setup failed: %v\n", err)
		os.Exit(1)
	}

	code := m.Run()

	// Tear down the cluster unless KEEP_CLUSTER=true is set.
	if code == 0 && os.Getenv("KEEP_CLUSTER") != "true" {
		// env.Teardown()
	} else if code != 0 {
		fmt.Println("--- Tests FAILED, keeping cluster for debugging ---")
	} else {
		fmt.Println("--- KEEP_CLUSTER=true, skipping teardown ---")
	}

	os.Exit(code)
}

func setup() error {
	if err := env.Init(); err != nil {
		return fmt.Errorf("initializing environment: %w", err)
	}

	fmt.Println("--- Uninstalling existing proxies (if any) ---")
	uninstallProxies()

	fmt.Println("--- Deploying api-server-proxy ---")
	if err := env.DeployAPIServerProxy(); err != nil {
		return fmt.Errorf("deploying api-server-proxy: %w", err)
	}
	fmt.Println("  api-server-proxy deployed successfully")

	fmt.Println("--- Deploying kubelet-proxy ---")
	if err := env.DeployKubeletProxy(); err != nil {
		return fmt.Errorf("deploying kubelet-proxy: %w", err)
	}
	fmt.Println("  kubelet-proxy deployed successfully")

	// Verify both proxies are running.
	for _, svc := range []string{"api-server-proxy", "kubelet-proxy"} {
		out, err := env.ExecOnNode("systemctl", "is-active", "--quiet", svc)
		if err != nil {
			return fmt.Errorf("%s is not running on worker node: %s", svc, out)
		}
		fmt.Printf("  %s: active ✓\n", svc)
	}

	fmt.Println("--- Setup complete ---")
	return nil
}

// uninstallProxies removes both proxies from the worker node if they exist.
func uninstallProxies() {
	for _, name := range []string{"kubelet-proxy", "api-server-proxy"} {
		script := filepath.Join(k8sNodeDir, name, "scripts", "uninstall.sh")
		if _, err := os.Stat(script); err != nil {
			continue
		}
		out, err := env.UninstallProxy(name, script)
		if err != nil {
			fmt.Printf("  Warning: uninstall %s: %v\n%s\n", name, err, out)
		} else {
			fmt.Printf("  Uninstalled %s\n", name)
		}
	}
}

// kubectl runs a kubectl command and returns combined stdout+stderr.
func kubectl(args ...string) (string, error) {
	return runCmd("kubectl", args...)
}

// waitForServiceActive polls until a systemd service is active or times out.
func waitForServiceActive(t *testing.T, service string, timeout time.Duration) {
	t.Helper()
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		out, err := env.ExecOnNode("systemctl", "is-active", service)
		if err == nil && strings.Contains(out, "active") && !strings.Contains(out, "inactive") {
			return
		}
		time.Sleep(500 * time.Millisecond)
	}
	t.Fatalf("service %s not active after %v", service, timeout)
}

// runCmd runs a command and returns combined output.
func runCmd(name string, args ...string) (string, error) {
	cmd := exec.Command(name, args...)
	out, err := cmd.CombinedOutput()
	return strings.TrimSpace(string(out)), err
}

// runCmdWithTimeout runs a command with a timeout wrapper.
func runCmdWithTimeout(seconds int, name string, args ...string) (string, error) {
	fullArgs := append([]string{fmt.Sprintf("%d", seconds), name}, args...)
	return runCmd("timeout", fullArgs...)
}

// requireCmdSuccess asserts that a command succeeds.
func requireCmdSuccess(t *testing.T, name string, args ...string) string {
	t.Helper()
	out, err := runCmd(name, args...)
	if err != nil {
		t.Fatalf("command failed: %s %s\n  error: %v\n  output: %s",
			name, strings.Join(args, " "), err, out)
	}
	return out
}

// requireCmdFailure asserts that a command fails.
func requireCmdFailure(t *testing.T, name string, args ...string) string {
	t.Helper()
	out, err := runCmd(name, args...)
	if err == nil {
		t.Fatalf("expected command to fail: %s %s\n  output: %s",
			name, strings.Join(args, " "), out)
	}
	return out
}
