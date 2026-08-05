// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

//go:build integration && api_server_proxy

package integration

import (
	"encoding/base64"
	"fmt"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"
)

const (
	networkTestLabel     = "test-suite=flexnode-networking"
	networkTestNamespace = "default"
)

// cleanupNetworkTestPods deletes all pods with the networking test label.
func cleanupNetworkTestPods(t *testing.T) {
	t.Helper()
	out, _ := kubectl("delete", "pods",
		"-l", networkTestLabel,
		"-n", networkTestNamespace,
		"--force", "--grace-period=0", "--ignore-not-found")
	t.Logf("Cleanup: %s", out)
}

// getNodeAllocatablePods reads the allocatable.pods from the flex node.
func getNodeAllocatablePods(t *testing.T) int {
	t.Helper()
	out, err := kubectl("get", "node", env.FlexNodeName(),
		"-o", "jsonpath={.status.allocatable.pods}")
	if err != nil {
		t.Fatalf("failed to get allocatable pods: %v\n%s", err, out)
	}
	n, err := strconv.Atoi(strings.TrimSpace(out))
	if err != nil {
		t.Fatalf("failed to parse allocatable pods %q: %v", out, err)
	}
	return n
}

// deploySignedBusyboxPods deploys count signed busybox pods on the flex node
// using nodeName scheduling.
func deploySignedBusyboxPods(
	t *testing.T,
	count int,
	prefix string,
) {
	t.Helper()

	policyFile := filepath.Join(podPoliciesDir(), "busybox-pod-policy.json")
	policyJSON := loadPolicyJSON(t, policyFile)
	policyBase64 := base64.StdEncoding.EncodeToString([]byte(policyJSON))
	signature := signPolicy(t, policyBase64)

	for i := 1; i <= count; i++ {
		name := fmt.Sprintf("%s-%d", prefix, i)

		podYAML := fmt.Sprintf(`apiVersion: v1
kind: Pod
metadata:
  name: %s
  namespace: %s
  labels:
    test-suite: flexnode-networking
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
  terminationGracePeriodSeconds: 0`,
			name, networkTestNamespace, policyBase64, signature, env.FlexNodeName())

		cmd := exec.Command("kubectl", "apply", "-f", "-")
		cmd.Stdin = strings.NewReader(podYAML)
		out, err := cmd.CombinedOutput()
		if err != nil {
			t.Logf("Warning: failed to create pod %s: %v\n%s", name, err, string(out))
		}
	}
}

// countPodsByPhase counts pods with the networking test label in a given phase.
func countPodsByPhase(t *testing.T, phase string) int {
	t.Helper()
	out, _ := kubectl("get", "pods",
		"-l", networkTestLabel,
		"-n", networkTestNamespace,
		"--field-selector", fmt.Sprintf("status.phase=%s", phase),
		"-o", "name")
	if strings.TrimSpace(out) == "" {
		return 0
	}
	return len(strings.Split(strings.TrimSpace(out), "\n"))
}

// getPodsWithReason returns pod names that have the given status reason.
func getPodsWithReason(t *testing.T, reason string) []string {
	t.Helper()
	out, _ := kubectl("get", "pods",
		"-l", networkTestLabel,
		"-n", networkTestNamespace,
		"-o", fmt.Sprintf(
			`jsonpath={range .items[?(@.status.reason=="%s")]}{.metadata.name}{"\n"}{end}`,
			reason))
	trimmed := strings.TrimSpace(out)
	if trimmed == "" {
		return nil
	}
	return strings.Split(trimmed, "\n")
}

// ---------------------------------------------------------------------------
// Test: kubelet enforces maxPods limit
// ---------------------------------------------------------------------------

const testMaxPods = 10

func TestFlexNodeMaxPods(t *testing.T) {
	t.Cleanup(func() { cleanupNetworkTestPods(t) })
	cleanupNetworkTestPods(t)

	// Set a small maxPods so the test completes quickly.
	originalMaxPods := env.SetKubeletMaxPods(t, testMaxPods)
	t.Cleanup(func() { env.RestoreKubeletMaxPods(t, originalMaxPods) })

	maxPods := getNodeAllocatablePods(t)
	t.Logf("Node allocatable pods: %d", maxPods)

	total := maxPods + 1

	t.Logf("Deploying %d pods (maxPods=%d)...", total, maxPods)
	deploySignedBusyboxPods(t, total, "maxpods-test")

	// Wait for pods to settle.
	t.Log("Waiting 30s for pods to settle...")
	time.Sleep(30 * time.Second)

	running := countPodsByPhase(t, "Running")
	failed := countPodsByPhase(t, "Failed")
	outOfPodsPods := getPodsWithReason(t, "OutOfpods")
	rejected := len(outOfPodsPods)

	t.Logf("Running: %d, Failed: %d, OutOfpods: %d",
		running, failed, len(outOfPodsPods))

	// There may be existing pods on the node, so we can't assert an exact
	// count of running pods. Instead verify that at least one pod was
	// rejected due to the maxPods limit.
	if rejected < 1 {
		t.Errorf("expected at least 1 pod with OutOfpods reason, got %d", rejected)
	}

	t.Logf("✓ maxPods limit enforced: %d running, 1 rejected", running)
}
