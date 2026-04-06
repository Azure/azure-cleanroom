package apiserverproxy

// Config holds the configuration for the api-server proxy
type Config struct {
	// ListenAddr is the address to listen on for kubelet connections
	ListenAddr string

	// KubeconfigPath is the path to the kubeconfig file for API server connection
	KubeconfigPath string

	// KubeconfigContext is the context to use from the kubeconfig (optional, uses current-context if empty)
	KubeconfigContext string

	// TLS configuration for serving (the proxy's own TLS, not API server connection)
	TLSCertFile string
	TLSKeyFile  string

	// Logging options
	LogRequests    bool
	LogPodPayloads bool

	// PolicyVerificationCert is the path to the public key certificate
	// used to verify pod policy signatures. If set, pods must have a valid
	// signature in the annotation "api-server-proxy.io/signature"
	PolicyVerificationCert string

	// LoadedKubeConfig contains the parsed kubeconfig data (populated after loading)
	LoadedKubeConfig *LoadedKubeConfig
}

// LoadKubeconfigFromFile loads the kubeconfig file specified in KubeconfigPath
func (c *Config) LoadKubeconfigFromFile() error {
	if c.KubeconfigPath == "" {
		return nil
	}

	loaded, err := LoadKubeConfig(c.KubeconfigPath, c.KubeconfigContext)
	if err != nil {
		return err
	}

	c.LoadedKubeConfig = loaded
	return nil
}
