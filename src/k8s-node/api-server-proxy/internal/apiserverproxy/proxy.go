package apiserverproxy

import (
	"bufio"
	"bytes"
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"strings"
	"sync"
	"time"

	"github.com/azure/azure-cleanroom/src/k8s-node/api-server-proxy/internal/apiserverproxy/admission"
)

// Proxy intercepts traffic between kubelet and API server.
// It sits between the kubelet and the Kubernetes API server, intercepting
// pod watch/list responses and filtering out pods that should not run on this node.
//
// Architecture:
//
//	Kubelet <---> api-server-proxy <---> API Server
//
// The kubelet connects to api-server-proxy thinking it's the API server.
// The proxy forwards requests to the real API server and filters responses.
type Proxy struct {
	config              *Config
	apiServerURL        *url.URL
	reverseProxy        *httputil.ReverseProxy
	admissionController admission.Controller
	transport           *http.Transport
	logger              *log.Logger
	tokenProvider       *TokenProvider

	// Track rejected pods
	rejectedPods   map[string]*admission.Decision // key: namespace/name
	rejectedPodsMu sync.RWMutex
}

// New creates a new api-server proxy
func New(cfg *Config, admissionController admission.Controller) (*Proxy, error) {
	if cfg.LoadedKubeConfig == nil {
		return nil, fmt.Errorf("kubeconfig not loaded")
	}

	apiServerURL, err := url.Parse(cfg.LoadedKubeConfig.Server)
	if err != nil {
		return nil, fmt.Errorf("invalid API server URL from kubeconfig: %w", err)
	}

	logger := log.New(os.Stdout, "[api-server-proxy] ", log.LstdFlags|log.Lmicroseconds)

	// Create token provider for API server authentication
	tokenProvider := NewTokenProvider(cfg.LoadedKubeConfig, logger)

	p := &Proxy{
		config:              cfg,
		apiServerURL:        apiServerURL,
		admissionController: admissionController,
		logger:              logger,
		rejectedPods:        make(map[string]*admission.Decision),
		tokenProvider:       tokenProvider,
	}

	// Configure TLS for API server connection
	tlsConfig, err := p.buildAPIServerTLSConfig()
	if err != nil {
		return nil, fmt.Errorf("failed to build TLS config: %w", err)
	}

	// Create transport for API server connections
	p.transport = &http.Transport{
		TLSClientConfig: tlsConfig,
		MaxIdleConns:    100,
		IdleConnTimeout: 90 * time.Second,
	}

	// Create reverse proxy for non-pod requests
	p.reverseProxy = &httputil.ReverseProxy{
		Director:     p.director,
		Transport:    p.transport,
		ErrorHandler: p.errorHandler,
	}

	return p, nil
}

// buildAPIServerTLSConfig builds TLS configuration for connecting to API server
func (p *Proxy) buildAPIServerTLSConfig() (*tls.Config, error) {
	kubeConfig := p.config.LoadedKubeConfig

	tlsConfig := &tls.Config{
		InsecureSkipVerify: kubeConfig.InsecureSkipTLSVerify,
	}

	// Load CA certificate from kubeconfig
	if len(kubeConfig.CertificateAuthorityData) > 0 {
		caCertPool := x509.NewCertPool()
		if !caCertPool.AppendCertsFromPEM(kubeConfig.CertificateAuthorityData) {
			return nil, fmt.Errorf("failed to parse CA certificate from kubeconfig")
		}
		tlsConfig.RootCAs = caCertPool
	}

	// Load client certificate from kubeconfig
	if len(kubeConfig.ClientCertificateData) > 0 && len(kubeConfig.ClientKeyData) > 0 {
		cert, err := tls.X509KeyPair(kubeConfig.ClientCertificateData, kubeConfig.ClientKeyData)
		if err != nil {
			return nil, fmt.Errorf("failed to load client certificate from kubeconfig: %w", err)
		}
		tlsConfig.Certificates = []tls.Certificate{cert}
	}

	return tlsConfig, nil
}

// director modifies the request before forwarding to API server
func (p *Proxy) director(req *http.Request) {
	req.URL.Scheme = p.apiServerURL.Scheme
	req.URL.Host = p.apiServerURL.Host
	req.Host = p.apiServerURL.Host

	// Add bearer token authentication if available
	if token, err := p.tokenProvider.GetToken(); err != nil {
		p.logger.Printf("Warning: failed to get token: %v", err)
	} else if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}

	// Force JSON responses instead of protobuf
	// Kubelet requests protobuf by default, but we need JSON to parse and filter pods
	req.Header.Set("Accept", "application/json")

	if _, ok := req.Header["User-Agent"]; !ok {
		req.Header.Set("User-Agent", "api-server-proxy/1.0")
	}
}

// errorHandler handles proxy errors
func (p *Proxy) errorHandler(w http.ResponseWriter, r *http.Request, err error) {
	p.logger.Printf("Proxy error: %s %s: %v", r.Method, r.URL.Path, err)
	http.Error(w, fmt.Sprintf("Proxy error: %v", err), http.StatusBadGateway)
}

// ServeHTTP handles incoming requests from the kubelet
func (p *Proxy) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	if p.config.LogRequests {
		p.logger.Printf("Request: %s %s", r.Method, r.URL.String())
	}

	// Check if this is a pod-related request that needs interception
	if p.isPodRequest(r) {
		p.handlePodRequest(w, r)
		return
	}

	// Forward all other requests directly
	p.reverseProxy.ServeHTTP(w, r)
}

// isPodRequest checks if the request is for pods
func (p *Proxy) isPodRequest(r *http.Request) bool {
	path := r.URL.Path
	// Match: /api/v1/pods, /api/v1/namespaces/{ns}/pods, /api/v1/watch/pods, etc.
	return strings.Contains(path, "/pods")
}

// isWatchRequest checks if this is a watch request
func (p *Proxy) isWatchRequest(r *http.Request) bool {
	if r.URL.Query().Get("watch") == "true" {
		return true
	}
	return strings.Contains(r.URL.Path, "/watch/")
}

// handlePodRequest intercepts pod list/watch requests and filters responses
func (p *Proxy) handlePodRequest(w http.ResponseWriter, r *http.Request) {
	if p.isWatchRequest(r) {
		p.handlePodWatch(w, r)
		return
	}

	// For non-watch requests (LIST, GET), modify the response body
	p.handlePodListOrGet(w, r)
}

// handlePodListOrGet handles pod list/get requests
func (p *Proxy) handlePodListOrGet(w http.ResponseWriter, r *http.Request) {
	// Create the upstream request
	upstreamReq := p.createUpstreamRequest(r)

	resp, err := p.transport.RoundTrip(upstreamReq)
	if err != nil {
		p.logger.Printf("Error forwarding request: %v", err)
		http.Error(w, fmt.Sprintf("Error: %v", err), http.StatusBadGateway)
		return
	}
	defer resp.Body.Close()

	// Read the response body
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		p.logger.Printf("Error reading response: %v", err)
		http.Error(w, fmt.Sprintf("Error: %v", err), http.StatusBadGateway)
		return
	}

	// Only filter successful responses
	if resp.StatusCode == http.StatusOK {
		body = p.filterPodResponse(body)
	}

	// Copy headers (except Content-Length which may have changed)
	for k, vv := range resp.Header {
		if k == "Content-Length" {
			continue
		}
		for _, v := range vv {
			w.Header().Add(k, v)
		}
	}
	w.Header().Set("Content-Length", fmt.Sprintf("%d", len(body)))
	w.WriteHeader(resp.StatusCode)
	w.Write(body)
}

// handlePodWatch handles pod watch requests with streaming response
func (p *Proxy) handlePodWatch(w http.ResponseWriter, r *http.Request) {
	p.logger.Printf("Starting pod watch stream")

	// Create the upstream request
	upstreamReq := p.createUpstreamRequest(r)

	resp, err := p.transport.RoundTrip(upstreamReq)
	if err != nil {
		p.logger.Printf("Error forwarding watch request: %v", err)
		http.Error(w, fmt.Sprintf("Error: %v", err), http.StatusBadGateway)
		return
	}
	defer resp.Body.Close()

	// Copy headers
	for k, vv := range resp.Header {
		for _, v := range vv {
			w.Header().Add(k, v)
		}
	}
	w.WriteHeader(resp.StatusCode)

	// For non-OK responses, just copy the body
	if resp.StatusCode != http.StatusOK {
		io.Copy(w, resp.Body)
		return
	}

	// Enable streaming
	flusher, ok := w.(http.Flusher)
	if !ok {
		p.logger.Printf("Warning: ResponseWriter doesn't support Flusher, streaming may not work")
		io.Copy(w, resp.Body)
		return
	}

	// Process the watch stream line by line
	// Each line is a JSON watch event: {"type": "ADDED/MODIFIED/DELETED", "object": {...}}
	reader := bufio.NewReader(resp.Body)
	for {
		line, err := reader.ReadBytes('\n')
		if err != nil {
			if err != io.EOF {
				p.logger.Printf("Watch stream error: %v", err)
			}
			return
		}

		// Process the watch event
		filteredLine := p.filterWatchEvent(line)
		if filteredLine != nil {
			_, writeErr := w.Write(filteredLine)
			if writeErr != nil {
				p.logger.Printf("Error writing to client: %v", writeErr)
				return
			}
			flusher.Flush()
		}
	}
}

// createUpstreamRequest creates a request to forward to the API server
func (p *Proxy) createUpstreamRequest(r *http.Request) *http.Request {
	upstreamURL := *r.URL
	upstreamURL.Scheme = p.apiServerURL.Scheme
	upstreamURL.Host = p.apiServerURL.Host

	upstreamReq, _ := http.NewRequest(r.Method, upstreamURL.String(), r.Body)
	upstreamReq.Header = r.Header.Clone()
	upstreamReq.Host = p.apiServerURL.Host

	// Force JSON responses instead of protobuf
	// Kubelet requests protobuf by default, but we need JSON to parse and filter pods
	upstreamReq.Header.Set("Accept", "application/json")

	// Add bearer token if available
	if token, err := p.tokenProvider.GetToken(); err != nil {
		p.logger.Printf("Warning: failed to get token: %v", err)
	} else if token != "" {
		upstreamReq.Header.Set("Authorization", "Bearer "+token)
	}

	return upstreamReq
}

// filterPodResponse filters pods from a list or single pod response
func (p *Proxy) filterPodResponse(body []byte) []byte {
	var response map[string]interface{}
	if err := json.Unmarshal(body, &response); err != nil {
		p.logger.Printf("Error parsing pod response: %v", err)
		return body
	}

	kind, _ := response["kind"].(string)

	switch kind {
	case "PodList":
		items, ok := response["items"].([]interface{})
		if ok {
			filtered := p.filterPodItems(items)
			response["items"] = filtered
			newBody, err := json.Marshal(response)
			if err != nil {
				p.logger.Printf("Error marshaling filtered response: %v", err)
				return body
			}
			return newBody
		}

	case "Pod":
		// Check if pod is already in terminal state
		phase := getPodPhase(response)
		if phase != "Succeeded" && phase != "Failed" {
			decision := p.evaluatePodAdmission(response)
			if !decision.Allowed {
				namespace, name := getPodIdentifier(response)
				podKey := fmt.Sprintf("%s/%s", namespace, name)
				p.logger.Printf("REJECTED (get): Pod %s - %s", podKey, decision.Reason)

				// Use the same rejection pattern as list/watch
				go p.rejectPodViaStatus(namespace, name, decision.Reason)
			}
		}
	}

	// Always return the pod - the status patch handles rejection
	return body
}

// filterPodItems filters a list of pod items, rejecting disallowed pods via status patch
func (p *Proxy) filterPodItems(items []interface{}) []interface{} {
	var filtered []interface{}

	for _, item := range items {
		pod, ok := item.(map[string]interface{})
		if !ok {
			filtered = append(filtered, item)
			continue
		}

		namespace, name := getPodIdentifier(pod)
		podKey := fmt.Sprintf("%s/%s", namespace, name)

		// Check if pod is already in a terminal state
		phase := getPodPhase(pod)
		if phase == "Succeeded" || phase == "Failed" {
			filtered = append(filtered, pod)
			continue
		}

		// Check if already rejected
		p.rejectedPodsMu.RLock()
		_, alreadyRejected := p.rejectedPods[podKey]
		p.rejectedPodsMu.RUnlock()

		if alreadyRejected {
			// Don't include in list, already being rejected
			continue
		}

		// Evaluate admission
		decision := p.evaluatePodAdmission(pod)
		if !decision.Allowed {
			p.logger.Printf("REJECTED (list): Pod %s - %s", podKey, decision.Reason)

			// Track the rejection
			p.rejectedPodsMu.Lock()
			p.rejectedPods[podKey] = decision
			p.rejectedPodsMu.Unlock()

			// Report failure via status patch
			go p.rejectPodViaStatus(namespace, name, decision.Reason)

			// Don't include in the list
			continue
		}

		filtered = append(filtered, pod)
	}

	return filtered
}

// filterWatchEvent filters a single watch event from the stream
// For rejected pods, it reports failure via status patch (Kubernetes-native rejection)
func (p *Proxy) filterWatchEvent(line []byte) []byte {
	line = bytes.TrimSpace(line)
	if len(line) == 0 {
		return nil
	}

	var event map[string]interface{}
	if err := json.Unmarshal(line, &event); err != nil {
		p.logger.Printf("Error parsing watch event: %v", err)
		return append(line, '\n')
	}

	eventType, _ := event["type"].(string)
	object, ok := event["object"].(map[string]interface{})
	if !ok {
		return append(line, '\n')
	}

	// Check if this is a Pod object
	kind, _ := object["kind"].(string)
	if kind != "Pod" {
		return append(line, '\n')
	}

	namespace, name := getPodIdentifier(object)
	podKey := fmt.Sprintf("%s/%s", namespace, name)

	switch eventType {
	case "ADDED", "MODIFIED":
		// Check if pod is already in a terminal state - don't re-evaluate
		phase := getPodPhase(object)
		if phase == "Succeeded" || phase == "Failed" {
			// Clean up tracking if it was previously rejected
			p.rejectedPodsMu.Lock()
			delete(p.rejectedPods, podKey)
			p.rejectedPodsMu.Unlock()
			return append(line, '\n')
		}

		// Check if we already rejected this pod (avoid re-patching)
		p.rejectedPodsMu.RLock()
		_, alreadyRejected := p.rejectedPods[podKey]
		p.rejectedPodsMu.RUnlock()

		if alreadyRejected {
			// Already rejected, don't pass to kubelet
			return nil
		}

		// Evaluate admission for new or modified pods
		decision := p.evaluatePodAdmission(object)
		if !decision.Allowed {
			p.logger.Printf("REJECTED: Pod %s - %s", podKey, decision.Reason)

			// Track the rejection
			p.rejectedPodsMu.Lock()
			p.rejectedPods[podKey] = decision
			p.rejectedPodsMu.Unlock()

			// Report failure to API server via status patch (Kubernetes-native rejection)
			go p.rejectPodViaStatus(namespace, name, decision.Reason)

			// Don't pass this event to kubelet
			return nil
		}

		if p.config.LogRequests {
			p.logger.Printf("ALLOWED: Pod %s", podKey)
		}

	case "DELETED":
		// Clean up tracking for deleted pods
		p.rejectedPodsMu.Lock()
		delete(p.rejectedPods, podKey)
		p.rejectedPodsMu.Unlock()
	}

	// Pass through the event
	return append(line, '\n')
}

// evaluatePodAdmission checks if a pod should be admitted to run on this node
func (p *Proxy) evaluatePodAdmission(pod map[string]interface{}) *admission.Decision {
	namespace, name := getPodIdentifier(pod)

	req := &admission.Request{
		Namespace: namespace,
		Name:      name,
		Pod:       pod,
	}

	return p.admissionController.Admit(req)
}

// rejectPodViaStatus rejects a pod by patching its status to Failed
// This is the Kubernetes-native way to reject a pod - the same way kubelet reports failures
func (p *Proxy) rejectPodViaStatus(namespace, name, reason string) {
	p.logger.Printf("Patching pod %s/%s status to Failed: %s", namespace, name, reason)

	// Build the status patch
	statusPatch := map[string]interface{}{
		"status": map[string]interface{}{
			"phase":   "Failed",
			"reason":  "NodeAdmissionRejected",
			"message": fmt.Sprintf("Pod rejected by api-server-proxy: %s", reason),
			"conditions": []map[string]interface{}{
				{
					"type":               "Ready",
					"status":             "False",
					"reason":             "NodeAdmissionRejected",
					"message":            reason,
					"lastTransitionTime": time.Now().UTC().Format(time.RFC3339),
				},
				{
					"type":               "ContainersReady",
					"status":             "False",
					"reason":             "NodeAdmissionRejected",
					"message":            reason,
					"lastTransitionTime": time.Now().UTC().Format(time.RFC3339),
				},
				{
					"type":               "PodScheduled",
					"status":             "True",
					"reason":             "NodeAdmissionRejected",
					"message":            "Pod was scheduled but rejected by node admission policy",
					"lastTransitionTime": time.Now().UTC().Format(time.RFC3339),
				},
			},
		},
	}

	patchBody, err := json.Marshal(statusPatch)
	if err != nil {
		p.logger.Printf("Error marshaling status patch: %v", err)
		return
	}

	// Build the PATCH request URL
	patchURL := fmt.Sprintf("%s/api/v1/namespaces/%s/pods/%s/status",
		p.apiServerURL.String(), namespace, name)

	req, err := http.NewRequest(http.MethodPatch, patchURL, bytes.NewReader(patchBody))
	if err != nil {
		p.logger.Printf("Error creating PATCH request: %v", err)
		return
	}

	// Use strategic merge patch
	req.Header.Set("Content-Type", "application/strategic-merge-patch+json")

	// Add bearer token authentication (required for API server access)
	if token, err := p.tokenProvider.GetToken(); err != nil {
		p.logger.Printf("Warning: failed to get token for status patch: %v", err)
	} else if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}

	// Create a client with the same transport (includes TLS config)
	client := &http.Client{
		Transport: p.transport,
		Timeout:   10 * time.Second,
	}

	resp, err := client.Do(req)
	if err != nil {
		p.logger.Printf("Error patching pod status: %v", err)
		return
	}
	defer resp.Body.Close()

	if resp.StatusCode >= 200 && resp.StatusCode < 300 {
		p.logger.Printf("Successfully rejected pod %s/%s via status patch", namespace, name)
	} else {
		body, _ := io.ReadAll(resp.Body)
		p.logger.Printf("Failed to patch pod status (HTTP %d): %s", resp.StatusCode, string(body))
	}
}

// Helper functions

func getPodIdentifier(pod map[string]interface{}) (namespace, name string) {
	namespace = "default"
	name = "unknown"

	if metadata, ok := pod["metadata"].(map[string]interface{}); ok {
		if ns, ok := metadata["namespace"].(string); ok {
			namespace = ns
		}
		if n, ok := metadata["name"].(string); ok {
			name = n
		} else if gn, ok := metadata["generateName"].(string); ok {
			name = gn + "<generated>"
		}
	}
	return
}

func getPodPhase(pod map[string]interface{}) string {
	if status, ok := pod["status"].(map[string]interface{}); ok {
		if phase, ok := status["phase"].(string); ok {
			return phase
		}
	}
	return ""
}

// Run starts the proxy server
func (p *Proxy) Run(ctx context.Context) error {
	server := &http.Server{
		Addr:         p.config.ListenAddr,
		Handler:      p,
		ReadTimeout:  0, // No timeout for watch streams
		WriteTimeout: 0, // No timeout for watch streams
		IdleTimeout:  120 * time.Second,
	}

	errCh := make(chan error, 1)

	go func() {
		var err error
		if p.config.TLSCertFile != "" && p.config.TLSKeyFile != "" {
			p.logger.Printf("Starting HTTPS proxy on %s -> %s", p.config.ListenAddr, p.apiServerURL.String())
			err = server.ListenAndServeTLS(p.config.TLSCertFile, p.config.TLSKeyFile)
		} else {
			p.logger.Printf("Starting HTTP proxy on %s -> %s (no TLS)", p.config.ListenAddr, p.apiServerURL.String())
			err = server.ListenAndServe()
		}
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
		return server.Shutdown(shutdownCtx)
	case err := <-errCh:
		return err
	}
}

// GetRejectedPods returns a copy of currently rejected pods (for debugging/monitoring)
func (p *Proxy) GetRejectedPods() map[string]*admission.Decision {
	p.rejectedPodsMu.RLock()
	defer p.rejectedPodsMu.RUnlock()

	result := make(map[string]*admission.Decision)
	for k, v := range p.rejectedPods {
		result[k] = v
	}
	return result
}
