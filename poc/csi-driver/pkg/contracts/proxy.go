// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Package contracts/proxy.go defines the canonical wire protocol between the
// CSI driver and the blobfuse-proxy host service.
//
// The same JSON message is sent by the driver over a Unix socket (DaemonSet
// mode) and read by the proxy from BLOBFUSE_MOUNTS_JSON at pod start (sidecar
// mode). Using a single shared type guarantees both sides stay in sync.
package contracts

// ProxyMountRequest is the canonical JSON mount request sent by the CSI driver
// to the blobfuse-proxy over the Unix socket, or embedded in BLOBFUSE_MOUNTS_JSON
// for proxy sidecar mode.
//
// The request supports two secret resolution paths:
//   - Pre-resolved: Env map already contains MSI_ENDPOINT and ENCRYPTION_KEY.
//     Used when the driver resolves secrets before dispatching (legacy path).
//   - Semantic: IdentityClientID / WrappedDekSecret etc. are set; the proxy
//     calls the identity and secrets sidecars to resolve them itself.
//     Preferred path — keeps the driver thin.
//
// The proxy's resolveSecrets() handles both paths: it only calls sidecars when
// the corresponding Env key is absent.
type ProxyMountRequest struct {
	// Op is the operation to perform. Must be "mount" or "unmount".
	Op string `json:"op"`

	// MountPath is the absolute path where blobfuse2 should mount the container.
	MountPath string `json:"mountPath"`

	// ContractID routes the governance API path prefix for OIDC token requests.
	// Optional; falls back to the proxy's default when empty.
	ContractID string `json:"contractId,omitempty"`

	// StagingDir overrides the staging directory derived from MountPath.
	// Used by proxy sidecar mode where the caller controls staging layout.
	StagingDir string `json:"stagingDir,omitempty"`

	// EncryptionMode is one of "CPK", "CSE", or "SSE".
	// Normalized to "CPK" when empty by the contract layer.
	EncryptionMode string `json:"encryptionMode,omitempty"`

	// Encrypted signals that blobfuse2 should load the encryptor plugin.
	// Set to true automatically when EncryptionMode is "CSE".
	Encrypted bool `json:"encrypted"`

	// ReadOnly requests a read-only FUSE mount.
	ReadOnly bool `json:"readOnly"`

	// SubDirectory mounts only this sub-path of the container.
	SubDirectory string `json:"subDirectory,omitempty"`

	// UseAdls enables Azure Data Lake Storage Gen2 semantics.
	UseAdls bool `json:"useAdls"`

	// CpkEnabled enables customer-provided key (CPK) encryption in transit.
	CpkEnabled bool `json:"cpkEnabled"`

	// DisableWriteback disables blobfuse2 write-back cache for the mount.
	DisableWriteback bool `json:"disableWriteback"`

	// BlockSizeMB sets the block cache block size in MiB. Defaults to 16.
	BlockSizeMB int `json:"blockSizeMB"`

	// TelemetryPath is the path where blobfuse2 writes telemetry data.
	TelemetryPath string `json:"telemetryPath"`

	// Env carries pre-resolved environment variables passed to blobfuse2.
	// Keys include AZURE_STORAGE_ACCOUNT, AZURE_STORAGE_ACCOUNT_CONTAINER,
	// AZURE_STORAGE_BLOB_ENDPOINT, BLOBFUSE_CACHE_PATH, and optionally
	// AZURE_STORAGE_AUTH_TYPE + MSI_ENDPOINT (if identity is pre-resolved)
	// and ENCRYPTION_KEY (if DEK is pre-resolved).
	Env map[string]string `json:"env"`

	// ─── Semantic identity fields (proxy resolves these into Env) ────────────
	//
	// Set these when the driver should not call the identity sidecar itself.
	// The proxy calls the identity sidecar (localhost:8290) and populates
	// MSI_ENDPOINT in Env before launching blobfuse2.

	// IdentityClientID is the managed identity client ID for Azure Storage auth.
	IdentityClientID string `json:"identityClientId,omitempty"`

	// IdentityTenantID is the Azure tenant for the managed identity.
	IdentityTenantID string `json:"identityTenantId,omitempty"`

	// IdentitySubject is the governance subject used to route OIDC token issuance.
	IdentitySubject string `json:"identitySubject,omitempty"`

	// ─── Semantic DEK fields (proxy resolves these into ENCRYPTION_KEY) ──────
	//
	// Set these for CSE mounts when the driver should not call the secrets
	// sidecar itself. The proxy calls the secrets sidecar (localhost:9300)
	// and populates ENCRYPTION_KEY in Env before launching blobfuse2.

	// WrappedDekAkvEndpoint is the AKV endpoint holding the wrapped DEK.
	WrappedDekAkvEndpoint string `json:"wrappedDekAkvEndpoint,omitempty"`

	// WrappedDekSecret is the AKV secret name or version of the wrapped DEK.
	WrappedDekSecret string `json:"wrappedDekSecret,omitempty"`

	// KID is the key ID of the KEK used to wrap the DEK.
	KID string `json:"kid,omitempty"`

	// AkvEndpoint is the AKV endpoint hosting the KEK.
	AkvEndpoint string `json:"akvEndpoint,omitempty"`

	// MaaEndpoint is the Microsoft Azure Attestation endpoint used for SKR.
	MaaEndpoint string `json:"maaEndpoint,omitempty"`
}

// ProxyResponse is the JSON response returned by the blobfuse-proxy to the
// CSI driver after a mount or unmount request.
type ProxyResponse struct {
	// Success is true when the operation completed without error.
	Success bool `json:"success"`

	// Error carries the error message when Success is false.
	Error string `json:"error,omitempty"`
}
