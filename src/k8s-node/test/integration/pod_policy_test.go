// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

//go:build integration && api_server_proxy

package integration

import (
	"encoding/base64"
	"fmt"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestPodPolicy(t *testing.T) {
	// Clean up any leftover test pods.
	cleanupPodPolicyResources(t)
	defer cleanupPodPolicyResources(t)

	t.Run("SignedPod_Allowed", testSignedPodAllowed)
	t.Run("UnsignedPod_Rejected", testUnsignedPodRejected)
	t.Run("BadSignature_Rejected", testBadSignatureRejected)
	t.Run("ImageMismatch_Rejected", testImageMismatchRejected)

	// Full policy tests require a ConfigMap for volume mounts.
	createTestVolumes(t)
	t.Run("FullPolicy_Allowed", testFullPolicyAllowed)
	t.Run("CommandMismatch_Rejected", testCommandMismatchRejected)
	t.Run("EnvMismatch_Rejected", testEnvMismatchRejected)
	t.Run("VolumeMismatch_Rejected", testVolumeMismatchRejected)
	t.Run("FakeK8sMount_Rejected", testFakeK8sMountRejected)
	t.Run("Insecure_Allowed", testInsecureAllowed)
}

func cleanupPodPolicyResources(t *testing.T) {
	t.Helper()
	t.Log("Cleaning up pod policy test resources")
	pods := []string{
		"test-signed", "test-unsigned", "test-bad-sig", "test-image-mismatch",
		"test-full-policy", "test-command-mismatch", "test-env-mismatch",
		"test-volume-mismatch", "test-fake-k8s-mount", "test-insecure",
	}
	for _, pod := range pods {
		kubectl("delete", "pod", pod, "-n", "default",
			"--ignore-not-found", "--grace-period=0", "--force")
	}
	kubectl("delete", "configmap", "test-config", "-n", "default", "--ignore-not-found")
	time.Sleep(2 * time.Second)
}

func testSignedPodAllowed(t *testing.T) {
	t.Log("Creating a signed busybox pod — should be ALLOWED by policy")
	policyFile := filepath.Join(podPoliciesDir(), "busybox-pod-policy.json")
	policyJSON := loadPolicyJSON(t, policyFile)
	policyBase64 := base64.StdEncoding.EncodeToString([]byte(policyJSON))
	signature := signPolicy(t, policyBase64)

	// The busybox policy expects container name "test", image "busybox:latest",
	// command ["sleep"]. Args are not constrained by the policy.
	podYAML := fmt.Sprintf(`apiVersion: v1
kind: Pod
metadata:
  name: test-signed
  namespace: default
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
  terminationGracePeriodSeconds: 0`, policyBase64, signature, env.FlexNodeName())

	cmd := exec.Command("kubectl", "apply", "-f", "-")
	cmd.Stdin = strings.NewReader(podYAML)
	out, err := cmd.CombinedOutput()
	t.Logf("kubectl apply: %s", string(out))
	if err != nil {
		t.Fatalf("failed to create signed pod: %v\n%s", err, out)
	}

	// Wait for the pod to be running (not Failed).
	if err := waitForPodRunning(t, "test-signed", 60*time.Second); err != nil {
		t.Fatalf("signed pod should be allowed: %v", err)
	}
	t.Log("✓ Signed pod is running — admitted by policy")
}

func testUnsignedPodRejected(t *testing.T) {
	t.Log("Creating an UNSIGNED pod (no policy annotations) — should be REJECTED")
	podYAML := fmt.Sprintf(`apiVersion: v1
kind: Pod
metadata:
  name: test-unsigned
  namespace: default
spec:
  nodeName: %s
  tolerations:
  - key: "pod-policy"
    operator: "Equal"
    value: "required"
    effect: "NoSchedule"
  containers:
  - name: test-unsigned
    image: busybox:latest
    command: ["sh", "-c", "sleep infinity"]
  terminationGracePeriodSeconds: 0`, env.FlexNodeName())

	cmd := exec.Command("kubectl", "apply", "-f", "-")
	cmd.Stdin = strings.NewReader(podYAML)
	out, err := cmd.CombinedOutput()
	t.Logf("kubectl apply: %s", string(out))
	if err != nil {
		t.Fatalf("failed to create unsigned pod: %v\n%s", err, out)
	}

	// Pod should be rejected (status = Failed).
	if err := waitForPodFailed(t, "test-unsigned", 30*time.Second); err != nil {
		t.Fatalf("unsigned pod should be rejected: %v", err)
	}
	t.Log("✓ Unsigned pod rejected — Failed status")
}

func testBadSignatureRejected(t *testing.T) {
	t.Log("Creating pod with INVALID signature — should be REJECTED")
	policyFile := filepath.Join(podPoliciesDir(), "busybox-pod-policy.json")
	policyJSON := loadPolicyJSON(t, policyFile)
	policyBase64 := base64.StdEncoding.EncodeToString([]byte(policyJSON))
	badSig := base64.StdEncoding.EncodeToString([]byte("bad-signature-data"))

	podYAML := buildSignedPodYAML(
		"test-bad-sig", "default", env.FlexNodeName(),
		"busybox:latest", []string{"sh", "-c", "sleep infinity"},
		policyBase64, badSig, "")

	cmd := exec.Command("kubectl", "apply", "-f", "-")
	cmd.Stdin = strings.NewReader(podYAML)
	out, _ := cmd.CombinedOutput()
	t.Logf("kubectl apply: %s", string(out))

	if err := waitForPodFailed(t, "test-bad-sig", 30*time.Second); err != nil {
		t.Fatalf("pod with bad signature should be rejected: %v", err)
	}
	t.Log("✓ Bad signature pod rejected")
}

func testImageMismatchRejected(t *testing.T) {
	t.Log("Creating pod with wrong image (nginx vs busybox policy) — should be REJECTED")
	policyFile := filepath.Join(podPoliciesDir(), "busybox-pod-policy.json")
	policyJSON := loadPolicyJSON(t, policyFile)
	policyBase64 := base64.StdEncoding.EncodeToString([]byte(policyJSON))
	signature := signPolicy(t, policyBase64)

	// Use nginx instead of busybox — should be rejected by policy.
	podYAML := buildSignedPodYAML(
		"test-image-mismatch", "default", env.FlexNodeName(),
		"nginx:latest", []string{"sh", "-c", "sleep infinity"},
		policyBase64, signature, "")

	cmd := exec.Command("kubectl", "apply", "-f", "-")
	cmd.Stdin = strings.NewReader(podYAML)
	out, _ := cmd.CombinedOutput()
	t.Logf("kubectl apply: %s", string(out))

	if err := waitForPodFailed(t, "test-image-mismatch", 30*time.Second); err != nil {
		t.Fatalf("pod with image mismatch should be rejected: %v", err)
	}
	t.Log("✓ Image mismatch pod rejected")
}

// waitForPodRunning polls until the pod is Running or times out.
func waitForPodRunning(t *testing.T, name string, timeout time.Duration) error {
	t.Helper()
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		out, err := kubectl("get", "pod", name, "-n", "default",
			"-o", "jsonpath={.status.phase}")
		if err != nil {
			if strings.Contains(out, "not found") || strings.Contains(out, "NotFound") {
				return fmt.Errorf("pod %s was deleted", name)
			}
			t.Logf("Warning: kubectl get pod %s failed: %v (output: %s)", name, err, out)
			time.Sleep(2 * time.Second)
			continue
		}
		if out == "Running" {
			return nil
		}
		if out == "Failed" {
			reason, _ := kubectl("get", "pod", name, "-n", "default",
				"-o", "jsonpath={.status.reason}")
			return fmt.Errorf("pod failed with reason: %s", reason)
		}
		time.Sleep(2 * time.Second)
	}
	return fmt.Errorf("timed out waiting for pod %s to be Running", name)
}

// waitForPodFailed polls until the pod phase is Failed or times out.
func waitForPodFailed(t *testing.T, name string, timeout time.Duration) error {
	t.Helper()
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		out, err := kubectl("get", "pod", name, "-n", "default",
			"-o", "jsonpath={.status.phase}")
		if err != nil {
			if strings.Contains(out, "not found") || strings.Contains(out, "NotFound") {
				return fmt.Errorf("pod %s was deleted", name)
			}
			t.Logf("Warning: kubectl get pod %s failed: %v (output: %s)", name, err, out)
		}
		if out == "Failed" {
			return nil
		}
		time.Sleep(2 * time.Second)
	}
	return fmt.Errorf("timed out waiting for pod %s to be Failed", name)
}

// waitForPodRejected polls until the pod is Failed with reason NodeAdmissionRejected.
func waitForPodRejected(t *testing.T, name string, timeout time.Duration) error {
	t.Helper()
	if err := waitForPodFailed(t, name, timeout); err != nil {
		return err
	}
	reason, err := kubectl("get", "pod", name, "-n", "default",
		"-o", "jsonpath={.status.reason}")
	if err != nil {
		return fmt.Errorf("failed to get pod reason: %w (output: %s)", err, reason)
	}
	if reason != "NodeAdmissionRejected" {
		return fmt.Errorf("expected reason NodeAdmissionRejected, got %s", reason)
	}
	return nil
}

// waitForPodAdmitted polls until the pod is Running or Pending (not rejected).
func waitForPodAdmitted(t *testing.T, name string, timeout time.Duration) error {
	t.Helper()
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		phase, err := kubectl("get", "pod", name, "-n", "default",
			"-o", "jsonpath={.status.phase}")
		if err != nil {
			if strings.Contains(phase, "not found") || strings.Contains(phase, "NotFound") {
				return fmt.Errorf("pod %s was deleted", name)
			}
			t.Logf("Warning: kubectl get pod %s failed: %v (output: %s)", name, err, phase)
			time.Sleep(2 * time.Second)
			continue
		}
		switch phase {
		case "Running", "Pending":
			return nil
		case "Failed":
			reason, _ := kubectl("get", "pod", name, "-n", "default",
				"-o", "jsonpath={.status.reason}")
			if reason == "NodeAdmissionRejected" {
				return fmt.Errorf("pod was rejected: %s", reason)
			}
			// Failed for a non-policy reason (e.g., image pull) — still admitted.
			return nil
		}
		time.Sleep(2 * time.Second)
	}
	return fmt.Errorf("timed out waiting for pod %s to be admitted", name)
}

func createTestVolumes(t *testing.T) {
	t.Helper()
	t.Log("Creating test ConfigMap for volume mount tests")
	yaml := `apiVersion: v1
kind: ConfigMap
metadata:
  name: test-config
  namespace: default
data:
  config.yaml: |
    setting: value`
	cmd := exec.Command("kubectl", "apply", "-f", "-")
	cmd.Stdin = strings.NewReader(yaml)
	out, err := cmd.CombinedOutput()
	t.Logf("kubectl apply: %s", string(out))
	if err != nil {
		t.Fatalf("failed to create test ConfigMap: %v\n%s", err, out)
	}
	t.Log("Test volumes created")
}

// fullPolicyPodYAML returns a pod YAML with all properties matching the
// full-policy-pod-policy.json, with optional overrides for mismatch tests.
func fullPolicyPodYAML(
	name string,
	policyBase64, signature string,
	command, mountPath, envValue string,
) string {
	return fmt.Sprintf(`apiVersion: v1
kind: Pod
metadata:
  name: %s
  namespace: default
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
  volumes:
  - name: config
    configMap:
      name: test-config
  - name: data
    emptyDir: {}
  containers:
  - name: app
    image: busybox:latest
    command: ["%s"]
    args: ["--config=/etc/app/config.yaml", "--verbose"]
    env:
    - name: APP_ENV
      value: "%s"
    - name: LOG_LEVEL
      value: "debug"
    volumeMounts:
    - name: config
      mountPath: %s
      readOnly: true
    - name: data
      mountPath: /data
      readOnly: false
  terminationGracePeriodSeconds: 0`,
		name, policyBase64, signature, env.FlexNodeName(),
		command, envValue, mountPath)
}

func testFullPolicyAllowed(t *testing.T) {
	t.Log("Creating pod matching full policy (command, args, env, volumes) — should be ALLOWED")
	policyFile := filepath.Join(podPoliciesDir(), "full-policy-pod-policy.json")
	policyJSON := loadPolicyJSON(t, policyFile)
	policyBase64 := base64.StdEncoding.EncodeToString([]byte(policyJSON))
	signature := signPolicy(t, policyBase64)

	podYAML := fullPolicyPodYAML("test-full-policy", policyBase64, signature,
		"/bin/myapp", "/etc/app", "production")

	cmd := exec.Command("kubectl", "apply", "-f", "-")
	cmd.Stdin = strings.NewReader(podYAML)
	out, err := cmd.CombinedOutput()
	t.Logf("kubectl apply: %s", string(out))
	if err != nil {
		t.Fatalf("failed to create full-policy pod: %v\n%s", err, out)
	}

	if err := waitForPodAdmitted(t, "test-full-policy", 60*time.Second); err != nil {
		t.Fatalf("full-policy pod should be allowed: %v", err)
	}
	t.Log("✓ Full policy pod admitted")
}

func testCommandMismatchRejected(t *testing.T) {
	t.Log("Creating pod with wrong command (/bin/sh vs /bin/myapp) — should be REJECTED")
	policyFile := filepath.Join(podPoliciesDir(), "full-policy-pod-policy.json")
	policyJSON := loadPolicyJSON(t, policyFile)
	policyBase64 := base64.StdEncoding.EncodeToString([]byte(policyJSON))
	signature := signPolicy(t, policyBase64)

	// Use /bin/sh instead of /bin/myapp — command mismatch.
	podYAML := fullPolicyPodYAML("test-command-mismatch", policyBase64, signature,
		"/bin/sh", "/etc/app", "production")

	cmd := exec.Command("kubectl", "apply", "-f", "-")
	cmd.Stdin = strings.NewReader(podYAML)
	out, _ := cmd.CombinedOutput()
	t.Logf("kubectl apply: %s", string(out))

	if err := waitForPodRejected(t, "test-command-mismatch", 30*time.Second); err != nil {
		t.Fatalf("command mismatch pod should be rejected: %v", err)
	}
	t.Log("✓ Command mismatch pod rejected")
}

func testEnvMismatchRejected(t *testing.T) {
	t.Log("Creating pod with wrong env (development vs production) — should be REJECTED")
	policyFile := filepath.Join(podPoliciesDir(), "full-policy-pod-policy.json")
	policyJSON := loadPolicyJSON(t, policyFile)
	policyBase64 := base64.StdEncoding.EncodeToString([]byte(policyJSON))
	signature := signPolicy(t, policyBase64)

	// Use "development" instead of "production" — env mismatch.
	podYAML := fullPolicyPodYAML("test-env-mismatch", policyBase64, signature,
		"/bin/myapp", "/etc/app", "development")

	cmd := exec.Command("kubectl", "apply", "-f", "-")
	cmd.Stdin = strings.NewReader(podYAML)
	out, _ := cmd.CombinedOutput()
	t.Logf("kubectl apply: %s", string(out))

	if err := waitForPodRejected(t, "test-env-mismatch", 30*time.Second); err != nil {
		t.Fatalf("env mismatch pod should be rejected: %v", err)
	}
	t.Log("✓ Env mismatch pod rejected")
}

func testVolumeMismatchRejected(t *testing.T) {
	t.Log("Creating pod with wrong mount path (/etc/config vs /etc/app) — should be REJECTED")
	policyFile := filepath.Join(podPoliciesDir(), "full-policy-pod-policy.json")
	policyJSON := loadPolicyJSON(t, policyFile)
	policyBase64 := base64.StdEncoding.EncodeToString([]byte(policyJSON))
	signature := signPolicy(t, policyBase64)

	// Use /etc/config instead of /etc/app — volume mount path mismatch.
	podYAML := fullPolicyPodYAML("test-volume-mismatch", policyBase64, signature,
		"/bin/myapp", "/etc/config", "production")

	cmd := exec.Command("kubectl", "apply", "-f", "-")
	cmd.Stdin = strings.NewReader(podYAML)
	out, _ := cmd.CombinedOutput()
	t.Logf("kubectl apply: %s", string(out))

	if err := waitForPodRejected(t, "test-volume-mismatch", 30*time.Second); err != nil {
		t.Fatalf("volume mismatch pod should be rejected: %v", err)
	}
	t.Log("✓ Volume mismatch pod rejected")
}

func testFakeK8sMountRejected(t *testing.T) {
	t.Log("Creating pod with writable fake Kubernetes-injected mount — should be REJECTED")
	policyFile := filepath.Join(podPoliciesDir(), "nginx-pod-policy.json")
	policyJSON := loadPolicyJSON(t, policyFile)
	policyBase64 := base64.StdEncoding.EncodeToString([]byte(policyJSON))
	signature := signPolicy(t, policyBase64)

	// Pod with a writable mount pretending to be a Kubernetes-injected mount.
	podYAML := fmt.Sprintf(`apiVersion: v1
kind: Pod
metadata:
  name: test-fake-k8s-mount
  namespace: default
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
    image: nginx:latest
    volumeMounts:
    - name: kube-api-access-abc12
      mountPath: /var/run/secrets/kubernetes.io/serviceaccount
      readOnly: false
  volumes:
  - name: kube-api-access-abc12
    emptyDir: {}
  terminationGracePeriodSeconds: 0`, policyBase64, signature, env.FlexNodeName())

	cmd := exec.Command("kubectl", "apply", "-f", "-")
	cmd.Stdin = strings.NewReader(podYAML)
	out, _ := cmd.CombinedOutput()
	t.Logf("kubectl apply: %s", string(out))

	if err := waitForPodRejected(t, "test-fake-k8s-mount", 30*time.Second); err != nil {
		t.Fatalf("fake k8s mount pod should be rejected: %v", err)
	}
	t.Log("✓ Fake K8s mount pod rejected")
}

func testInsecureAllowed(t *testing.T) {
	t.Log("Enabling insecure mode and creating UNSIGNED pod — should be ALLOWED")
	// Enable insecure mode on api-server-proxy.
	enableInsecureMode(t)
	defer disableInsecureMode(t)

	// Create an unsigned pod — should be allowed in insecure mode.
	podYAML := fmt.Sprintf(`apiVersion: v1
kind: Pod
metadata:
  name: test-insecure
  namespace: default
spec:
  nodeName: %s
  tolerations:
  - key: "pod-policy"
    operator: "Equal"
    value: "required"
    effect: "NoSchedule"
  containers:
  - name: test-insecure
    image: alpine:latest
    command: ["sleep", "3600"]
  terminationGracePeriodSeconds: 0`, env.FlexNodeName())

	cmd := exec.Command("kubectl", "apply", "-f", "-")
	cmd.Stdin = strings.NewReader(podYAML)
	out, err := cmd.CombinedOutput()
	t.Logf("kubectl apply: %s", string(out))
	if err != nil {
		t.Fatalf("failed to create insecure pod: %v\n%s", err, out)
	}

	if err := waitForPodAdmitted(t, "test-insecure", 60*time.Second); err != nil {
		t.Fatalf("unsigned pod should be allowed in insecure mode: %v", err)
	}
	t.Log("✓ Unsigned pod allowed in insecure mode")
}

// enableInsecureMode writes --insecure to the service env file and restarts.
func enableInsecureMode(t *testing.T) {
	t.Helper()
	t.Log("Enabling insecure mode via service-env file")

	_, err := env.ExecOnNode("bash", "-c",
		`echo 'EXTRA_ARGS=--insecure' > /etc/api-server-proxy/service-env`)
	if err != nil {
		t.Fatalf("failed to write service-env: %v", err)
	}
	if _, err := env.ExecOnNode("systemctl", "restart", "api-server-proxy"); err != nil {
		t.Fatalf("failed to restart api-server-proxy: %v", err)
	}

	waitForServiceActive(t, "api-server-proxy", 30*time.Second)
	t.Log("Insecure mode enabled, service restarted")
}

// disableInsecureMode clears the service env file and restarts.
func disableInsecureMode(t *testing.T) {
	t.Helper()
	t.Log("Disabling insecure mode via service-env file")

	_, _ = env.ExecOnNode("bash", "-c",
		`echo 'EXTRA_ARGS=' > /etc/api-server-proxy/service-env`)
	if _, err := env.ExecOnNode("systemctl", "restart", "api-server-proxy"); err != nil {
		t.Logf("Warning: failed to restart api-server-proxy: %v", err)
	}

	waitForServiceActive(t, "api-server-proxy", 10*time.Second)
	t.Log("Insecure mode disabled, service restarted")
}

// waitForServiceActive polls until a systemd service is active or times out.
