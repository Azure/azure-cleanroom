package v1alpha1

import (
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/runtime"
)

// CcfNetworkSpec defines the desired state of a CcfNetwork.
type CcfNetworkSpec struct {
	// InfraType is the infrastructure type for the CCF network.
	// +kubebuilder:validation:Enum=virtual;caci
	// +kubebuilder:validation:Required
	// +kubebuilder:validation:XValidation:rule="self == oldSelf",message="infraType is immutable once set"
	InfraType string `json:"infraType"`

	// NodeCount is the number of CCF nodes in the network.
	// +kubebuilder:validation:Minimum=1
	// +kubebuilder:default=1
	NodeCount int `json:"nodeCount"`

	// Members is the list of initial consortium members.
	// +kubebuilder:validation:MinItems=1
	Members []MemberSpec `json:"members"`

	// NodeLogLevel sets the CCF node log level.
	// +kubebuilder:validation:Enum=Trace;Debug;Info;Fail;Fatal
	// +optional
	NodeLogLevel string `json:"nodeLogLevel,omitempty"`

	// ProviderConfig is opaque provider-specific configuration
	// passed through to the provider client API as-is.
	// +optional
	// +kubebuilder:pruning:PreserveUnknownFields
	// +kubebuilder:validation:XPreserveUnknownFields
	ProviderConfig *runtime.RawExtension `json:"providerConfig,omitempty"`

	// SecurityPolicyCreationOption controls security
	// policy generation for CACI deployments.
	// +kubebuilder:validation:Enum=cached;cachedDebug;allowAll
	// +optional
	SecurityPolicyCreationOption string `json:"securityPolicyCreationOption,omitempty"`

	// DeletionPolicy controls whether the external CCF network
	// is deleted or retained when this resource is deleted.
	// When empty, defaults to "retain" (safe).
	// +kubebuilder:validation:Enum=retain;delete
	// +optional
	DeletionPolicy string `json:"deletionPolicy,omitempty"`
}

// MemberSpec defines a CCF consortium member.
type MemberSpec struct {
	// Certificate is the PEM-encoded member identity certificate.
	// +kubebuilder:validation:Required
	Certificate string `json:"certificate"`

	// EncryptionPublicKey is the PEM-encoded encryption public key.
	// +optional
	EncryptionPublicKey string `json:"encryptionPublicKey,omitempty"`

	// MemberData is opaque member-specific data.
	// +optional
	// +kubebuilder:pruning:PreserveUnknownFields
	// +kubebuilder:validation:XPreserveUnknownFields
	MemberData *runtime.RawExtension `json:"memberData,omitempty"`
}

// CcfNetworkPhase represents the lifecycle phase of a CCF network.
// +kubebuilder:validation:Enum=Pending;Creating;Running;Open;Failed;Deleting
type CcfNetworkPhase string

const (
	CcfNetworkPhasePending  CcfNetworkPhase = "Pending"
	CcfNetworkPhaseCreating CcfNetworkPhase = "Creating"
	CcfNetworkPhaseRunning  CcfNetworkPhase = "Running"
	CcfNetworkPhaseOpen     CcfNetworkPhase = "Open"
	CcfNetworkPhaseFailed   CcfNetworkPhase = "Failed"
	CcfNetworkPhaseDeleting CcfNetworkPhase = "Deleting"
)

// CcfNetworkStatus defines the observed state of a CcfNetwork.
type CcfNetworkStatus struct {
	// Phase is the current lifecycle phase of the CCF network.
	// +optional
	Phase CcfNetworkPhase `json:"phase,omitempty"`

	// ObservedGeneration is the most recent generation observed
	// by the controller.
	// +optional
	ObservedGeneration int64 `json:"observedGeneration,omitempty"`

	// OperationId is the ID of the in-progress async operation.
	// +optional
	OperationId string `json:"operationId,omitempty"`

	// TraceParent is the W3C traceparent header for the in-flight
	// operation.
	// +optional
	TraceParent string `json:"traceParent,omitempty"`

	// LastOperationTraceId is the trace ID of the most recent
	// create operation.
	// +optional
	LastOperationTraceId string `json:"lastOperationTraceId,omitempty"`

	// Endpoint is the CCF network endpoint URL.
	// +optional
	Endpoint string `json:"endpoint,omitempty"`

	// Nodes is the list of CCF node identifiers.
	// +optional
	Nodes []string `json:"nodes,omitempty"`

	// ServiceCert is the PEM-encoded CCF service certificate
	// retrieved from the running network.
	// +optional
	ServiceCert string `json:"serviceCert,omitempty"`

	// Conditions represent the latest available observations of
	// the CCF network's state.
	// +optional
	Conditions []metav1.Condition `json:"conditions,omitempty"`
}

// Condition types for CcfNetwork.
const (
	ConditionTypeCcfNetworkCreated = "CcfNetworkCreated"
	ConditionTypeCcfNetworkOpen    = "CcfNetworkOpen"
	ConditionTypeTransitionToOpen  = "TransitionToOpen"
)

// +kubebuilder:object:root=true
// +kubebuilder:subresource:status
// +kubebuilder:resource:shortName=ccfnet
// +kubebuilder:printcolumn:name="Phase",type=string,JSONPath=`.status.phase`
// +kubebuilder:printcolumn:name="InfraType",type=string,JSONPath=`.spec.infraType`
// +kubebuilder:printcolumn:name="Endpoint",type=string,JSONPath=`.status.endpoint`
// +kubebuilder:printcolumn:name="Age",type=date,JSONPath=`.metadata.creationTimestamp`

// CcfNetwork is the Schema for the ccfnetworks API.
type CcfNetwork struct {
	metav1.TypeMeta   `json:",inline"`
	metav1.ObjectMeta `json:"metadata,omitempty"`

	Spec   CcfNetworkSpec   `json:"spec,omitempty"`
	Status CcfNetworkStatus `json:"status,omitempty"`
}

// +kubebuilder:object:root=true

// CcfNetworkList contains a list of CcfNetwork.
type CcfNetworkList struct {
	metav1.TypeMeta `json:",inline"`
	metav1.ListMeta `json:"metadata,omitempty"`
	Items           []CcfNetwork `json:"items"`
}

func init() {
	SchemeBuilder.Register(
		&CcfNetwork{},
		&CcfNetworkList{},
	)
}
