package v1alpha1

import (
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
)

// CcfMemberSpec defines the desired state of a CcfMember.
type CcfMemberSpec struct {
	// Identifier is the human-readable name for this member
	// (e.g. "operator", "member0").
	// +kubebuilder:validation:Required
	// +kubebuilder:validation:MinLength=1
	Identifier string `json:"identifier"`

	// IsOperator indicates whether this member has operator
	// privileges in the CCF consortium.
	// +optional
	IsOperator bool `json:"isOperator,omitempty"`

	// GenerateEncryptionKey controls whether an RSA encryption
	// key pair is generated for this member in addition to the
	// identity certificate.
	// +optional
	GenerateEncryptionKey bool `json:"generateEncryptionKey,omitempty"`

	// SecretRef is the name of the Secret where generated certs
	// and keys are stored. Created by the controller if it does
	// not exist.
	// +kubebuilder:validation:Required
	SecretRef SecretRef `json:"secretRef"`

	// CgsImage is the container image for the governance client.
	// Defaults to the operator-configured image.
	// +optional
	CgsImage string `json:"cgsImage,omitempty"`

	// NetworkRef is the name of the CcfNetwork CR this
	// member is associated with. Must be in the same
	// namespace. Used to discover the CCF endpoint and
	// service cert for activation.
	// +optional
	NetworkRef string `json:"networkRef,omitempty"`
}

// SecretRef references a Kubernetes Secret by name.
type SecretRef struct {
	// Name is the name of the Secret.
	// +kubebuilder:validation:Required
	Name string `json:"name"`
}

// CcfMemberPhase represents the lifecycle phase of a CcfMember.
// +kubebuilder:validation:Enum=Pending;CertsGenerated;GovernanceClientDeployed;Activating;Active;Failed
type CcfMemberPhase string

const (
	CcfMemberPhasePending           CcfMemberPhase = "Pending"
	CcfMemberPhaseCertsGenerated    CcfMemberPhase = "CertsGenerated"
	CcfMemberPhaseGovClientDeployed CcfMemberPhase = "GovernanceClientDeployed"
	CcfMemberPhaseActivating        CcfMemberPhase = "Activating"
	CcfMemberPhaseActive            CcfMemberPhase = "Active"
	CcfMemberPhaseFailed            CcfMemberPhase = "Failed"
)

// CcfMemberStatus defines the observed state of a CcfMember.
type CcfMemberStatus struct {
	// Phase is the current lifecycle phase.
	// +optional
	Phase CcfMemberPhase `json:"phase,omitempty"`

	// ObservedGeneration is the most recent generation observed
	// by the controller.
	// +optional
	ObservedGeneration int64 `json:"observedGeneration,omitempty"`

	// SecretRef is the name of the Secret containing the
	// generated certs and keys.
	// +optional
	SecretRef string `json:"secretRef,omitempty"`

	// GovernanceClientEndpoint is the in-cluster endpoint of
	// the governance client Service for this member.
	// +optional
	GovernanceClientEndpoint string `json:"governanceClientEndpoint,omitempty"`

	// TraceParent is the W3C traceparent header for the
	// in-flight operation.
	// +optional
	TraceParent string `json:"traceParent,omitempty"`

	// LastOperationTraceId is the trace ID of the most recent
	// operation.
	// +optional
	LastOperationTraceId string `json:"lastOperationTraceId,omitempty"`

	// Conditions represent the latest available observations of
	// the member's state.
	// +optional
	Conditions []metav1.Condition `json:"conditions,omitempty"`
}

// Condition types for CcfMember.
const (
	ConditionTypeCertsReady      = "CertsReady"
	ConditionTypeGovClientReady  = "GovernanceClientReady"
	ConditionTypeMemberActivated = "MemberActivated"
)

// Secret data keys for CcfMember-generated secrets.
const (
	SecretKeyCert          = "cert.pem"
	SecretKeyPrivateKey    = "privk.pem"
	SecretKeyEncPublicKey  = "enc_pubk.pem"
	SecretKeyEncPrivateKey = "enc_privk.pem"
)

// +kubebuilder:object:root=true
// +kubebuilder:subresource:status
// +kubebuilder:resource:shortName=ccfm
// +kubebuilder:printcolumn:name="Phase",type=string,JSONPath=`.status.phase`
// +kubebuilder:printcolumn:name="Identifier",type=string,JSONPath=`.spec.identifier`
// +kubebuilder:printcolumn:name="Operator",type=boolean,JSONPath=`.spec.isOperator`
// +kubebuilder:printcolumn:name="Age",type=date,JSONPath=`.metadata.creationTimestamp`

// CcfMember is the Schema for the ccfmembers API.
type CcfMember struct {
	metav1.TypeMeta   `json:",inline"`
	metav1.ObjectMeta `json:"metadata,omitempty"`

	Spec   CcfMemberSpec   `json:"spec,omitempty"`
	Status CcfMemberStatus `json:"status,omitempty"`
}

// +kubebuilder:object:root=true

// CcfMemberList contains a list of CcfMember.
type CcfMemberList struct {
	metav1.TypeMeta `json:",inline"`
	metav1.ListMeta `json:"metadata,omitempty"`
	Items           []CcfMember `json:"items"`
}

func init() {
	SchemeBuilder.Register(
		&CcfMember{},
		&CcfMemberList{},
	)
}
