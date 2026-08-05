package v1alpha1

import (
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/runtime"
)

// ClusterSpec defines the desired state of a Cluster.
// +kubebuilder:validation:XValidation:rule="self.infraType != 'aks' || has(self.providerConfig)",message="providerConfig is required when infraType is aks"
type ClusterSpec struct {
	// InfraType is the infrastructure type for the cluster.
	// +kubebuilder:validation:Enum=virtual;aks
	// +kubebuilder:validation:Required
	// +kubebuilder:validation:XValidation:rule="self == oldSelf",message="infraType is immutable once set"
	InfraType string `json:"infraType"`

	// ObservabilityProfile configures observability (metrics, logs, traces).
	// +optional
	ObservabilityProfile *ObservabilityProfileSpec `json:"observabilityProfile,omitempty"`

	// MonitoringProfile configures monitoring capabilities.
	// +optional
	MonitoringProfile *MonitoringProfileSpec `json:"monitoringProfile,omitempty"`

	// AnalyticsWorkloadProfile configures the analytics workload.
	// +optional
	AnalyticsWorkloadProfile *AnalyticsWorkloadProfileSpec `json:"analyticsWorkloadProfile,omitempty"`

	// InferencingWorkloadProfile configures the inferencing workload.
	// +optional
	InferencingWorkloadProfile *InferencingWorkloadProfileSpec `json:"inferencingWorkloadProfile,omitempty"`

	// FlexNodeProfile configures flex node provisioning.
	// +optional
	FlexNodeProfile *FlexNodeProfileSpec `json:"flexNodeProfile,omitempty"`

	// AadProfile configures Azure Active Directory integration.
	// +optional
	AadProfile *AadProfileSpec `json:"aadProfile,omitempty"`

	// ProviderConfig is opaque provider-specific configuration passed
	// through to the provider client API as-is.
	// +optional
	// +kubebuilder:pruning:PreserveUnknownFields
	// +kubebuilder:validation:XPreserveUnknownFields
	ProviderConfig *runtime.RawExtension `json:"providerConfig,omitempty"`

	// DeletionPolicy controls whether the external cluster
	// is deleted or retained when this resource is deleted.
	// When empty, defaults to "retain" (safe).
	// +kubebuilder:validation:Enum=retain;delete
	// +optional
	DeletionPolicy string `json:"deletionPolicy,omitempty"`
}

// ObservabilityProfileSpec defines observability settings.
type ObservabilityProfileSpec struct {
	// Enabled controls whether observability is turned on.
	Enabled bool `json:"enabled"`
}

// MonitoringProfileSpec defines monitoring settings.
type MonitoringProfileSpec struct {
	// Enabled controls whether monitoring is turned on.
	Enabled bool `json:"enabled"`
}

// AnalyticsWorkloadProfileSpec defines analytics workload settings.
type AnalyticsWorkloadProfileSpec struct {
	// Enabled controls whether the analytics workload is enabled.
	Enabled bool `json:"enabled"`

	// ConfigurationUrl is the URL for analytics workload configuration.
	// Required when enabled is true.
	// +optional
	ConfigurationUrl string `json:"configurationUrl,omitempty"`

	// ConfigurationUrlCaCert is a PEM-encoded CA certificate for the
	// configuration URL.
	// +optional
	ConfigurationUrlCaCert string `json:"configurationUrlCaCert,omitempty"`

	// SecurityPolicyCreationOption controls security policy generation.
	// +kubebuilder:validation:Enum=cached;cachedDebug;allowAll
	// +optional
	SecurityPolicyCreationOption string `json:"securityPolicyCreationOption,omitempty"`

	// PoolProfile configures the workload pool.
	// +optional
	PoolProfile *WorkloadPoolProfileSpec `json:"poolProfile,omitempty"`
}

// WorkloadPoolProfileSpec defines pool sizing.
type WorkloadPoolProfileSpec struct {
	// NodeCount is the number of nodes in the pool.
	// +kubebuilder:validation:Minimum=1
	NodeCount int `json:"nodeCount"`
}

// InferencingWorkloadProfileSpec defines inferencing workload settings.
type InferencingWorkloadProfileSpec struct {
	// KServeProfile configures KServe-based inferencing.
	// +optional
	KServeProfile *KServeProfileSpec `json:"kserveProfile,omitempty"`
}

// KServeProfileSpec defines KServe inferencing settings.
type KServeProfileSpec struct {
	// Enabled controls whether KServe inferencing is enabled.
	Enabled bool `json:"enabled"`

	// ConfigurationUrl is the URL for inferencing configuration.
	// Required when enabled is true and governanceConfigRef is
	// not set.
	// +optional
	ConfigurationUrl string `json:"configurationUrl,omitempty"`

	// ConfigurationUrlCaCert is a PEM-encoded CA certificate for the
	// configuration URL.
	// +optional
	ConfigurationUrlCaCert string `json:"configurationUrlCaCert,omitempty"`

	// SecurityPolicyCreationOption controls security policy generation.
	// +kubebuilder:validation:Enum=cached;cachedDebug;allowAll
	// +optional
	SecurityPolicyCreationOption string `json:"securityPolicyCreationOption,omitempty"`

	// GovernanceConfigRef is the name of a ConfigMap written by
	// a WorkloadGovernance controller. When set, the Cluster
	// waits for this ConfigMap before provisioning the KServe
	// profile and reads configurationUrl from it.
	// +optional
	GovernanceConfigRef string `json:"governanceConfigRef,omitempty"`
}

// FlexNodeMode controls how flex nodes are provisioned.
// +kubebuilder:validation:Enum=manual;auto
type FlexNodeMode string

const (
	// FlexNodeModeManual provisions a fixed number of flex
	// nodes directly (the default / legacy behaviour).
	FlexNodeModeManual FlexNodeMode = "manual"

	// FlexNodeModeAuto lets Karpenter manage flex node
	// lifecycle via the accr CloudProvider.
	FlexNodeModeAuto FlexNodeMode = "auto"
)

// FlexNodeProfileSpec defines flex node settings.
type FlexNodeProfileSpec struct {
	// Enabled controls whether flex nodes are provisioned.
	Enabled bool `json:"enabled"`

	// Mode controls how flex nodes are provisioned.
	// "manual" provisions a fixed NodeCount of nodes
	// directly. "auto" delegates lifecycle to the
	// Karpenter autoscaler via the accr CloudProvider.
	// When empty, defaults to "manual" behaviour.
	// +optional
	Mode FlexNodeMode `json:"mode,omitempty"`

	// PolicySigningCertPem is the PEM-encoded policy signing certificate.
	// Mutually exclusive with PolicySigningCertSecretRef.
	// +optional
	PolicySigningCertPem string `json:"policySigningCertPem,omitempty"`

	// PolicySigningCertSecretRef references a Kubernetes Secret containing
	// the policy signing certificate. Mutually exclusive with
	// PolicySigningCertPem.
	// +optional
	PolicySigningCertSecretRef *SecretKeyRef `json:"policySigningCertSecretRef,omitempty"`

	// GovernanceConfigRef is the name of a ConfigMap written by
	// a WorkloadGovernance controller. When set, the Cluster
	// waits for this ConfigMap before provisioning flex nodes
	// and reads policySigningCertPem from it.
	// +optional
	GovernanceConfigRef string `json:"governanceConfigRef,omitempty"`

	// NodeCount is the number of flex nodes to provision.
	// +kubebuilder:validation:Minimum=1
	// +kubebuilder:default=1
	// +optional
	NodeCount *int `json:"nodeCount,omitempty"`

	// VmSize is the Azure VM size for flex nodes.
	// +optional
	VmSize string `json:"vmSize,omitempty"`

	// OsDiskSizeInGB is the OS disk size in GB.
	// +optional
	OsDiskSizeInGB *int `json:"osDiskSizeInGB,omitempty"`

	// MaxPodsPerNode is the maximum number of pods per flex node.
	// +kubebuilder:default=110
	// +optional
	MaxPodsPerNode *int `json:"maxPodsPerNode,omitempty"`

	// Insecure disables security checks on flex nodes.
	// When nil, defaults to true.
	// +optional
	Insecure *bool `json:"insecure,omitempty"`

	// ProvisionUsingSSH controls how flex node VMs are provisioned on
	// AKS. When true, a stock Ubuntu CVM image is created and the node
	// software is installed via SSH at deploy time (no pre-baked gallery
	// image / cleanroom-image-digests artifact is required). When false,
	// a pre-built gallery image is used. When nil, defaults to true for
	// the operator flow. Ignored for virtual (Kind) clusters.
	// +optional
	ProvisionUsingSSH *bool `json:"provisionUsingSSH,omitempty"`

	// SshPrivateKeyPem is the PEM-encoded SSH private key for node access.
	// +optional
	SshPrivateKeyPem string `json:"sshPrivateKeyPem,omitempty"`

	// SshPublicKey is the SSH public key for node access.
	// +optional
	SshPublicKey string `json:"sshPublicKey,omitempty"`

	// SshPrivateKeySecretRef references a Secret containing the SSH
	// private key. Mutually exclusive with SshPrivateKeyPem.
	// +optional
	SshPrivateKeySecretRef *SecretKeyRef `json:"sshPrivateKeySecretRef,omitempty"`

	// SshPublicKeySecretRef references a Secret containing the SSH
	// public key. Mutually exclusive with SshPublicKey.
	// +optional
	SshPublicKeySecretRef *SecretKeyRef `json:"sshPublicKeySecretRef,omitempty"`

	// RequirePreProvisionedKindNodes requires that flex worker nodes are
	// pre-provisioned in the Kind cluster config. When true, the flow fails
	// if the requested nodes are not already present. Defaults to false
	// (kindscaler dynamically adds nodes).
	// +optional
	RequirePreProvisionedKindNodes *bool `json:"requirePreProvisionedKindNodes,omitempty"`
}

// SecretKeyRef is a reference to a key in a Kubernetes Secret.
type SecretKeyRef struct {
	// Name is the name of the Secret.
	Name string `json:"name"`
	// Key is the key within the Secret data.
	Key string `json:"key"`
}

// AadProfileSpec defines Azure AD integration settings.
type AadProfileSpec struct {
	// Enabled controls whether AAD integration is enabled during
	// cluster creation.
	// +optional
	Enabled bool `json:"enabled,omitempty"`

	// AdminGroupObjectIds is a list of AAD group object IDs that are
	// granted admin access.
	AdminGroupObjectIds []string `json:"adminGroupObjectIds"`
}

// ClusterPhase represents the lifecycle phase of a cluster.
// +kubebuilder:validation:Enum=Pending;Creating;Running;Failed;Deleting;Updating
type ClusterPhase string

const (
	PhasePending  ClusterPhase = "Pending"
	PhaseCreating ClusterPhase = "Creating"
	PhaseRunning  ClusterPhase = "Running"
	PhaseFailed   ClusterPhase = "Failed"
	PhaseDeleting ClusterPhase = "Deleting"
	PhaseUpdating ClusterPhase = "Updating"
)

// ClusterStatus defines the observed state of a Cluster.
type ClusterStatus struct {
	// Phase is the current lifecycle phase of the cluster.
	// +optional
	Phase ClusterPhase `json:"phase,omitempty"`

	// ObservedGeneration is the most recent generation observed by the
	// controller.
	// +optional
	ObservedGeneration int64 `json:"observedGeneration,omitempty"`

	// OperationId is the ID of the in-progress async operation, if any.
	// +optional
	OperationId string `json:"operationId,omitempty"`

	// TraceParent is the W3C traceparent header for the in-flight
	// operation. Used internally for cross-reconcile span correlation.
	// Cleared when the operation completes or fails.
	// +optional
	TraceParent string `json:"traceParent,omitempty"`

	// LastOperationTraceId is the trace ID of the most recent
	// create/update operation. Persists after completion for debugging.
	// +optional
	LastOperationTraceId string `json:"lastOperationTraceId,omitempty"`

	// ObservabilityProfile reflects the observed observability state.
	// +optional
	ObservabilityProfile *ObservabilityProfileStatus `json:"observabilityProfile,omitempty"`

	// MonitoringProfile reflects the observed monitoring state.
	// +optional
	MonitoringProfile *MonitoringProfileStatus `json:"monitoringProfile,omitempty"`

	// AnalyticsWorkloadProfile reflects the observed analytics state.
	// +optional
	AnalyticsWorkloadProfile *AnalyticsWorkloadProfileStatus `json:"analyticsWorkloadProfile,omitempty"`

	// InferencingWorkloadProfile reflects the observed inferencing state.
	// +optional
	InferencingWorkloadProfile *InferencingProfileStatus `json:"inferencingWorkloadProfile,omitempty"`

	// FlexNodeProfile reflects the observed flex node state.
	// +optional
	FlexNodeProfile *FlexNodeProfileStatus `json:"flexNodeProfile,omitempty"`

	// ProviderProperties contains provider-specific output properties.
	// +optional
	// +kubebuilder:pruning:PreserveUnknownFields
	// +kubebuilder:validation:XPreserveUnknownFields
	ProviderProperties *runtime.RawExtension `json:"providerProperties,omitempty"`

	// LastHandledReconcileAt is the timestamp of the last
	// reconcile request handled by the controller.
	// +optional
	LastHandledReconcileAt string `json:"lastHandledReconcileAt,omitempty"`

	// Conditions represent the latest available observations of the
	// cluster's state.
	// +optional
	Conditions []metav1.Condition `json:"conditions,omitempty"`
}

// ObservabilityProfileStatus reflects observed observability state.
type ObservabilityProfileStatus struct {
	Enabled               bool   `json:"enabled"`
	MetricsEndpoint       string `json:"metricsEndpoint,omitempty"`
	LogsEndpoint          string `json:"logsEndpoint,omitempty"`
	TracesEndpoint        string `json:"tracesEndpoint,omitempty"`
	VisualizationEndpoint string `json:"visualizationEndpoint,omitempty"`
}

// MonitoringProfileStatus reflects observed monitoring state.
type MonitoringProfileStatus struct {
	Enabled bool `json:"enabled"`
}

// AnalyticsWorkloadProfileStatus reflects observed analytics state.
type AnalyticsWorkloadProfileStatus struct {
	Enabled   bool   `json:"enabled"`
	Namespace string `json:"namespace,omitempty"`
	Endpoint  string `json:"endpoint,omitempty"`
}

// InferencingProfileStatus reflects observed inferencing state.
type InferencingProfileStatus struct {
	// KServeProfile reflects the KServe inferencing status.
	// +optional
	KServeProfile *KServeInferencingProfileStatus `json:"kserveProfile,omitempty"`
}

// KServeInferencingProfileStatus reflects observed KServe state.
type KServeInferencingProfileStatus struct {
	Enabled   bool   `json:"enabled"`
	Namespace string `json:"namespace,omitempty"`
	Endpoint  string `json:"endpoint,omitempty"`
}

// FlexNodeProfileStatus reflects observed flex node state.
type FlexNodeProfileStatus struct {
	Enabled bool             `json:"enabled"`
	Nodes   []FlexNodeStatus `json:"nodes,omitempty"`
}

// FlexNodeStatus represents the status of a single flex node.
type FlexNodeStatus struct {
	Name   string `json:"name,omitempty"`
	Status string `json:"status,omitempty"`
}

// Condition types for Cluster.
const (
	ConditionTypeValidated             = "Validated"
	ConditionTypeClusterCreated        = "ClusterCreated"
	ConditionTypeReady                 = "Ready"
	ConditionTypeGovernanceReady       = "GovernanceReady"
	ConditionTypeWorkloadProfilesReady = "WorkloadProfilesReady"
)

// +kubebuilder:object:root=true
// +kubebuilder:subresource:status
// +kubebuilder:resource:shortName=crc
// +kubebuilder:printcolumn:name="Phase",type=string,JSONPath=`.status.phase`
// +kubebuilder:printcolumn:name="InfraType",type=string,JSONPath=`.spec.infraType`
// +kubebuilder:printcolumn:name="Age",type=date,JSONPath=`.metadata.creationTimestamp`

// Cluster is the Schema for the clusters API.
type Cluster struct {
	metav1.TypeMeta   `json:",inline"`
	metav1.ObjectMeta `json:"metadata,omitempty"`

	Spec   ClusterSpec   `json:"spec,omitempty"`
	Status ClusterStatus `json:"status,omitempty"`
}

// +kubebuilder:object:root=true

// ClusterList contains a list of Cluster.
type ClusterList struct {
	metav1.TypeMeta `json:",inline"`
	metav1.ListMeta `json:"metadata,omitempty"`
	Items           []Cluster `json:"items"`
}

func init() {
	SchemeBuilder.Register(
		&Cluster{},
		&ClusterList{},
	)
}
