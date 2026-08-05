// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

//go:build integration && api_server_proxy

package integration

import (
	"crypto"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/json"
	"encoding/pem"
	"fmt"
	"os"
	"sort"
	"strings"
	"testing"
)

// loadPolicyJSON loads and compacts a policy JSON file with sorted keys.
func loadPolicyJSON(t *testing.T, path string) string {
	t.Helper()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("failed to read policy file %s: %v", path, err)
	}

	var raw interface{}
	if err := json.Unmarshal(data, &raw); err != nil {
		t.Fatalf("failed to parse policy JSON: %v", err)
	}

	sorted := sortJSON(raw)
	compact, err := json.Marshal(sorted)
	if err != nil {
		t.Fatalf("failed to marshal sorted JSON: %v", err)
	}

	return string(compact)
}

// sortJSON recursively sorts map keys for deterministic output.
func sortJSON(v interface{}) interface{} {
	switch val := v.(type) {
	case map[string]interface{}:
		keys := make([]string, 0, len(val))
		for k := range val {
			keys = append(keys, k)
		}
		sort.Strings(keys)
		sorted := make(map[string]interface{}, len(val))
		for _, k := range keys {
			sorted[k] = sortJSON(val[k])
		}
		return sorted
	case []interface{}:
		for i, item := range val {
			val[i] = sortJSON(item)
		}
		return val
	default:
		return v
	}
}

// signPolicy signs a base64-encoded policy with RSA-PSS SHA-256.
func signPolicy(t *testing.T, policyBase64 string) string {
	t.Helper()

	keyData, err := os.ReadFile(signingKeyPath())
	if err != nil {
		t.Fatalf("failed to read signing key: %v", err)
	}

	block, _ := pem.Decode(keyData)
	if block == nil {
		t.Fatal("failed to decode PEM block from signing key")
	}

	key, err := x509.ParsePKCS8PrivateKey(block.Bytes)
	if err != nil {
		// Try PKCS1 format.
		rsaKey, err2 := x509.ParsePKCS1PrivateKey(block.Bytes)
		if err2 != nil {
			t.Fatalf("failed to parse signing key: PKCS8: %v, PKCS1: %v", err, err2)
		}
		key = rsaKey
	}

	rsaKey, ok := key.(*rsa.PrivateKey)
	if !ok {
		t.Fatalf("signing key is not RSA: %T", key)
	}

	policyBytes, err := base64.StdEncoding.DecodeString(policyBase64)
	if err != nil {
		t.Fatalf("failed to decode policy base64: %v", err)
	}

	hash := sha256.Sum256(policyBytes)
	signature, err := rsa.SignPSS(rand.Reader, rsaKey, crypto.SHA256, hash[:],
		&rsa.PSSOptions{SaltLength: rsa.PSSSaltLengthEqualsHash})
	if err != nil {
		t.Fatalf("failed to sign policy: %v", err)
	}

	return base64.StdEncoding.EncodeToString(signature)
}

// podPoliciesDir returns the path to the shared pod-policies directory.
func podPoliciesDir() string {
	return fmt.Sprintf("%s/api-server-proxy/scripts/pod-policies", k8sNodeDir)
}

// buildSignedPodYAML creates a pod YAML string with policy and signature
// annotations.
func buildSignedPodYAML(
	name, namespace, nodeName, image string,
	command []string, policyBase64, signature string,
	extraSpec string,
) string {
	cmdYAML := ""
	if len(command) > 0 {
		parts := make([]string, len(command))
		for i, c := range command {
			parts[i] = fmt.Sprintf(`"%s"`, c)
		}
		cmdYAML = fmt.Sprintf("    command: [%s]", strings.Join(parts, ", "))
	}

	return fmt.Sprintf(`apiVersion: v1
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
  - name: %s
    image: %s
%s
%s
  terminationGracePeriodSeconds: 0`,
		name, namespace, policyBase64, signature,
		nodeName, name, image, cmdYAML, extraSpec)
}
