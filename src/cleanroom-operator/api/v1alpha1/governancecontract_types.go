package v1alpha1

import (
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/runtime"
)

// GovernanceContractSpec defines the desired state of a
// GovernanceContract.
type GovernanceContractSpec struct {
	// ContractId is the unique identifier for this governance
	// contract within the CCF network.
	// +kubebuilder:validation:Required
	// +kubebuilder:validation:MinLength=1
	ContractId string `json:"contractId"`

	// NetworkRef is the name of the CcfNetwork CR this
	// contract is associated with. Must be in the same
	// namespace.
	// +kubebuilder:validation:Required
	NetworkRef string `json:"networkRef"`

	// MemberRef is the name of the CcfMember CR to use as
	// the operator for governance operations. Must be in
	// the same namespace and have isOperator=true.
	// +kubebuilder:validation:Required
	MemberRef string `json:"memberRef"`

	// Data is the contract configuration payload (JSON).
	// When empty, the controller synthesizes it from the
	// CcfNetwork status (endpoint, service cert discovery).
	// +optional
	Data string `json:"data,omitempty"`

	// DeploymentSpec is the ARM deployment template JSON.
	// When set, the controller proposes it after the
	// contract is accepted.
	// +optional
	// +kubebuilder:pruning:PreserveUnknownFields
	// +kubebuilder:validation:XPreserveUnknownFields
	DeploymentSpec *runtime.RawExtension `json:"deploymentSpec,omitempty"`

	// CleanRoomPolicy is the governance policy JSON.
	// When set, the controller proposes it after the
	// contract is accepted.
	// +optional
	// +kubebuilder:pruning:PreserveUnknownFields
	// +kubebuilder:validation:XPreserveUnknownFields
	CleanRoomPolicy *runtime.RawExtension `json:"cleanRoomPolicy,omitempty"`

	// AutoApprove controls whether the operator member
	// automatically votes accept on all proposals it
	// creates. Set to true for single-operator scenarios.
	// +optional
	AutoApprove bool `json:"autoApprove,omitempty"`

	// RuntimeOptions configures optional runtime features
	// for the clean room.
	// +optional
	RuntimeOptions RuntimeOptions `json:"runtimeOptions,omitempty"`

	// EnableCA enables the certificate authority for the
	// contract. When true, the controller proposes enabling
	// CA and generates a signing key.
	// +optional
	EnableCA bool `json:"enableCA,omitempty"`

	// OutputConfigMapRef is the name of the ConfigMap where
	// governance outputs (CCF endpoint, service cert,
	// contract state, etc.) are written.
	// +kubebuilder:validation:Required
	OutputConfigMapRef string `json:"outputConfigMapRef"`

	// GovernanceServiceRef is the name of the
	// GovernanceService CR whose deployment (constitution,
	// JS app, OIDC) must complete before contract creation
	// begins.
	// +kubebuilder:validation:Required
	GovernanceServiceRef string `json:"governanceServiceRef"`
}

// RuntimeOptions configures optional runtime features.
type RuntimeOptions struct {
	// EnableLogging enables logging for the clean room.
	// +optional
	EnableLogging bool `json:"enableLogging,omitempty"`

	// EnableTelemetry enables telemetry for the clean room.
	// +optional
	EnableTelemetry bool `json:"enableTelemetry,omitempty"`
}

// GovernanceContractPhase represents the lifecycle phase.
// +kubebuilder:validation:Enum=Pending;WaitingForNetwork;WaitingForGovernanceService;Configuring;Ready;Failed
type GovernanceContractPhase string

const (
	GovernanceContractPhasePending      GovernanceContractPhase = "Pending"
	GovernanceContractPhaseWaiting      GovernanceContractPhase = "WaitingForNetwork"
	GovernanceContractPhaseWaitingForGS GovernanceContractPhase = "WaitingForGovernanceService"
	GovernanceContractPhaseConfiguring  GovernanceContractPhase = "Configuring"
	GovernanceContractPhaseReady        GovernanceContractPhase = "Ready"
	GovernanceContractPhaseFailed       GovernanceContractPhase = "Failed"
)

// GovernanceContractStatus defines the observed state of a
// GovernanceContract.
type GovernanceContractStatus struct {
	// Phase is the current lifecycle phase.
	// +optional
	Phase GovernanceContractPhase `json:"phase,omitempty"`

	// ObservedGeneration is the most recent generation
	// observed by the controller.
	// +optional
	ObservedGeneration int64 `json:"observedGeneration,omitempty"`

	// ContractVersion is the CCF view.seqno version of the
	// contract, used for optimistic concurrency.
	// +optional
	ContractVersion string `json:"contractVersion,omitempty"`

	// TraceParent is the W3C traceparent header for the
	// in-flight operation.
	// +optional
	TraceParent string `json:"traceParent,omitempty"`

	// LastOperationTraceId is the trace ID of the most recent
	// operation.
	// +optional
	LastOperationTraceId string `json:"lastOperationTraceId,omitempty"`

	// CaCert is the PEM-encoded CA certificate generated
	// for this contract. Populated after the CA key is
	// generated.
	// +optional
	CaCert string `json:"caCert,omitempty"`

	// Conditions represent the latest available observations
	// of the contract's state.
	// +optional
	Conditions []metav1.Condition `json:"conditions,omitempty"`
}

// Condition types for GovernanceContract.
const (
	ConditionTypeContractCreated         = "ContractCreated"
	ConditionTypeContractAccepted        = "ContractAccepted"
	ConditionTypeDeploymentSpecAccepted  = "DeploymentSpecAccepted"
	ConditionTypeCleanRoomPolicyAccepted = "CleanRoomPolicyAccepted"
	ConditionTypeLoggingEnabled          = "LoggingEnabled"
	ConditionTypeTelemetryEnabled        = "TelemetryEnabled"
	ConditionTypeCAEnabled               = "CAEnabled"
	ConditionTypeCAKeyGenerated          = "CAKeyGenerated"
	ConditionTypeConfigMapReady          = "ConfigMapReady"
)

// +kubebuilder:object:root=true
// +kubebuilder:subresource:status
// +kubebuilder:resource:shortName=gc
// +kubebuilder:printcolumn:name="Phase",type=string,JSONPath=`.status.phase`
// +kubebuilder:printcolumn:name="Contract",type=string,JSONPath=`.spec.contractId`
// +kubebuilder:printcolumn:name="Network",type=string,JSONPath=`.spec.networkRef`
// +kubebuilder:printcolumn:name="Age",type=date,JSONPath=`.metadata.creationTimestamp`

// GovernanceContract is the Schema for the
// governancecontracts API.
type GovernanceContract struct {
	metav1.TypeMeta   `json:",inline"`
	metav1.ObjectMeta `json:"metadata,omitempty"`

	Spec   GovernanceContractSpec   `json:"spec,omitempty"`
	Status GovernanceContractStatus `json:"status,omitempty"`
}

// +kubebuilder:object:root=true

// GovernanceContractList contains a list of
// GovernanceContract.
type GovernanceContractList struct {
	metav1.TypeMeta `json:",inline"`
	metav1.ListMeta `json:"metadata,omitempty"`
	Items           []GovernanceContract `json:"items"`
}

func init() {
	SchemeBuilder.Register(
		&GovernanceContract{},
		&GovernanceContractList{},
	)
}
