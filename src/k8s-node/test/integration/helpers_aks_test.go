// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

//go:build aks && integration

package integration

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"
)

const (
	kubeletProxyPort = 10250
)

// aksEnvironment drives an AKS flex-node VM over ssh + az.
type aksEnvironment struct{}

func currentEnv() Environment { return aksEnvironment{} }

func (aksEnvironment) Name() string { return "aks" }

var (
	aksVMPublicIP      string
	aksSSHKeyFile      string
	aksFlexNodeName    string
	aksSSHOpts         []string
	originalKubeconfig string
)

// Init deploys the AKS cluster and VM if not already present, then loads SSH
// credentials and node info.
func (e aksEnvironment) Init() error {
	configDir := filepath.Join(k8sNodeDir, "scripts", "aks", "generated")
	vmConfigFile := filepath.Join(configDir, "flex-node-vm-config.json")

	// Deploy AKS cluster + flex node VM if config doesn't exist.
	if _, err := os.Stat(vmConfigFile); err != nil {
		fmt.Println("--- AKS infrastructure not found, deploying ---")

		fmt.Println("--- Creating AKS cluster ---")
		clusterScript := filepath.Join(k8sNodeDir, "scripts", "aks", "deploy-cluster.sh")
		out, err := runCmd("bash", clusterScript)
		fmt.Println(out)
		if err != nil {
			return fmt.Errorf("deploying AKS cluster: %w", err)
		}

		fmt.Println("--- Deploying flex node VM ---")
		vmScript := filepath.Join(k8sNodeDir, "scripts", "aks", "deploy-flex-node-vm.sh")
		out, err = runCmd("bash", vmScript, "--max-pods-per-node", "5")
		fmt.Println(out)
		if err != nil {
			return fmt.Errorf("deploying flex node VM: %w", err)
		}
	}

	// Save and set KUBECONFIG for kubectl commands.
	originalKubeconfig = os.Getenv("KUBECONFIG")
	kubeconfig := filepath.Join(configDir, "kubeconfig")
	if _, err := os.Stat(kubeconfig); err == nil {
		os.Setenv("KUBECONFIG", kubeconfig)
	}

	return loadAKSConfig()
}

func loadAKSConfig() error {
	configDir := filepath.Join(k8sNodeDir, "scripts", "aks", "generated")

	vmConfigFile := filepath.Join(configDir, "flex-node-vm-config.json")
	vmData, err := os.ReadFile(vmConfigFile)
	if err != nil {
		return fmt.Errorf("reading VM config: %w", err)
	}

	var vmConfig struct {
		ResourceGroup string `json:"resourceGroup"`
		VMName        string `json:"vmName"`
		SSHPrivateKey string `json:"sshPrivateKeyFile"`
	}
	if err := json.Unmarshal(vmData, &vmConfig); err != nil {
		return fmt.Errorf("parsing VM config: %w", err)
	}

	aksSSHKeyFile = vmConfig.SSHPrivateKey
	aksSSHOpts = []string{
		"-i", aksSSHKeyFile,
		"-o", "StrictHostKeyChecking=no",
		"-o", "UserKnownHostsFile=/dev/null",
	}

	ip, err := runCmd("az", "vm", "show",
		"--resource-group", vmConfig.ResourceGroup,
		"--name", vmConfig.VMName,
		"--show-details", "--query", "publicIps", "-o", "tsv")
	if err != nil {
		return fmt.Errorf("getting VM IP: %w", err)
	}
	aksVMPublicIP = strings.TrimSpace(ip)

	out, err := kubectl("get", "nodes", "--no-headers",
		"-o", "custom-columns=NAME:.metadata.name")
	if err != nil {
		return fmt.Errorf("listing nodes: %w", err)
	}
	for _, name := range strings.Split(out, "\n") {
		name = strings.TrimSpace(name)
		if name != "" && !strings.HasPrefix(name, "aks-") {
			aksFlexNodeName = name
			break
		}
	}
	if aksFlexNodeName == "" {
		return fmt.Errorf("no flex node found")
	}

	return nil
}

func (aksEnvironment) ExecOnNode(args ...string) (string, error) {
	sshArgs := append([]string{}, aksSSHOpts...)
	sshArgs = append(sshArgs, fmt.Sprintf("azureuser@%s", aksVMPublicIP))
	remoteCmd := "sudo"
	for _, a := range args {
		remoteCmd += " " + shellescape(a)
	}
	sshArgs = append(sshArgs, remoteCmd)
	return runCmd("ssh", sshArgs...)
}

func shellescape(s string) string {
	if !strings.ContainsAny(s, " '\"\\$!&|;(){}*?#~<>`\t\n") {
		return s
	}
	return "'" + strings.ReplaceAll(s, "'", `'\''`) + "'"
}

func (aksEnvironment) CurlKubeletAPI(path string) (httpCode int, body string, err error) {
	// Use the node's internal IP so traffic goes through iptables PREROUTING.
	cmd := fmt.Sprintf(
		"NODE_IP=$(hostname -I | awk '{print $1}'); curl -sk --max-time 5 -w '\\n%%{http_code}' https://$NODE_IP:%d%s",
		kubeletProxyPort, shellescape(path))
	sshArgs := append([]string{}, aksSSHOpts...)
	sshArgs = append(sshArgs, fmt.Sprintf("azureuser@%s", aksVMPublicIP), cmd)
	out, cmdErr := runCmd("ssh", sshArgs...)

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

func (aksEnvironment) FlexNodeName() string {
	return aksFlexNodeName
}

// DeployAPIServerProxy is a no-op — the proxy is already deployed by
// deploy-flex-node-vm.sh before the flex-node agent starts.
func (aksEnvironment) DeployAPIServerProxy() error {
	fmt.Println("  (proxy already deployed by deploy-flex-node-vm.sh)")
	return nil
}

// DeployKubeletProxy is a no-op — the proxy is already deployed by
// deploy-flex-node-vm.sh before the flex-node agent starts.
func (aksEnvironment) DeployKubeletProxy() error {
	fmt.Println("  (proxy already deployed by deploy-flex-node-vm.sh)")
	return nil
}

func (aksEnvironment) SigningKeyDir() string {
	// deploy-flex-node-vm.sh stores signing keys in the shared AKS generated dir.
	return filepath.Join(k8sNodeDir, "scripts", "aks",
		"generated", "policy-signing-keys")
}

func (aksEnvironment) KubeletProxyInstallMode() string {
	configFile := filepath.Join(k8sNodeDir, "kubelet-proxy", "scripts", "aks",
		"generated", "install-config.json")
	data, err := os.ReadFile(configFile)
	if err != nil {
		return "default"
	}
	if strings.Contains(string(data), `"insecure"`) {
		return "insecure"
	}
	return "default"
}

// UninstallProxy is a no-op — proxies are managed by deploy-flex-node-vm.sh.
func (aksEnvironment) UninstallProxy(name, script string) (string, error) {
	fmt.Printf("  (skipping uninstall (managed by deploy-flex-node-vm.sh) of %s)\n", name)
	return "", nil
}

func (e aksEnvironment) CopyFileToNode(t *testing.T, localPath, remotePath string) error {
	t.Helper()
	tmpDest := fmt.Sprintf("/tmp/%s", filepath.Base(localPath))
	scpArgs := append([]string{}, aksSSHOpts...)
	scpArgs = append(scpArgs, localPath, fmt.Sprintf("azureuser@%s:%s", aksVMPublicIP, tmpDest))
	if _, err := runCmd("scp", scpArgs...); err != nil {
		return fmt.Errorf("scp failed: %w", err)
	}
	_, err := e.ExecOnNode("mv", tmpDest, remotePath)
	return err
}

func (aksEnvironment) Teardown() {
	fmt.Println("--- Tearing down AKS cluster ---")
	script := filepath.Join(k8sNodeDir, "scripts", "aks", "teardown-cluster.sh")
	out, err := runCmd("bash", script)
	fmt.Println(out)
	if err != nil {
		fmt.Printf("  Warning: teardown failed: %v\n", err)
	}
	if originalKubeconfig != "" {
		os.Setenv("KUBECONFIG", originalKubeconfig)
	} else {
		os.Unsetenv("KUBECONFIG")
	}
}

// SetKubeletMaxPods overrides KUBELET_EXTRA_ARGS via a high-numbered systemd
// drop-in (preserving --port=10251) and restarts kubelet.
func (e aksEnvironment) SetKubeletMaxPods(t *testing.T, maxPods int) int {
	t.Helper()

	original := getNodeAllocatablePods(t)
	t.Logf("Current maxPods: %d, setting to: %d", original, maxPods)

	maxPodsStr := strconv.Itoa(maxPods)
	dropIn := "/etc/systemd/system/kubelet.service.d/99-test-max-pods.conf"
	content := fmt.Sprintf(
		"[Service]\nEnvironment=\"KUBELET_EXTRA_ARGS=--port=10251 --max-pods=%s\"\n",
		maxPodsStr)
	out, err := e.ExecOnNode("bash", "-c",
		fmt.Sprintf("echo '%s' > %s && systemctl daemon-reload", content, dropIn))
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

// RestoreKubeletMaxPods removes the test drop-in and restarts kubelet.
func (e aksEnvironment) RestoreKubeletMaxPods(t *testing.T, original int) {
	t.Helper()
	t.Logf("Restoring kubelet maxPods to %d", original)

	dropIn := "/etc/systemd/system/kubelet.service.d/99-test-max-pods.conf"
	e.ExecOnNode("bash", "-c", //nolint:errcheck
		fmt.Sprintf("rm -f %s && systemctl daemon-reload", dropIn))
	e.ExecOnNode("systemctl", "restart", "kubelet") //nolint:errcheck
	time.Sleep(5 * time.Second)
}
