package kubeletproxy

import (
	"bytes"
	"fmt"
	"io"
	"log"
	"net/http"
)

// Transport wraps an http.RoundTripper and enforces kubelet API policy
// using a Rego-based PolicyEngine.
type Transport struct {
	Inner  http.RoundTripper
	Logger *log.Logger
	Policy *PolicyEngine
}

// RoundTrip implements http.RoundTripper with kubelet API policy enforcement.
func (t *Transport) RoundTrip(req *http.Request) (*http.Response, error) {
	t.Logger.Printf("Proxying: %s %s", req.Method, req.URL.String())

	if req.TLS != nil && len(req.TLS.PeerCertificates) > 0 {
		clientCert := req.TLS.PeerCertificates[0]
		t.Logger.Printf("Client cert subject: %s", clientCert.Subject)
	}

	requestURI := req.URL.RequestURI()
	allowed, err := t.Policy.Evaluate(req.Context(), requestURI)
	if err != nil {
		t.Logger.Printf("Policy evaluation error for %s: %v", requestURI, err)
		allowed = false
	}

	if !allowed {
		t.Logger.Printf("Rejected by policy: %s", req.URL.String())

		response := &http.Response{
			Status:     "403 Forbidden",
			StatusCode: http.StatusForbidden,
			Proto:      req.Proto,
			ProtoMajor: req.ProtoMajor,
			ProtoMinor: req.ProtoMinor,
			Request:    req,
			Header:     make(http.Header),
			Body: io.NopCloser(bytes.NewBufferString(
				fmt.Sprintf("%v rejected by policy", requestURI),
			)),
			Close: true,
		}
		response.Header.Set("Content-Type", "text/plain")
		return response, nil
	}

	t.Logger.Printf("Allowed by policy: %s", requestURI)
	return t.Inner.RoundTrip(req)
}
