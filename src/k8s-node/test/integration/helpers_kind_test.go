// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

//go:build kind && integration

package integration

import (
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"
)

const (
	defaultClusterName = "api-server-proxy-test"
	kubeletProxyPort   = 10250
)

var (
	clusterName          = defaultClusterName
	workerNodeName       = clusterName + "-worker"
	controlPlaneNodeName = clusterName + "-control-plane"
)

func init() {
	if name := os.Getenv("CLUSTER_NAME"); name != "" {
		clusterName = name
		workerNodeName = clusterName + "-worker"
		controlPlaneNodeName = clusterName + "-control-plane"
	}
}

// kindEnvironment drives a Kind worker node over docker exec.
type kindEnvironment struct{}

func currentEnv() Environment { return kindEnvironment{} }

func (kindEnvironment) Name() string { return "kind" }

// Init creates the Kind cluster (if needed) and configures the worker node as
// a flex node by deploying both proxies via the single orchestrator. Mirrors
// the AKS flow where deploy-flex-node-vm.sh does the full setup and the deploy
// methods below are no-ops.
func (kindEnvironment) Init() error {
	clusterScript := filepath.Join(k8sNodeDir, "scripts", "kind", "deploy-cluster.sh")
	fmt.Println("--- Ensuring Kind cluster exists ---")
	out, err := runCmd("bash", clusterScript)
	fmt.Println(out)
	if err != nil {
		return fmt.Errorf("creating Kind cluster: %w", err)
	}

	flexScript := filepath.Join(k8sNodeDir, "scripts", "kind", "deploy-kind-flex-node.sh")
	fmt.Println("--- Configuring Kind worker as flex node (install + configure proxies) ---")
	out, err = runCmd("bash", flexScript)
	fmt.Println(out)
	if err != nil {
		return fmt.Errorf("configuring Kind flex node: %w", err)
	}
	return nil
}

// ExecOnNode runs a command on the worker node via docker exec.
func (kindEnvironment) ExecOnNode(args ...string) (string, error) {
	fullArgs := append([]string{"exec", workerNodeName}, args...)
	return runCmd("docker", fullArgs...)
}

// CurlKubeletAPI curls a kubelet API path from inside the worker node.
func (e kindEnvironment) CurlKubeletAPI(path string) (httpCode int, body string, err error) {
	url := fmt.Sprintf("https://127.0.0.1:%d%s", kubeletProxyPort, path)
	out, cmdErr := e.ExecOnNode(
		"curl", "-sk", "--max-time", "5", "-w", "\n%{http_code}", url,
	)

	lines := strings.Split(out, "\n")
	if cmdErr != nil || len(lines) < 1 {
		return 0, out, fmt.Errorf("curl failed: %w (output: %s)", cmdErr, out)
	}

	codeStr := strings.TrimSpace(lines[len(lines)-1])
	body = strings.Join(lines[:len(lines)-1], "\n")

	code, parseErr := strconv.Atoi(codeStr)
	if parseErr != nil || code < 100 || code > 599 {
		return 0, body, fmt.Errorf("invalid HTTP code %q in curl output", codeStr)
	}

	return code, body, nil
}

// FlexNodeName returns the worker node name for kubectl commands.
func (kindEnvironment) FlexNodeName() string {
	return workerNodeName
}

// DeployAPIServerProxy is a no-op — the proxy is already deployed by
// deploy-kind-flex-node.sh in Init.
func (kindEnvironment) DeployAPIServerProxy() error {
	fmt.Println("  (proxy already deployed by deploy-kind-flex-node.sh)")
	return nil
}

// DeployKubeletProxy is a no-op — the proxy is already deployed by
// deploy-kind-flex-node.sh in Init.
func (kindEnvironment) DeployKubeletProxy() error {
	fmt.Println("  (proxy already deployed by deploy-kind-flex-node.sh)")
	return nil
}

// UninstallProxy is a no-op — proxies are managed by deploy-kind-flex-node.sh.
func (kindEnvironment) UninstallProxy(name, script string) (string, error) {
	fmt.Printf("  (skipping uninstall of %s, managed by deploy-kind-flex-node.sh)\n", name)
	return "", nil
}

// CopyFileToNode copies a local file to a path on the Kind worker node.
func (kindEnvironment) CopyFileToNode(t *testing.T, localPath, remotePath string) error {
	t.Helper()
	_, err := runCmd("docker", "cp", localPath, workerNodeName+":"+remotePath)
	return err
}

// Teardown deletes the Kind cluster.
func (kindEnvironment) Teardown() {
	fmt.Println("--- Tearing down Kind cluster ---")
	script := filepath.Join(k8sNodeDir, "scripts", "kind", "teardown-cluster.sh")
	out, err := runCmd("bash", script)
	fmt.Println(out)
	if err != nil {
		fmt.Printf("  Warning: teardown failed: %v\n", err)
	} else {
		fmt.Println("  Kind cluster deleted")
	}
}

// SigningKeyDir returns the path to the generated signing keys.
func (kindEnvironment) SigningKeyDir() string {
	// deploy-kind-flex-node.sh stores signing keys in the shared kind generated dir.
	return filepath.Join(k8sNodeDir, "scripts", "kind",
		"generated", "policy-signing-keys")
}

// KubeletProxyInstallMode reads the kubelet-proxy install mode (default or insecure).
func (kindEnvironment) KubeletProxyInstallMode() string {
	configFile := filepath.Join(k8sNodeDir, "scripts", "kind",
		"generated", "install-config.json")
	data, err := os.ReadFile(configFile)
	if err != nil {
		return "default"
	}

	content := string(data)
	if strings.Contains(content, `"insecure"`) {
		return "insecure"
	}
	return "default"
}

// SetKubeletMaxPods sets maxPods in /var/lib/kubelet/config.yaml (kind's kubelet
// ignores $KUBELET_EXTRA_ARGS) and restarts kubelet. Backing up/restoring that
// file preserves the port: 10251 set by kubelet-proxy's configure.sh.
func (e kindEnvironment) SetKubeletMaxPods(t *testing.T, maxPods int) int {
	t.Helper()

	original := getNodeAllocatablePods(t)
	t.Logf("Current maxPods: %d, setting to: %d", original, maxPods)

	maxPodsStr := strconv.Itoa(maxPods)
	kubeletCfg := "/var/lib/kubelet/config.yaml"
	backup := kubeletCfg + ".maxpods-test-backup"
	out, err := e.ExecOnNode("bash", "-c", fmt.Sprintf(
		"[ -f %s ] || cp %s %s; "+
			"if grep -q '^maxPods:' %s; then "+
			"sed -i 's/^maxPods:.*/maxPods: %s/' %s; "+
			"else echo 'maxPods: %s' >> %s; fi",
		backup, kubeletCfg, backup,
		kubeletCfg, maxPodsStr, kubeletCfg,
		maxPodsStr, kubeletCfg))
	if err != nil {
		t.Fatalf("failed to set maxPods: %v\n%s", err, out)
	}

	out, err = e.ExecOnNode("systemctl", "restart", "kubelet")
	if err != nil {
		t.Fatalf("failed to restart kubelet: %v\n%s", err, out)
	}

	time.Sleep(5 * time.Second)

	out, err = e.ExecOnNode("systemctl", "is-active", "--quiet", "kubelet")
	if err != nil {
		t.Fatalf("kubelet not active after restart: %v\n%s", err, out)
	}
	t.Logf("kubelet restarted with maxPods=%d", maxPods)
	return original
}

// RestoreKubeletMaxPods restores the backed-up kubelet config and restarts kubelet.
func (e kindEnvironment) RestoreKubeletMaxPods(t *testing.T, original int) {
	t.Helper()
	t.Logf("Restoring kubelet maxPods to %d", original)

	kubeletCfg := "/var/lib/kubelet/config.yaml"
	backup := kubeletCfg + ".maxpods-test-backup"
	e.ExecOnNode("bash", "-c", //nolint:errcheck
		fmt.Sprintf("[ -f %s ] && mv %s %s", backup, backup, kubeletCfg))
	e.ExecOnNode("systemctl", "restart", "kubelet") //nolint:errcheck
	time.Sleep(5 * time.Second)
}
