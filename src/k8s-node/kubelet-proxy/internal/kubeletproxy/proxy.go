package kubeletproxy

import (
	"context"
	"crypto/tls"
	"fmt"
	"io"
	"log"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"strings"
	"time"
)

const execAPI = "/exec"
const portForwardAPI = "/portForward"

// Proxy reverse proxies API Server requests to the kubelet.
//
// Architecture:
//
//	API Server ──:10250──▶ kubelet-proxy ──:10251──▶ Kubelet
//
// The API Server connects to kubelet-proxy thinking it is the kubelet.
// The proxy forwards requests to the real kubelet and enforces API policy.
type Proxy struct {
	config      *Config
	kubeletURL  *url.URL
	proxy       *httputil.ReverseProxy
	server      *http.Server
	tlsConfig   *tls.Config // client TLS config for kubelet connection
	policy      *PolicyEngine
	logger      *log.Logger
	logRequests bool
}

// New creates a new kubelet proxy.
func New(ctx context.Context, cfg *Config, logger *log.Logger) (*Proxy, error) {
	if logger == nil {
		logger = log.New(os.Stdout, "[kubelet-proxy] ", log.LstdFlags|log.Lmicroseconds)
	}

	parsedKubeletURL, err := url.Parse(cfg.KubeletURL)
	if err != nil {
		return nil, fmt.Errorf("invalid kubelet URL %q: %w", cfg.KubeletURL, err)
	}

	// Load client certificate for connecting to kubelet.
	clientCert, err := tls.LoadX509KeyPair(cfg.ClientCertFile, cfg.ClientKeyFile)
	if err != nil {
		return nil, fmt.Errorf("failed to load client certificate: %w", err)
	}

	clientTLSConfig := &tls.Config{
		Certificates: []tls.Certificate{clientCert},
		// Kubelet uses a self-signed certificate.
		InsecureSkipVerify: true,
	}

	// Create the Rego policy engine for API filtering.
	policyEngine, err := NewPolicyEngine(ctx, cfg.APIPolicyFile, logger)
	if err != nil {
		return nil, fmt.Errorf("failed to create policy engine: %w", err)
	}

	transport := &Transport{
		Inner: &http.Transport{
			TLSClientConfig: clientTLSConfig,
		},
		Logger: logger,
		Policy: policyEngine,
	}

	reverseProxy := httputil.NewSingleHostReverseProxy(parsedKubeletURL)
	reverseProxy.Transport = transport

	// Build server TLS config for API Server connections.
	// TODO: In the future we may need to authenticate API server connections
	// using a CA certificate (e.g., the cluster CA) instead of accepting any
	// client. This would require adding a --ca-cert flag and setting
	// ClientCAs + RequireAndVerifyClientCert on the server TLS config.
	serverTLSConfig := &tls.Config{
		ClientAuth: tls.RequestClientCert,
	}

	p := &Proxy{
		config:      cfg,
		kubeletURL:  parsedKubeletURL,
		proxy:       reverseProxy,
		tlsConfig:   clientTLSConfig,
		policy:      policyEngine,
		logger:      logger,
		logRequests: cfg.LogRequests,
	}

	listenAddr := cfg.ListenAddr
	if listenAddr == "" {
		listenAddr = ":10250"
	}

	p.server = &http.Server{
		Addr:         listenAddr,
		TLSConfig:    serverTLSConfig,
		Handler:      p,
		ReadTimeout:  0, // No timeout for streaming requests.
		WriteTimeout: 0,
		IdleTimeout:  120 * time.Second,
	}

	return p, nil
}

// ServeHTTP handles incoming requests from the API Server.
func (p *Proxy) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	if p.logRequests {
		p.logger.Printf("Request: %s %s", r.Method, r.URL.String())
	}

	// Handle SPDY upgrade for exec/portForward if the path matches and is
	// an upgrade request. These APIs use SPDY/3.1 which httputil.ReverseProxy
	// does not support, so we handle them with raw TCP tunnelling.
	if isSPDYRequest(r) && (strings.Contains(r.URL.Path, execAPI) ||
		strings.Contains(r.URL.Path, portForwardAPI)) {
		requestURI := r.URL.RequestURI()
		allowed, err := p.policy.Evaluate(r.Context(), requestURI)
		if err != nil {
			p.logger.Printf("Policy evaluation error for %s: %v", requestURI, err)
			allowed = false
		}

		if !allowed {
			p.logger.Printf("Rejected by policy: %s", requestURI)
			http.Error(w, fmt.Sprintf("%v rejected by policy", requestURI),
				http.StatusForbidden)
			return
		}

		p.handleSPDYUpgrade(w, r)
		return
	}

	p.proxy.ServeHTTP(w, r)
}

// handleSPDYUpgrade handles SPDY connection upgrades for exec/portForward.
func (p *Proxy) handleSPDYUpgrade(w http.ResponseWriter, r *http.Request) {
	p.logger.Printf("SPDY upgrade: %s %s", r.Method, r.URL.String())

	dstConn, err := tls.Dial("tcp", p.kubeletURL.Host, p.tlsConfig)
	if err != nil {
		p.logger.Printf("Failed to connect to kubelet for SPDY: %v", err)
		http.Error(w, "Failed to connect to kubelet", http.StatusBadGateway)
		return
	}
	defer dstConn.Close() //nolint:errcheck // best-effort close

	hijacker, ok := w.(http.Hijacker)
	if !ok {
		http.Error(w, "Hijacking not supported", http.StatusInternalServerError)
		return
	}

	clientConn, _, err := hijacker.Hijack()
	if err != nil {
		http.Error(w, "Failed to hijack connection", http.StatusInternalServerError)
		return
	}
	defer clientConn.Close() //nolint:errcheck // best-effort close

	// Forward the original request to kubelet.
	if err := r.Write(dstConn); err != nil {
		p.logger.Printf("Failed to write request to kubelet: %v", err)
		return
	}

	// Bidirectional copy between client and kubelet.
	go io.Copy(dstConn, clientConn)  //nolint:errcheck // streaming copy
	io.Copy(clientConn, dstConn)     //nolint:errcheck // streaming copy
}

// isSPDYRequest checks if the request is a SPDY connection upgrade.
func isSPDYRequest(r *http.Request) bool {
	upgradeHeader := r.Header.Get("Upgrade")
	connectionHeader := r.Header.Get("Connection")
	return strings.EqualFold(upgradeHeader, "SPDY/3.1") &&
		strings.Contains(strings.ToLower(connectionHeader), "upgrade")
}

// Run starts the proxy server and blocks until the context is cancelled.
func (p *Proxy) Run(ctx context.Context) error {
	errCh := make(chan error, 1)

	go func() {
		p.logger.Printf("Starting HTTPS proxy on %s -> %s",
			p.server.Addr, p.kubeletURL.String())
		err := p.server.ListenAndServeTLS(p.config.ServerCertFile, p.config.ServerKeyFile)
		if err != nil && err != http.ErrServerClosed {
			errCh <- err
		}
		close(errCh)
	}()

	select {
	case <-ctx.Done():
		p.logger.Printf("Shutting down proxy...")
		shutdownCtx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		return p.server.Shutdown(shutdownCtx)
	case err := <-errCh:
		return err
	}
}
