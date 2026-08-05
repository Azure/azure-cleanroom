package admission

import (
	"crypto"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/base64"
	"encoding/pem"
	"fmt"
	"math/big"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// policyDir is the relative path from the test package directory to the shared
// pod-policies used by both Go unit tests and the kind integration tests.
const policyDir = "../../../scripts/pod-policies"

var (
	testPrivKey    *rsa.PrivateKey
	testController *PolicyVerificationController

	// Policies loaded from JSON files in scripts/pod-policies/.
	nginxPolicy          string
	busyboxPolicy        string
	multiContainerPolicy string
	initContainerPolicy  string
	fullPolicy           string
	regexEnvPolicy       string
	argsPolicy           string
)

// TestMain sets up a shared RSA certificate and controller for all tests,
// and loads policy JSON files from the shared scripts/pod-policies directory.
func TestMain(m *testing.M) {
	// Load policy files.
	policies := map[string]*string{
		"nginx-pod-policy.json":           &nginxPolicy,
		"busybox-pod-policy.json":         &busyboxPolicy,
		"multi-container-pod-policy.json": &multiContainerPolicy,
		"init-container-pod-policy.json":  &initContainerPolicy,
		"full-policy-pod-policy.json":     &fullPolicy,
		"regex-env-pod-policy.json":       &regexEnvPolicy,
		"args-pod-policy.json":            &argsPolicy,
	}
	for filename, dest := range policies {
		data, err := os.ReadFile(filepath.Join(policyDir, filename))
		if err != nil {
			fmt.Fprintf(os.Stderr, "failed to load policy %s: %v\n", filename, err)
			os.Exit(1)
		}
		*dest = string(data)
	}

	privKey, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		fmt.Fprintf(os.Stderr, "failed to generate RSA key: %v\n", err)
		os.Exit(1)
	}
	testPrivKey = privKey

	template := &x509.Certificate{
		SerialNumber: big.NewInt(1),
		Subject:      pkix.Name{CommonName: "test-policy-signing"},
		NotBefore:    time.Now().Add(-time.Hour),
		NotAfter:     time.Now().Add(24 * time.Hour),
	}

	certDER, err := x509.CreateCertificate(
		rand.Reader, template, template, &privKey.PublicKey, privKey,
	)
	if err != nil {
		fmt.Fprintf(os.Stderr, "failed to create certificate: %v\n", err)
		os.Exit(1)
	}

	tmpDir, err := os.MkdirTemp("", "admission-test-*")
	if err != nil {
		fmt.Fprintf(os.Stderr, "failed to create temp dir: %v\n", err)
		os.Exit(1)
	}
	defer os.RemoveAll(tmpDir) //nolint:errcheck // Best-effort cleanup in test setup.

	certPath := filepath.Join(tmpDir, "test-cert.pem")
	certFile, err := os.Create(certPath)
	if err != nil {
		fmt.Fprintf(os.Stderr, "failed to create cert file: %v\n", err)
		os.Exit(1)
	}

	encodeErr := pem.Encode(
		certFile, &pem.Block{Type: "CERTIFICATE", Bytes: certDER},
	)
	if encodeErr != nil {
		certFile.Close() //nolint:errcheck // Already handling encodeErr.
		fmt.Fprintf(os.Stderr, "failed to encode cert: %v\n", encodeErr)
		os.Exit(1)
	}
	certFile.Close() //nolint:errcheck // File was just written successfully.

	testController, err = NewPolicyVerificationController(certPath)
	if err != nil {
		fmt.Fprintf(os.Stderr, "failed to create controller: %v\n", err)
		os.Exit(1)
	}

	os.Exit(m.Run())
}

// signPolicy signs the given policy bytes with the RSA private key using RSA-PSS.
func signPolicy(t *testing.T, policyBytes []byte, privKey *rsa.PrivateKey) string {
	t.Helper()

	hash := sha256.Sum256(policyBytes)
	pssOpts := &rsa.PSSOptions{
		SaltLength: rsa.PSSSaltLengthEqualsHash,
		Hash:       crypto.SHA256,
	}
	sig, err := rsa.SignPSS(rand.Reader, privKey, crypto.SHA256, hash[:], pssOpts)
	if err != nil {
		t.Fatalf("failed to sign policy: %v", err)
	}

	return base64.StdEncoding.EncodeToString(sig)
}

// makePod builds a pod object for testing.
func makePod(annotations map[string]interface{}, containers []interface{}) map[string]interface{} {
	pod := map[string]interface{}{
		"metadata": map[string]interface{}{
			"annotations": annotations,
		},
		"spec": map[string]interface{}{
			"containers": containers,
		},
	}
	return pod
}

// makePodWithInit builds a pod with both containers and init containers.
func makePodWithInit(
	annotations map[string]interface{},
	containers, initContainers []interface{},
) map[string]interface{} {
	pod := makePod(annotations, containers)
	spec := pod["spec"].(map[string]interface{})
	spec["initContainers"] = initContainers
	return pod
}

// helper to create a signed pod annotation pair.
func signedAnnotations(t *testing.T, policyJSON string, privKey *rsa.PrivateKey) map[string]interface{} {
	t.Helper()
	policyBytes := []byte(policyJSON)
	policyBase64 := base64.StdEncoding.EncodeToString(policyBytes)
	signature := signPolicy(t, policyBytes, privKey)
	return map[string]interface{}{
		PolicyAnnotation:    policyBase64,
		SignatureAnnotation: signature,
	}
}

func TestAdmit_SignedPodAllowed(t *testing.T) {
	pod := makePod(
		signedAnnotations(t, nginxPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{"name": "test", "image": "nginx:latest"},
		},
	)

	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})

	if !decision.Allowed {
		t.Fatalf("expected pod to be allowed, got denied: %s", decision.Reason)
	}
}

func TestAdmit_BusyboxPolicy_Allowed(t *testing.T) {
	pod := makePod(
		signedAnnotations(t, busyboxPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{
				"name":    "test",
				"image":   "busybox:latest",
				"command": []interface{}{"sleep"},
			},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if !decision.Allowed {
		t.Fatalf("expected pod to be allowed, got denied: %s", decision.Reason)
	}
}

func TestAdmit_BusyboxPolicy_ImageMismatch(t *testing.T) {
	pod := makePod(
		signedAnnotations(t, busyboxPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{
				"name":    "test",
				"image":   "nginx:latest",
				"command": []interface{}{"sleep"},
			},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if decision.Allowed {
		t.Fatal("expected pod to be denied for image mismatch with busybox policy")
	}
	if !strings.Contains(decision.Reason, "image") ||
		!strings.Contains(decision.Reason, "does not match policy") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_MissingPolicyAnnotation(t *testing.T) {
	pod := makePod(map[string]interface{}{}, []interface{}{})
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})

	if decision.Allowed {
		t.Fatal("expected pod to be denied for missing policy annotation")
	}
	if !strings.Contains(decision.Reason, "pod policy required but not found") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_MissingSignatureAnnotation(t *testing.T) {
	policyBase64 := base64.StdEncoding.EncodeToString([]byte(nginxPolicy))
	pod := makePod(
		map[string]interface{}{PolicyAnnotation: policyBase64},
		[]interface{}{},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})

	if decision.Allowed {
		t.Fatal("expected pod to be denied for missing signature annotation")
	}
	if !strings.Contains(decision.Reason, "pod signature required but not found") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_BadSignature(t *testing.T) {
	policyBase64 := base64.StdEncoding.EncodeToString([]byte(nginxPolicy))
	pod := makePod(
		map[string]interface{}{
			PolicyAnnotation:    policyBase64,
			SignatureAnnotation: "aW52YWxpZHNpZ25hdHVyZQ==",
		},
		[]interface{}{
			map[string]interface{}{"name": "test", "image": "nginx:latest"},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})

	if decision.Allowed {
		t.Fatal("expected pod to be denied for bad signature")
	}
	if !strings.Contains(decision.Reason, "policy signature verification failed") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_AllowAllPolicy_Denied(t *testing.T) {
	allowAllPolicy := `["allowall"]`
	pod := makePod(
		signedAnnotations(t, allowAllPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{"name": "any-container", "image": "alpine:latest"},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})

	if decision.Allowed {
		t.Fatal("expected pod to be denied with allowall policy, but it was allowed")
	}
	if !strings.Contains(decision.Reason, "invalid policy") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_InvalidJSON(t *testing.T) {
	invalidJSON := "this is not valid json"
	pod := makePod(
		signedAnnotations(t, invalidJSON, testPrivKey),
		[]interface{}{
			map[string]interface{}{"name": "test", "image": "nginx:latest"},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})

	if decision.Allowed {
		t.Fatal("expected pod to be denied for invalid JSON policy")
	}
	if !strings.Contains(decision.Reason, "invalid policy") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_PolicyMissingName(t *testing.T) {
	policy := `[{"properties": {"image": "nginx:latest"}}]`
	pod := makePod(
		signedAnnotations(t, policy, testPrivKey),
		[]interface{}{
			map[string]interface{}{"name": "test", "image": "nginx:latest"},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})

	if decision.Allowed {
		t.Fatal("expected pod to be denied for policy entry missing name")
	}
	if !strings.Contains(decision.Reason, "non-empty \"name\"") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_PolicyEmptyProperties(t *testing.T) {
	policy := `[{"name": "test", "properties": {}}]`
	pod := makePod(
		signedAnnotations(t, policy, testPrivKey),
		[]interface{}{
			map[string]interface{}{"name": "test", "image": "nginx:latest"},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})

	if decision.Allowed {
		t.Fatal("expected pod to be denied for policy entry with empty properties")
	}
	if !strings.Contains(decision.Reason, "non-empty \"properties\"") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_MultipleContainers(t *testing.T) {
	// Both containers match.
	pod := makePod(
		signedAnnotations(t, multiContainerPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{"name": "web", "image": "nginx:latest"},
			map[string]interface{}{"name": "sidecar", "image": "busybox:latest"},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if !decision.Allowed {
		t.Fatalf("expected pod to be allowed, got: %s", decision.Reason)
	}

	// One container mismatched.
	pod2 := makePod(
		signedAnnotations(t, multiContainerPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{"name": "web", "image": "nginx:latest"},
			map[string]interface{}{"name": "sidecar", "image": "alpine:latest"},
		},
	)
	decision2 := testController.Admit(&Request{Namespace: "default", Name: "test-pod-2", Pod: pod2})
	if decision2.Allowed {
		t.Fatal("expected pod to be denied with mismatched sidecar image")
	}
	if !strings.Contains(decision2.Reason, "does not match policy") {
		t.Fatalf("unexpected reason: %s", decision2.Reason)
	}
}

func TestAdmit_ContainerNotInPolicy(t *testing.T) {
	pod := makePod(
		signedAnnotations(t, nginxPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{"name": "unknown", "image": "nginx:latest"},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})

	if decision.Allowed {
		t.Fatal("expected pod to be denied for unknown container")
	}
	if !strings.Contains(decision.Reason, "not found in policy") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_InitContainers(t *testing.T) {
	// Matching init + regular containers.
	pod := makePodWithInit(
		signedAnnotations(t, initContainerPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{"name": "app", "image": "nginx:latest"},
		},
		[]interface{}{
			map[string]interface{}{"name": "init", "image": "busybox:latest"},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if !decision.Allowed {
		t.Fatalf("expected pod to be allowed, got: %s", decision.Reason)
	}

	// Mismatched init container image.
	pod2 := makePodWithInit(
		signedAnnotations(t, initContainerPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{"name": "app", "image": "nginx:latest"},
		},
		[]interface{}{
			map[string]interface{}{"name": "init", "image": "alpine:latest"},
		},
	)
	decision2 := testController.Admit(&Request{Namespace: "default", Name: "test-pod-2", Pod: pod2})
	if decision2.Allowed {
		t.Fatal("expected pod to be denied with mismatched init container image")
	}
	if !strings.Contains(decision2.Reason, "initContainers") {
		t.Fatalf("unexpected reason: %s", decision2.Reason)
	}
}

func TestAdmit_FullPolicy_Allowed(t *testing.T) {
	pod := makePod(
		signedAnnotations(t, fullPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{
				"name":    "app",
				"image":   "busybox:latest",
				"command": []interface{}{"/bin/myapp"},
				"args":    []interface{}{"--config=/etc/app/config.yaml", "--verbose"},
				"env": []interface{}{
					map[string]interface{}{"name": "APP_ENV", "value": "production"},
					map[string]interface{}{"name": "LOG_LEVEL", "value": "debug"},
				},
				"volumeMounts": []interface{}{
					map[string]interface{}{
						"name": "config", "mountPath": "/etc/app", "readOnly": true,
					},
					map[string]interface{}{
						"name": "data", "mountPath": "/data", "readOnly": false,
					},
				},
			},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if !decision.Allowed {
		t.Fatalf("expected pod to be allowed, got: %s", decision.Reason)
	}
}

func TestAdmit_FullPolicy_CommandMismatch(t *testing.T) {
	pod := makePod(
		signedAnnotations(t, fullPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{
				"name":    "app",
				"image":   "busybox:latest",
				"command": []interface{}{"/bin/sh"},
				"args":    []interface{}{"--config=/etc/app/config.yaml", "--verbose"},
				"env": []interface{}{
					map[string]interface{}{"name": "APP_ENV", "value": "production"},
					map[string]interface{}{"name": "LOG_LEVEL", "value": "debug"},
				},
				"volumeMounts": []interface{}{
					map[string]interface{}{
						"name": "config", "mountPath": "/etc/app", "readOnly": true,
					},
					map[string]interface{}{
						"name": "data", "mountPath": "/data", "readOnly": false,
					},
				},
			},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if decision.Allowed {
		t.Fatal("expected pod to be denied for command mismatch")
	}
	if !strings.Contains(decision.Reason, "command") ||
		!strings.Contains(decision.Reason, "does not match policy") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_FullPolicy_EnvMismatch(t *testing.T) {
	pod := makePod(
		signedAnnotations(t, fullPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{
				"name":    "app",
				"image":   "busybox:latest",
				"command": []interface{}{"/bin/myapp"},
				"args":    []interface{}{"--config=/etc/app/config.yaml", "--verbose"},
				"env": []interface{}{
					map[string]interface{}{"name": "APP_ENV", "value": "development"},
					map[string]interface{}{"name": "LOG_LEVEL", "value": "debug"},
				},
				"volumeMounts": []interface{}{
					map[string]interface{}{
						"name": "config", "mountPath": "/etc/app", "readOnly": true,
					},
					map[string]interface{}{
						"name": "data", "mountPath": "/data", "readOnly": false,
					},
				},
			},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if decision.Allowed {
		t.Fatal("expected pod to be denied for env mismatch")
	}
	if !strings.Contains(decision.Reason, "env var") ||
		!strings.Contains(decision.Reason, "does not match policy") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_FullPolicy_VolumeMountMismatch(t *testing.T) {
	pod := makePod(
		signedAnnotations(t, fullPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{
				"name":    "app",
				"image":   "busybox:latest",
				"command": []interface{}{"/bin/myapp"},
				"args":    []interface{}{"--config=/etc/app/config.yaml", "--verbose"},
				"env": []interface{}{
					map[string]interface{}{"name": "APP_ENV", "value": "production"},
					map[string]interface{}{"name": "LOG_LEVEL", "value": "debug"},
				},
				"volumeMounts": []interface{}{
					map[string]interface{}{
						"name": "config", "mountPath": "/etc/config", "readOnly": true,
					},
					map[string]interface{}{
						"name": "data", "mountPath": "/data", "readOnly": false,
					},
				},
			},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if decision.Allowed {
		t.Fatal("expected pod to be denied for volume mount mismatch")
	}
	if !strings.Contains(decision.Reason, "volume mount") ||
		!strings.Contains(decision.Reason, "does not match policy") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_RegexEnvVar_Allowed(t *testing.T) {
	pod := makePod(
		signedAnnotations(t, regexEnvPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{
				"name":  "app",
				"image": "nginx:latest",
				"env": []interface{}{
					map[string]interface{}{"name": "VERSION", "value": "v1.2.3"},
					map[string]interface{}{"name": "APP_MODE", "value": "production"},
				},
			},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if !decision.Allowed {
		t.Fatalf("expected pod to be allowed with regex env var, got denied: %s", decision.Reason)
	}
}

func TestAdmit_RegexEnvVar_Denied(t *testing.T) {
	pod := makePod(
		signedAnnotations(t, regexEnvPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{
				"name":  "app",
				"image": "nginx:latest",
				"env": []interface{}{
					map[string]interface{}{"name": "VERSION", "value": "latest"},
					map[string]interface{}{"name": "APP_MODE", "value": "production"},
				},
			},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if decision.Allowed {
		t.Fatal("expected pod to be denied for regex env var mismatch")
	}
	if !strings.Contains(decision.Reason, "env var") ||
		!strings.Contains(decision.Reason, "does not match policy") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_Args_Allowed(t *testing.T) {
	pod := makePod(
		signedAnnotations(t, argsPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{
				"name":    "app",
				"image":   "busybox:latest",
				"command": []interface{}{"/bin/myapp"},
				"args":    []interface{}{"--config=/etc/app.conf", "--verbose"},
			},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if !decision.Allowed {
		t.Fatalf("expected pod to be allowed, got: %s", decision.Reason)
	}
}

func TestAdmit_Args_Mismatch(t *testing.T) {
	pod := makePod(
		signedAnnotations(t, argsPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{
				"name":    "app",
				"image":   "busybox:latest",
				"command": []interface{}{"/bin/myapp"},
				"args":    []interface{}{"--config=/etc/other.conf", "--verbose"},
			},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if decision.Allowed {
		t.Fatal("expected pod to be denied for args mismatch")
	}
	if !strings.Contains(decision.Reason, "args") ||
		!strings.Contains(decision.Reason, "does not match policy") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_ExtraCommand_Denied(t *testing.T) {
	// nginx policy has no command; pod specifies one.
	pod := makePod(
		signedAnnotations(t, nginxPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{
				"name":    "test",
				"image":   "nginx:latest",
				"command": []interface{}{"/bin/sh"},
			},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if decision.Allowed {
		t.Fatal("expected pod to be denied for extra command not in policy")
	}
	if !strings.Contains(decision.Reason, "command") ||
		!strings.Contains(decision.Reason, "policy does not allow") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_ExtraArgs_Denied(t *testing.T) {
	// nginx policy has no args; pod specifies them.
	pod := makePod(
		signedAnnotations(t, nginxPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{
				"name":  "test",
				"image": "nginx:latest",
				"args":  []interface{}{"--debug"},
			},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if decision.Allowed {
		t.Fatal("expected pod to be denied for extra args not in policy")
	}
	if !strings.Contains(decision.Reason, "args") ||
		!strings.Contains(decision.Reason, "policy does not allow") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_ExtraEnvVars_Denied(t *testing.T) {
	// nginx policy has no env vars; pod specifies them.
	pod := makePod(
		signedAnnotations(t, nginxPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{
				"name":  "test",
				"image": "nginx:latest",
				"env": []interface{}{
					map[string]interface{}{"name": "SECRET", "value": "leaked"},
				},
			},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if decision.Allowed {
		t.Fatal("expected pod to be denied for extra env vars not in policy")
	}
	if !strings.Contains(decision.Reason, "env var") ||
		!strings.Contains(decision.Reason, "policy does not allow") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_ExtraVolumeMounts_Denied(t *testing.T) {
	// nginx policy has no volume mounts; pod specifies them.
	pod := makePod(
		signedAnnotations(t, nginxPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{
				"name":  "test",
				"image": "nginx:latest",
				"volumeMounts": []interface{}{
					map[string]interface{}{
						"name": "secrets", "mountPath": "/etc/secrets", "readOnly": true,
					},
				},
			},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if decision.Allowed {
		t.Fatal("expected pod to be denied for extra volume mounts not in policy")
	}
	if !strings.Contains(decision.Reason, "volume mount") ||
		!strings.Contains(decision.Reason, "policy does not allow") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_ExtraEnvVar_InPolicy_Denied(t *testing.T) {
	// full policy has APP_ENV and LOG_LEVEL; pod adds an extra EXTRA_VAR.
	pod := makePod(
		signedAnnotations(t, fullPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{
				"name":    "app",
				"image":   "busybox:latest",
				"command": []interface{}{"/bin/myapp"},
				"args":    []interface{}{"--config=/etc/app/config.yaml", "--verbose"},
				"env": []interface{}{
					map[string]interface{}{"name": "APP_ENV", "value": "production"},
					map[string]interface{}{"name": "LOG_LEVEL", "value": "debug"},
					map[string]interface{}{"name": "EXTRA_VAR", "value": "sneaky"},
				},
				"volumeMounts": []interface{}{
					map[string]interface{}{
						"name": "config", "mountPath": "/etc/app", "readOnly": true,
					},
					map[string]interface{}{
						"name": "data", "mountPath": "/data", "readOnly": false,
					},
				},
			},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if decision.Allowed {
		t.Fatal("expected pod to be denied for extra env var not in policy")
	}
	if !strings.Contains(decision.Reason, "EXTRA_VAR") ||
		!strings.Contains(decision.Reason, "not in policy") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_ExtraVolumeMount_InPolicy_Denied(t *testing.T) {
	// full policy has config and data mounts; pod adds an extra secret mount.
	pod := makePod(
		signedAnnotations(t, fullPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{
				"name":    "app",
				"image":   "busybox:latest",
				"command": []interface{}{"/bin/myapp"},
				"args":    []interface{}{"--config=/etc/app/config.yaml", "--verbose"},
				"env": []interface{}{
					map[string]interface{}{"name": "APP_ENV", "value": "production"},
					map[string]interface{}{"name": "LOG_LEVEL", "value": "debug"},
				},
				"volumeMounts": []interface{}{
					map[string]interface{}{
						"name": "config", "mountPath": "/etc/app", "readOnly": true,
					},
					map[string]interface{}{
						"name": "data", "mountPath": "/data", "readOnly": false,
					},
					map[string]interface{}{
						"name": "secret", "mountPath": "/etc/secret", "readOnly": true,
					},
				},
			},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if decision.Allowed {
		t.Fatal("expected pod to be denied for extra volume mount not in policy")
	}
	if !strings.Contains(decision.Reason, "secret") ||
		!strings.Contains(decision.Reason, "not in policy") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_FullPolicy_MultipleMismatches(t *testing.T) {
	// Pod has wrong command, wrong env value, and an extra volume mount.
	pod := makePod(
		signedAnnotations(t, fullPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{
				"name":    "app",
				"image":   "busybox:latest",
				"command": []interface{}{"/bin/sh"},
				"env": []interface{}{
					map[string]interface{}{"name": "APP_ENV", "value": "staging"},
					map[string]interface{}{"name": "LOG_LEVEL", "value": "debug"},
					map[string]interface{}{"name": "EXTRA", "value": "sneaky"},
				},
				"volumeMounts": []interface{}{
					map[string]interface{}{
						"name": "config", "mountPath": "/etc/wrong", "readOnly": true,
					},
					map[string]interface{}{
						"name": "data", "mountPath": "/data", "readOnly": false,
					},
					map[string]interface{}{
						"name": "secret", "mountPath": "/etc/secret", "readOnly": true,
					},
				},
			},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if decision.Allowed {
		t.Fatal("expected pod to be denied for multiple mismatches")
	}
	// Verify the reason contains at least multiple denial reasons.
	reason := decision.Reason
	hasCommand := strings.Contains(reason, "command")
	hasEnv := strings.Contains(reason, "env var")
	hasMount := strings.Contains(reason, "volume mount")
	mismatches := 0
	if hasCommand {
		mismatches++
	}
	if hasEnv {
		mismatches++
	}
	if hasMount {
		mismatches++
	}
	if mismatches < 2 {
		t.Fatalf("expected multiple denial reasons, got: %s", reason)
	}
}

func TestAdmit_PolicySupersetContainers_Allowed(t *testing.T) {
	// Policy has two containers (web + sidecar) but pod only has one (web).
	// This should be allowed — policy can be a superset.
	pod := makePod(
		signedAnnotations(t, multiContainerPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{
				"name":  "web",
				"image": "nginx:latest",
			},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if !decision.Allowed {
		t.Fatalf("expected pod to be allowed when policy has extra containers, got: %s", decision.Reason)
	}
}

func TestAdmit_MaliciousK8sInjectedMount_WritableKubeApiAccess_Denied(t *testing.T) {
	// A malicious user inserts a mount named kube-api-access-* (looks like a
	// Kubernetes auto-injected mount) but sets readOnly=false. The policy
	// engine must reject it.
	pod := makePod(
		signedAnnotations(t, nginxPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{
				"name":  "test",
				"image": "nginx:latest",
				"volumeMounts": []interface{}{
					map[string]interface{}{
						"name":      "kube-api-access-abc12",
						"mountPath": "/var/run/secrets/kubernetes.io/serviceaccount",
						"readOnly":  false,
					},
				},
			},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if decision.Allowed {
		t.Fatal("expected pod to be denied for writable kube-api-access mount")
	}
	if !strings.Contains(decision.Reason, "Kubernetes injected mount") ||
		!strings.Contains(decision.Reason, "must be readOnly") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_MaliciousK8sInjectedMount_WritableServiceAccount_Denied(t *testing.T) {
	// A mount at the service account path with a non-kube-api-access name is
	// NOT recognized as a k8s-injected mount. It should be denied as an extra
	// volume mount not in policy.
	pod := makePod(
		signedAnnotations(t, nginxPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{
				"name":  "test",
				"image": "nginx:latest",
				"volumeMounts": []interface{}{
					map[string]interface{}{
						"name":      "fake-sa-mount",
						"mountPath": "/var/run/secrets/kubernetes.io/serviceaccount",
						"readOnly":  false,
					},
				},
			},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if decision.Allowed {
		t.Fatal("expected pod to be denied for extra volume mount not in policy")
	}
	if !strings.Contains(decision.Reason, "volume mount") ||
		!strings.Contains(decision.Reason, "policy does not allow") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_MaliciousK8sInjectedMount_WrongMountPath_Denied(t *testing.T) {
	// A mount with the kube-api-access-* name but at a non-standard path is
	// NOT recognized as a k8s-injected mount. It should be denied as an extra
	// volume mount not in policy.
	pod := makePod(
		signedAnnotations(t, nginxPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{
				"name":  "test",
				"image": "nginx:latest",
				"volumeMounts": []interface{}{
					map[string]interface{}{
						"name":      "kube-api-access-abc12",
						"mountPath": "/tmp/stolen-tokens",
						"readOnly":  true,
					},
				},
			},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if decision.Allowed {
		t.Fatal("expected pod to be denied for kube-api-access mount at wrong path")
	}
	if !strings.Contains(decision.Reason, "volume mount") ||
		!strings.Contains(decision.Reason, "policy does not allow") {
		t.Fatalf("unexpected reason: %s", decision.Reason)
	}
}

func TestAdmit_K8sInjectedMount_ReadOnly_Allowed(t *testing.T) {
	// A properly readOnly kube-api-access mount should be allowed (skipped by
	// policy checks).
	pod := makePod(
		signedAnnotations(t, nginxPolicy, testPrivKey),
		[]interface{}{
			map[string]interface{}{
				"name":  "test",
				"image": "nginx:latest",
				"volumeMounts": []interface{}{
					map[string]interface{}{
						"name":      "kube-api-access-abc12",
						"mountPath": "/var/run/secrets/kubernetes.io/serviceaccount",
						"readOnly":  true,
					},
				},
			},
		},
	)
	decision := testController.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if !decision.Allowed {
		t.Fatalf("expected pod with readOnly k8s mount to be allowed, got: %s", decision.Reason)
	}
}

func TestAdmit_ECDSASignature(t *testing.T) {
	privKey, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatalf("failed to generate ECDSA key: %v", err)
	}

	template := &x509.Certificate{
		SerialNumber: big.NewInt(1),
		Subject:      pkix.Name{CommonName: "test-ecdsa"},
		NotBefore:    time.Now().Add(-time.Hour),
		NotAfter:     time.Now().Add(24 * time.Hour),
	}
	certDER, err := x509.CreateCertificate(
		rand.Reader, template, template, &privKey.PublicKey, privKey,
	)
	if err != nil {
		t.Fatalf("failed to create certificate: %v", err)
	}

	certPath := filepath.Join(t.TempDir(), "ecdsa-cert.pem")
	certFile, err := os.Create(certPath)
	if err != nil {
		t.Fatalf("failed to create cert file: %v", err)
	}
	if encErr := pem.Encode(certFile, &pem.Block{Type: "CERTIFICATE", Bytes: certDER}); encErr != nil {
		t.Fatalf("failed to encode cert: %v", encErr)
	}
	certFile.Close() //nolint:errcheck // File was just written successfully.

	controller, err := NewPolicyVerificationController(certPath)
	if err != nil {
		t.Fatalf("failed to create controller: %v", err)
	}

	policyBytes := []byte(nginxPolicy)
	policyBase64 := base64.StdEncoding.EncodeToString(policyBytes)

	hash := sha256.Sum256(policyBytes)
	sig, err := ecdsa.SignASN1(rand.Reader, privKey, hash[:])
	if err != nil {
		t.Fatalf("failed to sign: %v", err)
	}
	signature := base64.StdEncoding.EncodeToString(sig)

	pod := makePod(
		map[string]interface{}{
			PolicyAnnotation:    policyBase64,
			SignatureAnnotation: signature,
		},
		[]interface{}{
			map[string]interface{}{"name": "test", "image": "nginx:latest"},
		},
	)

	decision := controller.Admit(&Request{Namespace: "default", Name: "test-pod", Pod: pod})
	if !decision.Allowed {
		t.Fatalf("expected pod to be allowed with ECDSA signature, got: %s", decision.Reason)
	}
}
