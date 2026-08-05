package v1alpha1

import (
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
)

// CcfUserSpec defines the desired state of a CcfUser.
type CcfUserSpec struct {
	// Identifier is the human-readable name for this user
	// (e.g. "publisher0").
	// +kubebuilder:validation:Required
	// +kubebuilder:validation:MinLength=1
	Identifier string `json:"identifier"`

	// MemberRef is the name of the CcfMember CR whose
	// governance client is used to submit proposals for
	// this user (add identity, register JWT issuer).
	// +kubebuilder:validation:Required
	MemberRef string `json:"memberRef"`

	// NetworkRef is the name of the CcfNetwork CR for
	// CCF endpoint and service cert discovery. Must be
	// in the same namespace.
	// +kubebuilder:validation:Required
	NetworkRef string `json:"networkRef"`

	// CgsImage is the container image for the user's
	// governance client. Defaults to the operator-
	// configured image.
	// +optional
	CgsImage string `json:"cgsImage,omitempty"`
}

// CcfUserPhase represents the lifecycle phase of a CcfUser.
// +kubebuilder:validation:Enum=Pending;GovernanceClientDeployed;Active;Failed
type CcfUserPhase string

const (
	CcfUserPhasePending           CcfUserPhase = "Pending"
	CcfUserPhaseGovClientDeployed CcfUserPhase = "GovernanceClientDeployed"
	CcfUserPhaseActive            CcfUserPhase = "Active"
	CcfUserPhaseFailed            CcfUserPhase = "Failed"
)

// CcfUserStatus defines the observed state of a CcfUser.
type CcfUserStatus struct {
	// Phase is the current lifecycle phase.
	// +optional
	Phase CcfUserPhase `json:"phase,omitempty"`

	// ObservedGeneration is the most recent generation
	// observed by the controller.
	// +optional
	ObservedGeneration int64 `json:"observedGeneration,omitempty"`

	// UserId is the generated UUID used as the user's
	// object-id in the CCF consortium.
	// +optional
	UserId string `json:"userId,omitempty"`

	// TenantId is the generated UUID used as the user's
	// tenant-id in the CCF consortium.
	// +optional
	TenantId string `json:"tenantId,omitempty"`

	// GovernanceClientEndpoint is the in-cluster endpoint
	// of the governance client Service for this user.
	// +optional
	GovernanceClientEndpoint string `json:"governanceClientEndpoint,omitempty"`

	// TraceParent is the W3C traceparent header for the
	// in-flight operation.
	// +optional
	TraceParent string `json:"traceParent,omitempty"`

	// LastOperationTraceId is the trace ID of the most
	// recent operation.
	// +optional
	LastOperationTraceId string `json:"lastOperationTraceId,omitempty"`

	// Conditions represent the latest available
	// observations of the user's state.
	// +optional
	Conditions []metav1.Condition `json:"conditions,omitempty"`
}

// Condition types for CcfUser.
const (
	ConditionTypeJwtIssuerRegistered = "JwtIssuerRegistered"
	ConditionTypeUserGovClientReady  = "GovernanceClientReady"
	ConditionTypeUserIdentityAdded   = "UserIdentityAdded"
)

// +kubebuilder:object:root=true
// +kubebuilder:subresource:status
// +kubebuilder:resource:shortName=ccfu
// +kubebuilder:printcolumn:name="Phase",type=string,JSONPath=`.status.phase`
// +kubebuilder:printcolumn:name="Identifier",type=string,JSONPath=`.spec.identifier`
// +kubebuilder:printcolumn:name="Member",type=string,JSONPath=`.spec.memberRef`
// +kubebuilder:printcolumn:name="Age",type=date,JSONPath=`.metadata.creationTimestamp`

// CcfUser is the Schema for the ccfusers API.
type CcfUser struct {
	metav1.TypeMeta   `json:",inline"`
	metav1.ObjectMeta `json:"metadata,omitempty"`

	Spec   CcfUserSpec   `json:"spec,omitempty"`
	Status CcfUserStatus `json:"status,omitempty"`
}

// +kubebuilder:object:root=true

// CcfUserList contains a list of CcfUser.
type CcfUserList struct {
	metav1.TypeMeta `json:",inline"`
	metav1.ListMeta `json:"metadata,omitempty"`
	Items           []CcfUser `json:"items"`
}

func init() {
	SchemeBuilder.Register(
		&CcfUser{},
		&CcfUserList{},
	)
}
