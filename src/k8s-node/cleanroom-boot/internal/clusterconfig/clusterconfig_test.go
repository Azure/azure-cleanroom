// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package clusterconfig

import "testing"

func TestExtractClusterInfoFromKubeconfig(t *testing.T) {
	yaml := []byte(`
apiVersion: v1
kind: Config
clusters:
- name: c
  cluster:
    server: https://my-cluster.hcp.eastus.azmk8s.io:443
    certificate-authority-data: QUJD
`)
	server, ca, err := extractClusterInfoFromKubeconfig(yaml)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if server != "https://my-cluster.hcp.eastus.azmk8s.io:443" {
		t.Errorf("server = %q", server)
	}
	if ca != "QUJD" {
		t.Errorf("ca = %q", ca)
	}
}

func TestExtractClusterInfoFromKubeconfig_Errors(t *testing.T) {
	cases := map[string][]byte{
		"no clusters":  []byte("apiVersion: v1\nclusters: []\n"),
		"empty server": []byte("clusters:\n- cluster:\n    certificate-authority-data: QUJD\n"),
		"bad yaml":     []byte("clusters: [::"),
	}
	for name, data := range cases {
		if _, _, err := extractClusterInfoFromKubeconfig(data); err == nil {
			t.Errorf("%s: expected error, got nil", name)
		}
	}
}

func TestParseAKSResourceID(t *testing.T) {
	id := "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.ContainerService/managedClusters/aks-1"
	sub, rg, name, err := parseAKSResourceID(id)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if sub != "sub-1" || rg != "rg-1" || name != "aks-1" {
		t.Errorf("parsed sub=%q rg=%q name=%q", sub, rg, name)
	}
}

func TestParseAKSResourceID_Errors(t *testing.T) {
	for _, id := range []string{
		"",
		"/subscriptions/sub-1/resourceGroups/rg-1",
		"/subscriptions/sub-1/providers/Microsoft.ContainerService/managedClusters/aks-1",
	} {
		if _, _, _, err := parseAKSResourceID(id); err == nil {
			t.Errorf("id %q: expected error, got nil", id)
		}
	}
}
