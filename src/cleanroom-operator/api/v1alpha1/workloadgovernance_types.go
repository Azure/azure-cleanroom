package v1alpha1

import (
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/runtime"
)

// WorkloadGovernanceSpec defines the desired state of a
// WorkloadGovernance resource.
type WorkloadGovernanceSpec struct {
	// WorkloadType identifies the workload profile this
	// governance handles.
	// +kubebuilder:validation:Enum="kserve-inferencing"
	// +kubebuilder:validation:Required
	WorkloadType string `json:"workloadType"`

	// ContractId is the unique contract identifier within
	// the CCF network.
	// +kubebuilder:validation:Required
	// +kubebuilder:validation:MinLength=1
	ContractId string `json:"contractId"`

	// NetworkRef is the name of the CcfNetwork CR.
	// +kubebuilder:validation:Required
	NetworkRef string `json:"networkRef"`

	// MemberRef is the name of the CcfMember CR to use as
	// the operator for governance operations.
	// +kubebuilder:validation:Required
	MemberRef string `json:"memberRef"`

	// GovernanceServiceRef is the name of the
	// GovernanceService CR whose deployment must complete
	// before governance operations begin.
	// +kubebuilder:validation:Required
	GovernanceServiceRef string `json:"governanceServiceRef"`

	// AutoApprove controls whether proposals are
	// automatically voted accept.
	// +optional
	AutoApprove bool `json:"autoApprove,omitempty"`

	// EnableCA enables the certificate authority for the
	// contract.
	// +optional
	EnableCA bool `json:"enableCA,omitempty"`

	// RuntimeOptions configures optional runtime features.
	// +optional
	RuntimeOptions RuntimeOptions `json:"runtimeOptions,omitempty"`

	// InfraType is the infrastructure type passed to the
	// cluster provider for deployment generation.
	// +kubebuilder:validation:Required
	InfraType string `json:"infraType"`

	// SecurityPolicyCreationOption controls security policy
	// generation for deployment generation.
	// +optional
	SecurityPolicyCreationOption string `json:"securityPolicyCreationOption,omitempty"`

	// ProviderConfig is opaque provider-specific config
	// passed to the cluster provider.
	// +optional
	// +kubebuilder:pruning:PreserveUnknownFields
	// +kubebuilder:validation:XPreserveUnknownFields
	ProviderConfig *runtime.RawExtension `json:"providerConfig,omitempty"`

	// OutputConfigMapRef is the name of the ConfigMap where
	// governance outputs are written.
	// +kubebuilder:validation:Required
	OutputConfigMapRef string `json:"outputConfigMapRef"`
}

// WorkloadGovernancePhase represents the lifecycle phase.
// +kubebuilder:validation:Enum=Pending;WaitingForContract;Configuring;Ready;Failed
type WorkloadGovernancePhase string

const (
	WorkloadGovernancePhasePending            WorkloadGovernancePhase = "Pending"
	WorkloadGovernancePhaseWaitingForContract WorkloadGovernancePhase = "WaitingForContract"
	WorkloadGovernancePhaseConfiguring        WorkloadGovernancePhase = "Configuring"
	WorkloadGovernancePhaseReady              WorkloadGovernancePhase = "Ready"
	WorkloadGovernancePhaseFailed             WorkloadGovernancePhase = "Failed"
)

// WorkloadGovernanceStatus defines the observed state of a
// WorkloadGovernance resource.
type WorkloadGovernanceStatus struct {
	// Phase is the current lifecycle phase.
	// +optional
	Phase WorkloadGovernancePhase `json:"phase,omitempty"`

	// ObservedGeneration is the most recent generation
	// observed by the controller.
	// +optional
	ObservedGeneration int64 `json:"observedGeneration,omitempty"`

	// TraceParent is the W3C traceparent header for the
	// in-flight operation.
	// +optional
	TraceParent string `json:"traceParent,omitempty"`

	// LastOperationTraceId is the trace ID of the most recent
	// operation.
	// +optional
	LastOperationTraceId string `json:"lastOperationTraceId,omitempty"`

	// Conditions represent the latest available observations
	// of the resource's state.
	// +optional
	Conditions []metav1.Condition `json:"conditions,omitempty"`
}

// Condition types for WorkloadGovernance.
const (
	ConditionTypeWGContractCreated = "GovernanceContractCreated"
	ConditionTypeWGContractReady   = "GovernanceContractReady"
	ConditionTypeSigningEnabled    = "SigningEnabled"
	ConditionTypeSigningKeyGen     = "SigningKeyGenerated"
	ConditionTypeDeploymentGen     = "DeploymentGenerated"
	ConditionTypeWGDeploySpecOK    = "DeploymentSpecAccepted"
	ConditionTypeWGPolicyOK        = "CleanRoomPolicyAccepted"
	ConditionTypeWGConfigMapReady  = "ConfigMapUpdated"
)

// +kubebuilder:object:root=true
// +kubebuilder:subresource:status
// +kubebuilder:resource:shortName=wg
// +kubebuilder:printcolumn:name="Phase",type=string,JSONPath=`.status.phase`
// +kubebuilder:printcolumn:name="Workload",type=string,JSONPath=`.spec.workloadType`
// +kubebuilder:printcolumn:name="Contract",type=string,JSONPath=`.spec.contractId`
// +kubebuilder:printcolumn:name="Age",type=date,JSONPath=`.metadata.creationTimestamp`

// WorkloadGovernance is the Schema for the
// workloadgovernances API.
type WorkloadGovernance struct {
	metav1.TypeMeta   `json:",inline"`
	metav1.ObjectMeta `json:"metadata,omitempty"`

	Spec   WorkloadGovernanceSpec   `json:"spec,omitempty"`
	Status WorkloadGovernanceStatus `json:"status,omitempty"`
}

// +kubebuilder:object:root=true

// WorkloadGovernanceList contains a list of
// WorkloadGovernance.
type WorkloadGovernanceList struct {
	metav1.TypeMeta `json:",inline"`
	metav1.ListMeta `json:"metadata,omitempty"`
	Items           []WorkloadGovernance `json:"items"`
}

func init() {
	SchemeBuilder.Register(
		&WorkloadGovernance{},
		&WorkloadGovernanceList{},
	)
}
