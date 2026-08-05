// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

//go:build aks && integration && api_server_proxy

package integration

import (
	"fmt"
	"strings"
	"testing"
	"time"
)

// ---------------------------------------------------------------------------
// Test: CNI bridge IP exhaustion (AKS only — needs SSH access to modify CNI)
// ---------------------------------------------------------------------------

func TestFlexNodeCNIExhaustion(t *testing.T) {
	t.Cleanup(func() {
		cleanupNetworkTestPods(t)
		restoreCNIConfig(t)
	})
	cleanupNetworkTestPods(t)

	availableIPs := 4
	totalPods := availableIPs + 1

	deployCNIBridgeConfig(t, "10.99.1.5", "10.99.1.8")

	t.Logf("Deploying %d pods (only %d IPs available)...", totalPods, availableIPs)
	deploySignedBusyboxPods(t, totalPods, "ip-exhaust")

	t.Log("Waiting 20s for sandbox creation...")
	time.Sleep(20 * time.Second)

	running := countPodsByPhase(t, "Running")
	notRunning := totalPods - running

	// Check for FailedCreatePodSandBox events.
	events, _ := kubectl("get", "events",
		"--field-selector", "reason=FailedCreatePodSandBox",
		"-o", "jsonpath={.items[*].message}")

	t.Logf("Running: %d, Not running: %d", running, notRunning)

	if running == availableIPs && notRunning == 1 &&
		strings.Contains(strings.ToLower(events), "no ip addresses available") {
		t.Logf("✓ CNI IP exhaustion: %d pods running, 1 failed (IP exhaustion)", availableIPs)
	} else {
		t.Errorf("expected %d running + 1 IP-exhausted, got %d running + %d not-running\nevents: %s",
			availableIPs, running, notRunning, events)
	}
}

// cniBackupDir holds a snapshot of the node's real CNI configs while the test
// swaps in a limited bridge config.
const cniBackupDir = "/etc/cni/net.d.flexnode-test-backup"

// deployCNIBridgeConfig snapshots the existing CNI configs and replaces them
// with a limited bridge config (few IPs) via SSH.
func deployCNIBridgeConfig(t *testing.T, rangeStart, rangeEnd string) {
	t.Helper()

	// Snapshot whatever CNI configs currently exist (filenames vary by
	// environment — the flex node uses 10-bridge.conflist, not 10-azure.*).
	// Recreate the backup dir fresh so a rerun captures the true originals.
	out, err := env.ExecOnNode("bash", "-c", fmt.Sprintf(
		"rm -rf %s && mkdir -p %s && "+
			"cp -a /etc/cni/net.d/. %s/ 2>/dev/null || true",
		cniBackupDir, cniBackupDir, cniBackupDir))
	if err != nil {
		t.Fatalf("failed to back up CNI config: %v\n%s", err, out)
	}
	t.Logf("CNI backup: snapshotted /etc/cni/net.d -> %s", cniBackupDir)

	bridgeConfig := fmt.Sprintf(`{
  "cniVersion": "0.3.1",
  "name": "test-bridge",
  "type": "bridge",
  "bridge": "cni-test0",
  "isGateway": true,
  "ipMasq": true,
  "ipam": {
    "type": "host-local",
    "ranges": [[{"subnet": "10.99.1.0/24", "rangeStart": "%s", "rangeEnd": "%s"}]]
  }
}`, rangeStart, rangeEnd)

	// Remove existing configs and write the limited bridge config. Also clear
	// any leftover host-local IPAM allocations for this network so the range
	// starts empty (stale allocations from a prior run would pre-exhaust it).
	out, err = env.ExecOnNode("bash", "-c",
		fmt.Sprintf("rm -f /etc/cni/net.d/*.conflist /etc/cni/net.d/*.conf && "+
			"rm -rf /var/lib/cni/networks/test-bridge && "+
			"echo '%s' > /etc/cni/net.d/10-test-bridge.conf", bridgeConfig))
	if err != nil {
		t.Fatalf("failed to deploy bridge CNI config: %v\n%s", err, out)
	}
	t.Log("Deployed limited bridge CNI config")
}

// restoreCNIConfig restores the snapshotted CNI configs and clears the stale
// test bridge so kubelet can create pod sandboxes again.
func restoreCNIConfig(t *testing.T) {
	t.Helper()
	out, err := env.ExecOnNode("bash", "-c", fmt.Sprintf(
		"rm -f /etc/cni/net.d/10-test-bridge.conf; "+
			"rm -rf /var/lib/cni/networks/test-bridge; "+
			"if [ -d %s ]; then "+
			"rm -f /etc/cni/net.d/*.conflist /etc/cni/net.d/*.conf 2>/dev/null; "+
			"cp -a %s/. /etc/cni/net.d/ && rm -rf %s; fi; "+
			// Delete the stale bridges so kubelet recreates cni0 with the
			// restored subnet (avoids 'cni0 already has an IP' errors).
			"ip link delete cni-test0 2>/dev/null || true; "+
			"ip link delete cni0 2>/dev/null || true; "+
			// Restart kubelet so CNI is re-initialised cleanly from the
			// restored config before later pod-dependent tests run.
			"systemctl restart kubelet",
		cniBackupDir, cniBackupDir, cniBackupDir))
	if err != nil {
		t.Logf("Warning: failed to restore CNI config: %v\n%s", err, out)
	} else {
		t.Log("Restored original CNI config")
	}
}

// ---------------------------------------------------------------------------
// Test: Host binary default outbound IP (AKS only — needs real NIC secondary IPs)
// ---------------------------------------------------------------------------

func TestFlexNodeOutboundIP(t *testing.T) {
	// Get the primary IP from the node object.
	primaryIP, err := kubectl("get", "node", env.FlexNodeName(),
		"-o", "jsonpath={.status.addresses[?(@.type==\"InternalIP\")].address}")
	if err != nil || strings.TrimSpace(primaryIP) == "" {
		t.Fatalf("failed to get node InternalIP: %v\n%s", err, primaryIP)
	}
	primaryIP = strings.TrimSpace(primaryIP)
	t.Logf("Primary NIC IP: %s", primaryIP)

	// Check which source IP the kernel selects for outbound.
	routeOut, err := env.ExecOnNode("bash", "-c",
		"ip route get 8.8.8.8 | head -1 | sed 's/.*src //' | awk '{print $1}'")
	if err != nil {
		t.Fatalf("failed to get outbound route: %v\n%s", err, routeOut)
	}
	outboundIP := strings.TrimSpace(routeOut)
	t.Logf("Default outbound source IP: %s", outboundIP)

	// Also verify via Python socket connect.
	socketOut, err := env.ExecOnNode("python3", "-c",
		`import socket; s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM); s.connect(('8.8.8.8',80)); print(s.getsockname()[0]); s.close()`)
	if err != nil {
		t.Logf("Warning: python3 socket check failed: %v", err)
	} else {
		socketIP := strings.TrimSpace(socketOut)
		t.Logf("Socket-selected outbound IP: %s", socketIP)
	}

	// Get all IPs on eth0.
	allIPsOut, _ := env.ExecOnNode("bash", "-c",
		"ip -4 addr show eth0 | grep 'inet ' | awk '{print $2}' | sed 's|/.*||'")
	t.Logf("All IPs on eth0: %s", strings.ReplaceAll(strings.TrimSpace(allIPsOut), "\n", " "))

	if outboundIP == primaryIP {
		t.Logf("✓ Default outbound uses primary IP (%s)", outboundIP)
	} else if strings.Contains(allIPsOut, outboundIP) {
		t.Logf("✓ Default outbound uses a NIC IP (%s), primary is %s", outboundIP, primaryIP)
	} else {
		t.Errorf("default outbound IP (%s) is not any NIC IP. Primary: %s, All: %s",
			outboundIP, primaryIP, allIPsOut)
	}
}
