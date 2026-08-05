package kubeletproxy

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"fmt"
	"io"
	"log"
	"math/big"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"runtime"
	"testing"
	"time"
)

// generateTestCert creates a self-signed certificate and key in temp files.
func generateTestCert(t *testing.T) (certFile, keyFile string) {
	t.Helper()

	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatalf("Failed to generate key: %v", err)
	}

	template := &x509.Certificate{
		SerialNumber: big.NewInt(1),
		Subject:      pkix.Name{CommonName: "test"},
		NotBefore:    time.Now(),
		NotAfter:     time.Now().Add(1 * time.Hour),
		KeyUsage:     x509.KeyUsageDigitalSignature | x509.KeyUsageKeyEncipherment,
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth, x509.ExtKeyUsageClientAuth},
		IPAddresses:  []net.IP{net.ParseIP("127.0.0.1")},
	}

	certDER, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		t.Fatalf("Failed to create certificate: %v", err)
	}

	certF, err := os.CreateTemp("", "cert-*.pem")
	if err != nil {
		t.Fatalf("Failed to create cert temp file: %v", err)
	}
	err = pem.Encode(certF, &pem.Block{Type: "CERTIFICATE", Bytes: certDER})
	if err != nil {
		t.Fatalf("Failed to encode cert PEM: %v", err)
	}
	err = certF.Close()
	if err != nil {
		t.Fatalf("Failed to close cert file: %v", err)
	}

	keyDER, err := x509.MarshalECPrivateKey(key)
	if err != nil {
		t.Fatalf("Failed to marshal key: %v", err)
	}

	keyF, err := os.CreateTemp("", "key-*.pem")
	if err != nil {
		t.Fatalf("Failed to create key temp file: %v", err)
	}
	err = pem.Encode(keyF, &pem.Block{Type: "EC PRIVATE KEY", Bytes: keyDER})
	if err != nil {
		t.Fatalf("Failed to encode key PEM: %v", err)
	}
	err = keyF.Close()
	if err != nil {
		t.Fatalf("Failed to close key file: %v", err)
	}

	return certF.Name(), keyF.Name()
}

// policyFilePath returns the absolute path to a policy file in scripts/api-policies/.
func policyFilePath(t *testing.T, name string) string {
	t.Helper()
	_, thisFile, _, _ := runtime.Caller(0)
	projectRoot := filepath.Join(filepath.Dir(thisFile), "..", "..")
	p := filepath.Join(projectRoot, "scripts", "api-policies", name)
	if _, err := os.Stat(p); err != nil {
		t.Fatalf("Policy file not found: %s", p)
	}
	return p
}

// writeTempPolicy creates a temporary JSON policy file with the given APIs.
func writeTempPolicy(t *testing.T, apis []string) string {
	t.Helper()
	f, err := os.CreateTemp("", "api-policy-*.json")
	if err != nil {
		t.Fatalf("Failed to create temp policy file: %v", err)
	}

	content := `{"allowed_apis": [`
	for i, api := range apis {
		if i > 0 {
			content += ","
		}
		content += fmt.Sprintf("%q", api)
	}
	content += `]}`
	if _, err := f.WriteString(content); err != nil {
		t.Fatalf("Failed to write policy file: %v", err)
	}
	if err := f.Close(); err != nil {
		t.Fatalf("Failed to close policy file: %v", err)
	}
	return f.Name()
}

type proxyTestCase struct {
	name           string
	path           string
	wantStatusCode int
	wantBody       string
}

func allowed(name, path string) proxyTestCase {
	return proxyTestCase{name: name, path: path, wantStatusCode: http.StatusOK, wantBody: "Hello from kubelet!"}
}

func forbidden(name, path string) proxyTestCase {
	return proxyTestCase{
		name:           name,
		path:           path,
		wantStatusCode: http.StatusForbidden,
		wantBody:       fmt.Sprintf("%v rejected by policy", path),
	}
}

func runProxyTestCases(t *testing.T, policyFile string, cases []proxyTestCase) {
	t.Helper()

	serverCert, serverKey := generateTestCert(t)
	defer os.Remove(serverCert) //nolint:errcheck
	defer os.Remove(serverKey)  //nolint:errcheck
	clientCert, clientKey := generateTestCert(t)
	defer os.Remove(clientCert) //nolint:errcheck
	defer os.Remove(clientKey)  //nolint:errcheck

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			kubeletBackend := httptest.NewTLSServer(
				http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
					w.WriteHeader(http.StatusOK)
					if _, err := w.Write([]byte("Hello from kubelet!")); err != nil {
						return
					}
				}),
			)
			defer kubeletBackend.Close()

			listener, err := net.Listen("tcp", "127.0.0.1:0")
			if err != nil {
				t.Fatalf("Failed to get free port: %v", err)
			}
			proxyAddr := listener.Addr().String()
			_ = listener.Close()

			cfg := &Config{
				KubeletURL:     kubeletBackend.URL,
				ListenAddr:     proxyAddr,
				ServerCertFile: serverCert,
				ServerKeyFile:  serverKey,
				ClientCertFile: clientCert,
				ClientKeyFile:  clientKey,
				APIPolicyFile:  policyFile,
				LogRequests:    true,
			}

			logger := log.New(os.Stdout, "[test] ", log.LstdFlags)
			proxy, err := New(context.Background(), cfg, logger)
			if err != nil {
				t.Fatalf("New failed: %v", err)
			}

			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			go func() { _ = proxy.Run(ctx) }()
			time.Sleep(100 * time.Millisecond)

			client := &http.Client{
				Timeout:   5 * time.Second,
				Transport: &http.Transport{TLSClientConfig: &tls.Config{InsecureSkipVerify: true}},
			}

			resp, err := client.Get("https://" + proxyAddr + tc.path)
			if err != nil {
				t.Fatalf("GET failed: %v", err)
			}
			defer resp.Body.Close() //nolint:errcheck

			if resp.StatusCode != tc.wantStatusCode {
				t.Errorf("Status code = %d, want %d", resp.StatusCode, tc.wantStatusCode)
			}

			body, err := io.ReadAll(resp.Body)
			if err != nil {
				t.Fatalf("Read body failed: %v", err)
			}
			if string(body) != tc.wantBody {
				t.Errorf("Body = %q, want %q", string(body), tc.wantBody)
			}
		})
	}
}

// TestProxyDefaultPolicyFile tests with the default-api-policy.json file.
func TestProxyDefaultPolicyFile(t *testing.T) {
	pf := policyFilePath(t, "default-api-policy.json")

	runProxyTestCases(t, pf, []proxyTestCase{
		// Allowed.
		allowed("PodsAllowed", "/pods"),
		allowed("HealthzAllowed", "/healthz"),
		allowed("MetricsAllowed", "/metrics"),
		allowed("MetricsCadvisorAllowed", "/metrics/cadvisor"),
		allowed("MetricsResourceAllowed", "/metrics/resource"),
		allowed("MetricsProbesAllowed", "/metrics/probes"),
		allowed("StatsAllowed", "/stats"),
		allowed("CheckpointAllowed", "/checkpoint"),

		// Blocked.
		forbidden("ExecForbidden", "/exec"),
		forbidden("RunForbidden", "/run"),
		forbidden("AttachForbidden", "/attach"),
		forbidden("PortForwardForbidden", "/portForward"),
		forbidden("ContainerLogsForbidden", "/containerLogs"),
		forbidden("LogsForbidden", "/logs"),
		forbidden("RunningPodsForbidden", "/runningpods"),
		forbidden("DebugPprofForbidden", "/debug/pprof"),
		forbidden("DebugFlagsForbidden", "/debug/flags/v"),
	})
}

// TestProxyInsecurePolicyFile tests with the insecure-api-policy.json file.
// Insecure policy additionally allows /containerLogs, /logs, /portForward, and /exec.
func TestProxyInsecurePolicyFile(t *testing.T) {
	pf := policyFilePath(t, "insecure-api-policy.json")

	runProxyTestCases(t, pf, []proxyTestCase{
		// Allowed (same as default).
		allowed("PodsAllowed", "/pods"),
		allowed("HealthzAllowed", "/healthz"),
		allowed("MetricsAllowed", "/metrics"),
		allowed("StatsAllowed", "/stats"),
		allowed("CheckpointAllowed", "/checkpoint"),

		// Additionally allowed by insecure policy.
		allowed("ContainerLogsAllowed", "/containerLogs"),
		allowed("LogsAllowed", "/logs"),
		allowed("PortForwardAllowed", "/portForward"),
		allowed("ExecAllowed", "/exec"),

		// Still blocked even in insecure mode.
		forbidden("RunForbidden", "/run"),
		forbidden("AttachForbidden", "/attach"),
		forbidden("RunningPodsForbidden", "/runningpods"),
		forbidden("DebugPprofForbidden", "/debug/pprof"),
	})
}

// TestProxyCustomPolicy tests with an inline custom policy that allows
// only /pods and /healthz.
func TestProxyCustomPolicy(t *testing.T) {
	pf := writeTempPolicy(t, []string{"/pods", "/healthz"})
	defer os.Remove(pf) //nolint:errcheck

	runProxyTestCases(t, pf, []proxyTestCase{
		allowed("PodsAllowed", "/pods"),
		allowed("HealthzAllowed", "/healthz"),
		forbidden("MetricsDenied", "/metrics"),
		forbidden("StatsDenied", "/stats"),
		forbidden("ExecDenied", "/exec"),
	})
}

// TestPolicyEngineDefaultFileEvaluate tests the default policy file engine directly.
func TestPolicyEngineDefaultFileEvaluate(t *testing.T) {
	logger := log.New(os.Stdout, "[test] ", log.LstdFlags)
	pf := policyFilePath(t, "default-api-policy.json")

	engine, err := NewPolicyEngine(context.Background(), pf, logger)
	if err != nil {
		t.Fatalf("NewPolicyEngine failed: %v", err)
	}

	tests := []struct {
		uri  string
		want bool
	}{
		// Allowed.
		{"/pods", true},
		{"/pods/", true},
		{"/pods?watch=true", true},
		{"/healthz", true},
		{"/metrics", true},
		{"/metrics/cadvisor", true},
		{"/metrics/resource", true},
		{"/stats", true},
		{"/stats/summary", true},
		{"/checkpoint", true},

		// Blocked.
		{"/exec", false},
		{"/exec/ns/pod/container", false},
		{"/run", false},
		{"/attach", false},
		{"/portForward", false},
		{"/containerLogs", false},
		{"/logs", false},
		{"/runningpods", false},
		{"/debug/pprof", false},
		{"/debug/flags/v", false},
		{"/unknown", false},
	}

	for _, tc := range tests {
		t.Run(tc.uri, func(t *testing.T) {
			got, err := engine.Evaluate(context.Background(), tc.uri)
			if err != nil {
				t.Fatalf("Evaluate(%q) error: %v", tc.uri, err)
			}
			if got != tc.want {
				t.Errorf("Evaluate(%q) = %v, want %v", tc.uri, got, tc.want)
			}
		})
	}
}

// TestPolicyEngineInsecureFileEvaluate tests the insecure policy file engine directly.
func TestPolicyEngineInsecureFileEvaluate(t *testing.T) {
	logger := log.New(os.Stdout, "[test] ", log.LstdFlags)
	pf := policyFilePath(t, "insecure-api-policy.json")

	engine, err := NewPolicyEngine(context.Background(), pf, logger)
	if err != nil {
		t.Fatalf("NewPolicyEngine failed: %v", err)
	}

	tests := []struct {
		uri  string
		want bool
	}{
		// Same as default.
		{"/pods", true},
		{"/healthz", true},
		{"/metrics", true},
		{"/stats", true},

		// Insecure additions.
		{"/containerLogs", true},
		{"/containerLogs/ns/pod/container", true},
		{"/logs", true},
		{"/logs/syslog", true},
		{"/portForward", true},
		{"/portForward/ns/pod", true},
		{"/exec", true},
		{"/exec/ns/pod/container", true},

		// Still blocked.
		{"/run", false},
		{"/attach", false},
	}

	for _, tc := range tests {
		t.Run(tc.uri, func(t *testing.T) {
			got, err := engine.Evaluate(context.Background(), tc.uri)
			if err != nil {
				t.Fatalf("Evaluate(%q) error: %v", tc.uri, err)
			}
			if got != tc.want {
				t.Errorf("Evaluate(%q) = %v, want %v", tc.uri, got, tc.want)
			}
		})
	}
}
