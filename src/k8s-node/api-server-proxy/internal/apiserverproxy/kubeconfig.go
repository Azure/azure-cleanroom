package apiserverproxy

import (
	"encoding/base64"
	"fmt"
	"os"
	"path/filepath"

	"gopkg.in/yaml.v3"
)

// KubeConfig represents the structure of a kubeconfig file
type KubeConfig struct {
	APIVersion     string          `yaml:"apiVersion"`
	Kind           string          `yaml:"kind"`
	CurrentContext string          `yaml:"current-context"`
	Clusters       []NamedCluster  `yaml:"clusters"`
	Contexts       []NamedContext  `yaml:"contexts"`
	Users          []NamedAuthInfo `yaml:"users"`
}

// NamedCluster associates a name with a cluster
type NamedCluster struct {
	Name    string  `yaml:"name"`
	Cluster Cluster `yaml:"cluster"`
}

// Cluster contains information about a cluster
type Cluster struct {
	Server                   string `yaml:"server"`
	CertificateAuthority     string `yaml:"certificate-authority,omitempty"`
	CertificateAuthorityData string `yaml:"certificate-authority-data,omitempty"`
	InsecureSkipTLSVerify    bool   `yaml:"insecure-skip-tls-verify,omitempty"`
}

// NamedContext associates a name with a context
type NamedContext struct {
	Name    string  `yaml:"name"`
	Context Context `yaml:"context"`
}

// Context contains information about a context
type Context struct {
	Cluster   string `yaml:"cluster"`
	User      string `yaml:"user"`
	Namespace string `yaml:"namespace,omitempty"`
}

// NamedAuthInfo associates a name with user authentication info
type NamedAuthInfo struct {
	Name string   `yaml:"name"`
	User AuthInfo `yaml:"user"`
}

// AuthInfo contains information about user credentials
type AuthInfo struct {
	ClientCertificate     string      `yaml:"client-certificate,omitempty"`
	ClientCertificateData string      `yaml:"client-certificate-data,omitempty"`
	ClientKey             string      `yaml:"client-key,omitempty"`
	ClientKeyData         string      `yaml:"client-key-data,omitempty"`
	Token                 string      `yaml:"token,omitempty"`
	TokenFile             string      `yaml:"tokenFile,omitempty"`
	Username              string      `yaml:"username,omitempty"`
	Password              string      `yaml:"password,omitempty"`
	Exec                  *ExecConfig `yaml:"exec,omitempty"`
}

// ExecConfig specifies a command to provide client credentials
type ExecConfig struct {
	APIVersion         string       `yaml:"apiVersion"`
	Command            string       `yaml:"command"`
	Args               []string     `yaml:"args,omitempty"`
	Env                []ExecEnvVar `yaml:"env,omitempty"`
	ProvideClusterInfo bool         `yaml:"provideClusterInfo,omitempty"`
	InstallHint        string       `yaml:"installHint,omitempty"`
}

// ExecEnvVar contains environment variable to set when executing the command
type ExecEnvVar struct {
	Name  string `yaml:"name"`
	Value string `yaml:"value"`
}

// LoadedKubeConfig contains the resolved configuration from a kubeconfig file
type LoadedKubeConfig struct {
	// Server is the API server URL
	Server string

	// CertificateAuthorityData is the CA cert data (PEM encoded)
	CertificateAuthorityData []byte

	// ClientCertificateData is the client cert data (PEM encoded)
	ClientCertificateData []byte

	// ClientKeyData is the client key data (PEM encoded)
	ClientKeyData []byte

	// BearerToken for token-based authentication (static token)
	BearerToken string

	// ExecConfig for exec-based credential provider (dynamic token)
	ExecConfig *ExecConfig

	// InsecureSkipTLSVerify skips TLS verification
	InsecureSkipTLSVerify bool
}

// LoadKubeConfig loads and parses a kubeconfig file
func LoadKubeConfig(kubeconfigPath string, contextName string) (*LoadedKubeConfig, error) {
	data, err := os.ReadFile(kubeconfigPath)
	if err != nil {
		return nil, fmt.Errorf("failed to read kubeconfig: %w", err)
	}

	var kubeconfig KubeConfig
	if err := yaml.Unmarshal(data, &kubeconfig); err != nil {
		return nil, fmt.Errorf("failed to parse kubeconfig: %w", err)
	}

	// Determine which context to use
	if contextName == "" {
		contextName = kubeconfig.CurrentContext
	}
	if contextName == "" {
		return nil, fmt.Errorf("no context specified and no current-context in kubeconfig")
	}

	// Find the context
	var context *Context
	for _, c := range kubeconfig.Contexts {
		if c.Name == contextName {
			context = &c.Context
			break
		}
	}
	if context == nil {
		return nil, fmt.Errorf("context %q not found in kubeconfig", contextName)
	}

	// Find the cluster
	var cluster *Cluster
	for _, c := range kubeconfig.Clusters {
		if c.Name == context.Cluster {
			cluster = &c.Cluster
			break
		}
	}
	if cluster == nil {
		return nil, fmt.Errorf("cluster %q not found in kubeconfig", context.Cluster)
	}

	// Find the user
	var user *AuthInfo
	for _, u := range kubeconfig.Users {
		if u.Name == context.User {
			user = &u.User
			break
		}
	}
	if user == nil {
		return nil, fmt.Errorf("user %q not found in kubeconfig", context.User)
	}

	// Build the loaded config
	loaded := &LoadedKubeConfig{
		Server:                cluster.Server,
		InsecureSkipTLSVerify: cluster.InsecureSkipTLSVerify,
	}

	kubeconfigDir := filepath.Dir(kubeconfigPath)

	// Load CA data
	if cluster.CertificateAuthorityData != "" {
		caData, err := base64.StdEncoding.DecodeString(cluster.CertificateAuthorityData)
		if err != nil {
			return nil, fmt.Errorf("failed to decode certificate-authority-data: %w", err)
		}
		loaded.CertificateAuthorityData = caData
	} else if cluster.CertificateAuthority != "" {
		caPath := resolvePath(cluster.CertificateAuthority, kubeconfigDir)
		caData, err := os.ReadFile(caPath)
		if err != nil {
			return nil, fmt.Errorf("failed to read certificate-authority file: %w", err)
		}
		loaded.CertificateAuthorityData = caData
	}

	// Load client certificate data
	if user.ClientCertificateData != "" {
		certData, err := base64.StdEncoding.DecodeString(user.ClientCertificateData)
		if err != nil {
			return nil, fmt.Errorf("failed to decode client-certificate-data: %w", err)
		}
		loaded.ClientCertificateData = certData
	} else if user.ClientCertificate != "" {
		certPath := resolvePath(user.ClientCertificate, kubeconfigDir)
		certData, err := os.ReadFile(certPath)
		if err != nil {
			return nil, fmt.Errorf("failed to read client-certificate file: %w", err)
		}
		loaded.ClientCertificateData = certData
	}

	// Load client key data
	if user.ClientKeyData != "" {
		keyData, err := base64.StdEncoding.DecodeString(user.ClientKeyData)
		if err != nil {
			return nil, fmt.Errorf("failed to decode client-key-data: %w", err)
		}
		loaded.ClientKeyData = keyData
	} else if user.ClientKey != "" {
		keyPath := resolvePath(user.ClientKey, kubeconfigDir)
		keyData, err := os.ReadFile(keyPath)
		if err != nil {
			return nil, fmt.Errorf("failed to read client-key file: %w", err)
		}
		loaded.ClientKeyData = keyData
	}

	// Load bearer token
	if user.Token != "" {
		loaded.BearerToken = user.Token
	} else if user.TokenFile != "" {
		tokenPath := resolvePath(user.TokenFile, kubeconfigDir)
		tokenData, err := os.ReadFile(tokenPath)
		if err != nil {
			return nil, fmt.Errorf("failed to read token file: %w", err)
		}
		loaded.BearerToken = string(tokenData)
	}

	// Copy exec config for exec-based credential provider
	if user.Exec != nil {
		loaded.ExecConfig = user.Exec
	}

	return loaded, nil
}

// resolvePath resolves a path relative to a base directory if it's not absolute
func resolvePath(path, baseDir string) string {
	if filepath.IsAbs(path) {
		return path
	}
	return filepath.Join(baseDir, path)
}
