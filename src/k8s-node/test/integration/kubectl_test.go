// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

//go:build integration && kubelet_proxy && api_server_proxy

package integration

import (
	"encoding/base64"
	"fmt"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
)

const (
	testPodName      = "integration-test-pod"
	testPodNamespace = "default"
)

func deployTestPod(t *testing.T) {
	t.Helper()
	t.Log("Deploying signed busybox test pod to worker node")

	// Delete any existing test pod and wait for it to be gone.
	out, _ := kubectl("delete", "pod", testPodName, "-n", testPodNamespace,
		"--ignore-not-found", "--wait=true", "--timeout=30s")
	t.Logf("Cleanup existing pod: %s", out)

	// Load and sign the busybox pod policy so the api-server-proxy admits the pod.
	policyFile := filepath.Join(podPoliciesDir(), "busybox-pod-policy.json")
	policyJSON := loadPolicyJSON(t, policyFile)
	policyBase64 := base64.StdEncoding.EncodeToString([]byte(policyJSON))
	signature := signPolicy(t, policyBase64)

	// The busybox policy expects container name "test", image "busybox:latest",
	// command ["sleep"]. Args are not constrained by the policy.
	podYAML := fmt.Sprintf(`apiVersion: v1
kind: Pod
metadata:
  name: %s
  namespace: %s
  annotations:
    api-server-proxy.io/policy: "%s"
    api-server-proxy.io/signature: "%s"
spec:
  nodeName: %s
  tolerations:
  - key: "pod-policy"
    operator: "Equal"
    value: "required"
    effect: "NoSchedule"
  containers:
  - name: test
    image: busybox:latest
    command: ["sleep"]
    args: ["infinity"]
    ports:
    - containerPort: 8080
  terminationGracePeriodSeconds: 0`,
		testPodName, testPodNamespace, policyBase64, signature, env.FlexNodeName())

	cmd := exec.Command("kubectl", "apply", "-f", "-")
	cmd.Stdin = strings.NewReader(podYAML)
	applyOut, err := cmd.CombinedOutput()
	t.Logf("kubectl apply: %s", string(applyOut))
	if err != nil {
		t.Fatalf("failed to create test pod: %v\n%s", err, string(applyOut))
	}

	// Wait for ready.
	waitOut, err := runCmdWithTimeout(60, "kubectl", "wait", "--for=condition=Ready",
		fmt.Sprintf("pod/%s", testPodName), "-n", testPodNamespace, "--timeout=60s")
	t.Logf("kubectl wait: %s", waitOut)
	if err != nil {
		t.Fatalf("test pod not ready: %v\n%s", err, waitOut)
	}
	t.Log("Test pod is ready")
}

func cleanupTestPod(t *testing.T) {
	t.Helper()
	t.Log("Cleaning up test pod")
	out, _ := kubectl("delete", "pod", testPodName, "-n", testPodNamespace,
		"--ignore-not-found", "--grace-period=0", "--force")
	t.Logf("kubectl delete: %s", out)
}

func TestKubectlProxy(t *testing.T) {
	if env.KubeletProxyInstallMode() == "insecure" {
		t.Fatal("TestKubectlProxy requires default (non-insecure) install mode")
	}

	t.Run("NodeStatusPort", func(t *testing.T) {
		t.Log("Verifying node status reports kubelet-proxy port (10250)")
		out, err := kubectl("get", "node", env.FlexNodeName(),
			"-o", "jsonpath={.status.daemonEndpoints.kubeletEndpoint.Port}")
		t.Logf("Node kubelet endpoint port: %s", out)
		if err != nil {
			t.Fatalf("failed to get node status: %v", err)
		}
		if out != "10250" {
			t.Fatalf("expected node port 10250, got %s", out)
		}
		t.Log("✓ Node status port is 10250")
	})

	deployTestPod(t)
	defer cleanupTestPod(t)

	t.Run("Exec_Forbidden", func(t *testing.T) {
		t.Log("Verifying kubectl exec is blocked by kubelet-proxy policy")
		out, err := runCmdWithTimeout(15, "kubectl", "exec", testPodName,
			"-n", testPodNamespace, "--", "echo", "hello")
		t.Logf("kubectl exec output: %s", out)
		if err == nil {
			t.Fatal("expected kubectl exec to fail, but it succeeded")
		}
		t.Logf("✓ kubectl exec blocked (exit error: %v)", err)
	})

	t.Run("PortForward_Forbidden", func(t *testing.T) {
		t.Log("Verifying kubectl port-forward is blocked by kubelet-proxy policy")
		out, err := runCmdWithTimeout(15, "kubectl", "port-forward",
			fmt.Sprintf("pod/%s", testPodName), "-n", testPodNamespace, "8080:8080")
		t.Logf("kubectl port-forward output: %s", out)
		if err == nil {
			t.Fatal("expected kubectl port-forward to fail, but it succeeded")
		}
		t.Logf("✓ kubectl port-forward blocked (exit error: %v)", err)
	})

	t.Run("Logs_Forbidden", func(t *testing.T) {
		t.Log("Verifying kubectl logs is blocked in default mode")
		out, err := runCmdWithTimeout(15, "kubectl", "logs", testPodName,
			"-n", testPodNamespace)
		t.Logf("kubectl logs output: %s", out)
		if err == nil {
			t.Fatal("expected kubectl logs to fail in default mode, but it succeeded")
		}
		t.Logf("✓ kubectl logs blocked (exit error: %v)", err)
	})
}

// TestKubectlProxy_InsecureMode switches kubelet-proxy to insecure policy and
// verifies that kubectl logs and port-forward are allowed, then restores the default policy.
func TestKubectlProxy_InsecureMode(t *testing.T) {
	enableKubeletInsecureMode(t)
	defer disableKubeletInsecureMode(t)

	deployTestPod(t)
	defer cleanupTestPod(t)

	t.Run("Logs_Allowed", func(t *testing.T) {
		t.Log("Verifying kubectl logs is allowed in insecure mode")
		out, err := runCmdWithTimeout(15, "kubectl", "logs", testPodName,
			"-n", testPodNamespace)
		t.Logf("kubectl logs output: %s", out)
		if err != nil {
			t.Fatalf("expected kubectl logs to succeed in insecure mode: %v", err)
		}
		t.Log("✓ kubectl logs allowed in insecure mode")
	})

	t.Run("PortForward_Allowed", func(t *testing.T) {
		t.Log("Verifying kubectl port-forward is allowed in insecure mode")
		// port-forward runs indefinitely, so use a short timeout. A successful
		// SPDY upgrade prints "Forwarding from ..." before the timeout kills it.
		// A policy rejection or auth failure returns immediately with an error.
		out, err := runCmdWithTimeout(5, "kubectl", "port-forward",
			fmt.Sprintf("pod/%s", testPodName), "-n", testPodNamespace, "18080:8080")
		t.Logf("kubectl port-forward output: %s", out)
		if strings.Contains(out, "Forwarding from") {
			t.Log("✓ kubectl port-forward allowed in insecure mode")
		} else if err != nil && (strings.Contains(out, "rejected by policy") ||
			strings.Contains(out, "Unauthorized")) {
			t.Fatalf("port-forward was rejected: %s", out)
		} else {
			// timeout exit (124) with "Forwarding from" output is expected success.
			t.Logf("port-forward exited (err=%v), checking output", err)
			if !strings.Contains(out, "error") {
				t.Log("✓ kubectl port-forward allowed in insecure mode (no error in output)")
			} else {
				t.Fatalf("unexpected port-forward error: %s", out)
			}
		}
	})
}
