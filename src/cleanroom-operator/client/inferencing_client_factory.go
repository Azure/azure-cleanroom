// Package client provides HTTP clients for cleanroom
// provider APIs.
package client

import (
	"context"
	"crypto/tls"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"strings"
	"time"

	"go.opentelemetry.io/contrib/instrumentation/net/http/otelhttp"
	"k8s.io/client-go/tools/clientcmd"
	"k8s.io/client-go/transport"
)

// InferencingClientFactory creates InferencingClient
// instances that can reach the inferencing agent in a
// workload cluster. In cross-cluster setups (e.g., Kind
// mgmt → Kind/AKS workload), requests are routed
// through the workload cluster's API server service
// proxy. In same-cluster setups (future), a direct
// transport is used.
type InferencingClientFactory struct {
	clusterClient *ClusterClient
}

// NewInferencingClientFactory creates a factory that
// uses the given ClusterClient to retrieve workload
// cluster kubeconfigs.
func NewInferencingClientFactory(
	clusterClient *ClusterClient,
) *InferencingClientFactory {
	return &InferencingClientFactory{
		clusterClient: clusterClient,
	}
}

// ClientForCluster returns an InferencingClient that
// reaches the inferencing agent in the specified
// workload cluster via the K8s API server service proxy.
//
// The endpoint is the in-cluster service URL stored in
// the Cluster CR status, e.g.:
//
//	https://kserve-inferencing-agent.kserve-inferencing-agent.svc
//
// This is parsed to extract the service name, namespace,
// and scheme for constructing the API server proxy path.
func (f *InferencingClientFactory) ClientForCluster(
	ctx context.Context,
	clusterName string,
	infraType string,
	providerConfig interface{},
	endpoint string,
) (*InferencingClient, error) {
	// Parse the in-cluster endpoint to extract service
	// name, namespace, and scheme.
	svcName, svcNamespace, svcScheme, err :=
		parseServiceEndpoint(endpoint)
	if err != nil {
		return nil, fmt.Errorf(
			"parsing service endpoint %q: %w",
			endpoint, err,
		)
	}

	// Get kubeconfig for the workload cluster from
	// the cluster provider.
	var pcRaw json.RawMessage
	if providerConfig != nil {
		pcRaw, _ = json.Marshal(providerConfig)
	}
	kubeconfigBytes, err :=
		f.clusterClient.GetKubeconfig(
			ctx,
			clusterName,
			&GetKubeconfigInput{
				InfraType:      infraType,
				ProviderConfig: pcRaw,
				Internal:       true,
			},
		)
	if err != nil {
		return nil, fmt.Errorf(
			"getting kubeconfig for cluster %s: %w",
			clusterName, err,
		)
	}

	// Parse kubeconfig into a REST config.
	restConfig, err :=
		clientcmd.RESTConfigFromKubeConfig(
			kubeconfigBytes,
		)
	if err != nil {
		return nil, fmt.Errorf(
			"parsing kubeconfig for cluster %s: %w",
			clusterName, err,
		)
	}

	// Build TLS config from the REST config's
	// CA cert and client credentials.
	transportConfig, err :=
		restConfig.TransportConfig()
	if err != nil {
		return nil, fmt.Errorf(
			"building transport config: %w", err,
		)
	}

	tlsConfig, err :=
		transport.TLSConfigFor(transportConfig)
	if err != nil {
		return nil, fmt.Errorf(
			"building TLS config: %w", err,
		)
	}
	if tlsConfig == nil {
		tlsConfig = &tls.Config{
			MinVersion: tls.VersionTLS12,
		}
	}

	// Build an http.Client with the workload cluster's
	// TLS credentials.
	httpTransport := &http.Transport{
		TLSClientConfig: tlsConfig,
	}

	// Wrap with bearer token if present.
	var rt http.RoundTripper = httpTransport
	if restConfig.BearerToken != "" {
		rt = &bearerTokenTransport{
			token: restConfig.BearerToken,
			inner: rt,
		}
	}

	httpClient := &http.Client{
		Timeout:   2 * time.Minute,
		Transport: otelhttp.NewTransport(rt),
	}

	// Build the API server proxy base URL:
	// <apiServer>/api/v1/namespaces/<ns>/services/
	//   <scheme>:<svc>:<port>/proxy
	apiServer := strings.TrimRight(
		restConfig.Host, "/",
	)
	proxyBaseURL := fmt.Sprintf(
		"%s/api/v1/namespaces/%s/services/"+
			"%s:%s:443/proxy",
		apiServer,
		svcNamespace,
		svcScheme,
		svcName,
	)

	return NewInferencingClient(
		proxyBaseURL, httpClient,
	), nil
}

// parseServiceEndpoint extracts the service name,
// namespace, and scheme from a K8s in-cluster service
// URL like:
//
//	https://svc-name.svc-namespace.svc
//
// For external endpoints (e.g. AKS LoadBalancer FQDN),
// falls back to the well-known inferencing agent
// service name/namespace.
// Returns (name, namespace, scheme, error).
func parseServiceEndpoint(
	endpoint string,
) (string, string, string, error) {
	u, err := url.Parse(endpoint)
	if err != nil {
		return "", "", "", fmt.Errorf(
			"parsing URL: %w", err,
		)
	}

	scheme := u.Scheme
	if scheme == "" {
		scheme = "https"
	}

	// Host is "svc-name.svc-namespace.svc" or
	// "svc-name.svc-namespace.svc.cluster.local"
	// or an external FQDN for AKS LoadBalancer
	// endpoints.
	host := u.Hostname()
	parts := strings.SplitN(host, ".", 3)

	// For in-cluster URLs (contain ".svc"), extract
	// the service name and namespace from the host.
	if len(parts) >= 2 &&
		(len(parts) < 3 ||
			strings.HasPrefix(parts[2], "svc")) {
		return parts[0], parts[1], scheme, nil
	}

	// External endpoint (e.g. AKS LoadBalancer
	// FQDN) — fall back to the well-known
	// inferencing agent service name/namespace.
	const agentSvc = "kserve-inferencing-agent"
	return agentSvc, agentSvc, scheme, nil
}

// bearerTokenTransport injects an Authorization header
// into every request.
type bearerTokenTransport struct {
	token string
	inner http.RoundTripper
}

func (t *bearerTokenTransport) RoundTrip(
	req *http.Request,
) (*http.Response, error) {
	req = req.Clone(req.Context())
	req.Header.Set(
		"Authorization", "Bearer "+t.token,
	)
	return t.inner.RoundTrip(req)
}
