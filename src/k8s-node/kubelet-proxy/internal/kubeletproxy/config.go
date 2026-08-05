package kubeletproxy

// Config holds the configuration for the kubelet proxy.
type Config struct {
	// KubeletURL is the URL of the kubelet backend (e.g., "https://127.0.0.1:10251").
	KubeletURL string

	// ListenAddr is the address to listen on for API server connections (default ":10250").
	ListenAddr string

	// ServerCertFile is the path to the TLS certificate for serving API server requests.
	ServerCertFile string

	// ServerKeyFile is the path to the TLS key for serving API server requests.
	ServerKeyFile string

	// ClientCertFile is the path to the TLS client certificate for connecting to kubelet.
	ClientCertFile string

	// ClientKeyFile is the path to the TLS client key for connecting to kubelet.
	ClientKeyFile string

	// APIPolicyFile is the path to a JSON file listing allowed kubelet APIs.
	// If empty, the default built-in allowed API list is used.
	APIPolicyFile string

	// LogRequests enables logging of all proxied requests.
	LogRequests bool
}

// Validate checks that all required configuration fields are set.
func (c *Config) Validate() error {
	if c.KubeletURL == "" {
		return errMissing("kubelet-url")
	}
	if c.ServerCertFile == "" {
		return errMissing("server-cert")
	}
	if c.ServerKeyFile == "" {
		return errMissing("server-key")
	}
	if c.ClientCertFile == "" {
		return errMissing("client-cert")
	}
	if c.ClientKeyFile == "" {
		return errMissing("client-key")
	}
	if c.APIPolicyFile == "" {
		return errMissing("api-policy")
	}
	return nil
}

type missingFieldError struct {
	field string
}

func (e *missingFieldError) Error() string {
	return "--" + e.field + " is required"
}

func errMissing(field string) error {
	return &missingFieldError{field: field}
}
