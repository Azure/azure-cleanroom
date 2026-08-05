package v1alpha1

import (
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/runtime"
)

// EnvironmentSpec defines the desired state of an Environment.
type EnvironmentSpec struct {
	// InfraType is the infrastructure type for all child
	// resources (CCF network, cluster).
	// +kubebuilder:validation:Enum=virtual;aks
	// +kubebuilder:validation:Required
	// +kubebuilder:validation:XValidation:rule="self == oldSelf",message="infraType is immutable once set"
	InfraType string `json:"infraType"`

	// ContractId is the governance contract identifier.
	// Defaults to the Environment name if not specified.
	// +optional
	ContractId string `json:"contractId,omitempty"`

	// AutoApprove controls whether the operator member
	// automatically votes accept on all governance
	// proposals. Set to true for single-operator scenarios.
	// +optional
	AutoApprove bool `json:"autoApprove,omitempty"`

	// EnableCA enables the certificate authority for the
	// governance contract.
	// +optional
	EnableCA bool `json:"enableCA,omitempty"`

	// CcfNetwork overrides CCF network settings.
	// +optional
	CcfNetwork *EnvironmentCcfNetworkSpec `json:"ccfNetwork,omitempty"`

	// Member configures the operator member.
	// +optional
	Member *EnvironmentMemberSpec `json:"member,omitempty"`

	// DeploymentSpec is the ARM deployment template JSON
	// for the governance contract.
	// +optional
	// +kubebuilder:pruning:PreserveUnknownFields
	// +kubebuilder:validation:XPreserveUnknownFields
	DeploymentSpec *runtime.RawExtension `json:"deploymentSpec,omitempty"`

	// CleanRoomPolicy is the governance policy JSON.
	// +optional
	// +kubebuilder:pruning:PreserveUnknownFields
	// +kubebuilder:validation:XPreserveUnknownFields
	CleanRoomPolicy *runtime.RawExtension `json:"cleanRoomPolicy,omitempty"`

	// RuntimeOptions configures optional runtime features.
	// +optional
	RuntimeOptions RuntimeOptions `json:"runtimeOptions,omitempty"`

	// Profiles configures workload and infrastructure
	// profiles for the cluster.
	// +optional
	Profiles *EnvironmentProfiles `json:"profiles,omitempty"`

	// ClusterProviderConfig is opaque provider-specific
	// configuration for the Cluster.
	// +optional
	// +kubebuilder:pruning:PreserveUnknownFields
	// +kubebuilder:validation:XPreserveUnknownFields
	ClusterProviderConfig *runtime.RawExtension `json:"clusterProviderConfig,omitempty"`

	// InitialMember optionally overrides the non-operator
	// member (member0) settings. A second member and its
	// GovernanceService are always created; this field
	// allows customising the member identifier or CGS image.
	// +optional
	InitialMember *EnvironmentInitialMemberSpec `json:"initialMember,omitempty"`

	// GovernanceService optionally overrides the governance
	// service deployment settings (constitution image, JS
	// app image, tenant ID). When omitted the controller
	// resolves images from CGS_CONSTITUTION_IMAGE and
	// CGS_JS_APP_IMAGE environment variables.
	// +optional
	GovernanceService *EnvironmentGovernanceServiceSpec `json:"governanceService,omitempty"`

	// DeletionPolicy controls whether external infrastructure
	// (CCF network, cluster) is deleted or retained when the
	// Environment is deleted. Defaults to "delete" for virtual
	// infraType and "retain" otherwise.
	// +kubebuilder:validation:Enum=retain;delete
	// +optional
	DeletionPolicy string `json:"deletionPolicy,omitempty"`

	// PrereqsConfigRef is the name of a ConfigMap created by
	// the prepare-prereqs command. It contains provider
	// configuration for both the CCF network and the cluster.
	// When set, the controller reads ccfProviderConfig and
	// clusterProviderConfig keys from the ConfigMap as
	// fallbacks for the inline CcfNetwork.ProviderConfig and
	// ClusterProviderConfig fields.
	// +optional
	PrereqsConfigRef string `json:"prereqsConfigRef,omitempty"`
}

// EnvironmentCcfNetworkSpec overrides CCF network defaults.
type EnvironmentCcfNetworkSpec struct {
	// NodeCount is the number of CCF nodes.
	// +kubebuilder:validation:Minimum=1
	// +kubebuilder:default=1
	// +optional
	NodeCount *int `json:"nodeCount,omitempty"`

	// NodeLogLevel sets the CCF node log level.
	// +kubebuilder:validation:Enum=Trace;Debug;Info;Fail;Fatal
	// +optional
	NodeLogLevel string `json:"nodeLogLevel,omitempty"`

	// ProviderConfig is opaque provider-specific
	// configuration for the CCF network.
	// +optional
	// +kubebuilder:pruning:PreserveUnknownFields
	// +kubebuilder:validation:XPreserveUnknownFields
	ProviderConfig *runtime.RawExtension `json:"providerConfig,omitempty"`

	// SecurityPolicyCreationOption controls security
	// policy generation for CCF CACI deployments.
	// +kubebuilder:validation:Enum=cached;cachedDebug;allowAll
	// +optional
	SecurityPolicyCreationOption string `json:"securityPolicyCreationOption,omitempty"`
}

// EnvironmentMemberSpec configures the operator member.
type EnvironmentMemberSpec struct {
	// Identifier is the human-readable name for the
	// operator member. Defaults to "operator".
	// +optional
	Identifier string `json:"identifier,omitempty"`

	// GenerateEncryptionKey controls whether an RSA
	// encryption key pair is generated.
	// +optional
	GenerateEncryptionKey bool `json:"generateEncryptionKey,omitempty"`

	// CgsImage overrides the governance client image.
	// +optional
	CgsImage string `json:"cgsImage,omitempty"`
}

// EnvironmentInitialMemberSpec configures the non-operator
// member (member0) used for governance service deployment.
type EnvironmentInitialMemberSpec struct {
	// Identifier is the human-readable name for this
	// member. Defaults to "member0".
	// +optional
	Identifier string `json:"identifier,omitempty"`

	// CgsImage overrides the governance client image.
	// +optional
	CgsImage string `json:"cgsImage,omitempty"`
}

// EnvironmentGovernanceServiceSpec configures the governance
// service deployment parameters.
type EnvironmentGovernanceServiceSpec struct {
	// ConstitutionImage is the OCI image for the
	// constitution bundle. Resolved from env var
	// CGS_CONSTITUTION_IMAGE if not set.
	// +optional
	ConstitutionImage string `json:"constitutionImage,omitempty"`

	// JsAppImage is the OCI image for the JS app bundle.
	// Resolved from env var CGS_JS_APP_IMAGE if not set.
	// +optional
	JsAppImage string `json:"jsAppImage,omitempty"`

	// TenantId is the Azure AD tenant ID for JWT issuer
	// configuration.
	// +optional
	TenantId string `json:"tenantId,omitempty"`

	// OidcContainerName is the container name used in the
	// shared OIDC storage account for this environment's
	// OpenID configuration documents. Must be unique
	// across all developers/CI runs. Typically set by the
	// CLI using the resource group name.
	// +optional
	OidcContainerName string `json:"oidcContainerName,omitempty"`
}

// EnvironmentProfiles configures workload and infra profiles.
type EnvironmentProfiles struct {
	// Observability configures observability (metrics, logs,
	// traces).
	// +optional
	Observability *ObservabilityProfileSpec `json:"observability,omitempty"`

	// Monitoring configures monitoring capabilities.
	// +optional
	Monitoring *MonitoringProfileSpec `json:"monitoring,omitempty"`

	// Analytics configures the analytics workload.
	// +optional
	Analytics *AnalyticsWorkloadProfileSpec `json:"analytics,omitempty"`

	// Inferencing configures the inferencing workload.
	// +optional
	Inferencing *InferencingWorkloadProfileSpec `json:"inferencing,omitempty"`

	// FlexNode configures flex node provisioning.
	// +optional
	FlexNode *FlexNodeProfileSpec `json:"flexNode,omitempty"`

	// Aad configures Azure AD integration.
	// +optional
	Aad *AadProfileSpec `json:"aad,omitempty"`
}

// DeletionPolicy controls whether external infrastructure
// is deleted or retained on resource deletion.
const (
	DeletionPolicyRetain = "retain"
	DeletionPolicyDelete = "delete"
)

// EnvironmentPhase represents the lifecycle phase.
// +kubebuilder:validation:Enum=Pending;Provisioning;Ready;Failed;Deleting
type EnvironmentPhase string

const (
	EnvironmentPhasePending      EnvironmentPhase = "Pending"
	EnvironmentPhaseProvisioning EnvironmentPhase = "Provisioning"
	EnvironmentPhaseReady        EnvironmentPhase = "Ready"
	EnvironmentPhaseFailed       EnvironmentPhase = "Failed"
	EnvironmentPhaseDeleting     EnvironmentPhase = "Deleting"
)

// EnvironmentStatus defines the observed state of an
// Environment.
type EnvironmentStatus struct {
	// Phase is the current lifecycle phase.
	// +optional
	Phase EnvironmentPhase `json:"phase,omitempty"`

	// Message is a human-readable summary of why the
	// environment is in its current phase.
	// +optional
	Message string `json:"message,omitempty"`

	// ObservedGeneration is the most recent generation
	// observed by the controller.
	// +optional
	ObservedGeneration int64 `json:"observedGeneration,omitempty"`

	// LastHandledReconcileAt is the timestamp of the last
	// reconciliation request processed by the controller.
	// Set by the controller when it begins handling a
	// reconcile.cleanroom.azure.com/requestedAt annotation.
	// +optional
	LastHandledReconcileAt string `json:"lastHandledReconcileAt,omitempty"`

	// TraceParent is the W3C traceparent header for the
	// in-flight operation. Used internally for cross-reconcile
	// span correlation. Cleared when the operation completes.
	// +optional
	TraceParent string `json:"traceParent,omitempty"`

	// LastOperationTraceId is the trace ID of the most recent
	// create/update operation. Persists after completion for
	// debugging.
	// +optional
	LastOperationTraceId string `json:"lastOperationTraceId,omitempty"`

	// Conditions represent the latest available observations
	// of the environment's state.
	// +optional
	Conditions []metav1.Condition `json:"conditions,omitempty"`
}

// Condition types for Environment.
const (
	ConditionTypeCcfMemberReady          = "CcfMemberReady"
	ConditionTypeCcfNetworkReady         = "CcfNetworkReady"
	ConditionTypeGovernanceServiceReady  = "GovernanceServiceReady"
	ConditionTypeGovernanceContractReady = "GovernanceContractReady"
	ConditionTypeWorkloadGovernanceReady = "WorkloadGovernanceReady"
	ConditionTypeClusterRunning          = "ClusterRunning"
	ConditionTypeClusterReady            = "ClusterReady"
	ConditionTypeEnvironmentReady        = "EnvironmentReady"
	ConditionTypeInitialMemberReady      = "InitialMemberReady"
)

// +kubebuilder:object:root=true
// +kubebuilder:subresource:status
// +kubebuilder:resource:shortName=env
// +kubebuilder:printcolumn:name="Phase",type=string,JSONPath=`.status.phase`
// +kubebuilder:printcolumn:name="InfraType",type=string,JSONPath=`.spec.infraType`
// +kubebuilder:printcolumn:name="Age",type=date,JSONPath=`.metadata.creationTimestamp`

// Environment is the Schema for the environments API.
// It is the top-level resource that creates and manages
// CcfMember, CcfNetwork, GovernanceContract, and Cluster
// child resources.
type Environment struct {
	metav1.TypeMeta   `json:",inline"`
	metav1.ObjectMeta `json:"metadata,omitempty"`

	Spec   EnvironmentSpec   `json:"spec,omitempty"`
	Status EnvironmentStatus `json:"status,omitempty"`
}

// +kubebuilder:object:root=true

// EnvironmentList contains a list of Environment.
type EnvironmentList struct {
	metav1.TypeMeta `json:",inline"`
	metav1.ListMeta `json:"metadata,omitempty"`
	Items           []Environment `json:"items"`
}

func init() {
	SchemeBuilder.Register(
		&Environment{},
		&EnvironmentList{},
	)
}
