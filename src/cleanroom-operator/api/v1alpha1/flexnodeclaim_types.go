package v1alpha1

import (
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
)

// FlexNodeClaimPhase represents the lifecycle phase of a
// FlexNodeClaim.
// +kubebuilder:validation:Enum=Pending;Provisioning;Ready;Failed;Deleting
type FlexNodeClaimPhase string

const (
	FlexNodeClaimPhasePending      FlexNodeClaimPhase = "Pending"
	FlexNodeClaimPhaseProvisioning FlexNodeClaimPhase = "Provisioning"
	FlexNodeClaimPhaseReady        FlexNodeClaimPhase = "Ready"
	FlexNodeClaimPhaseFailed       FlexNodeClaimPhase = "Failed"
	FlexNodeClaimPhaseDeleting     FlexNodeClaimPhase = "Deleting"
)

const (
	// FlexNodeClaimFinalizer is the finalizer placed on
	// FlexNodeClaim resources to ensure infrastructure
	// cleanup before deletion.
	FlexNodeClaimFinalizer = "cleanroom.azure.com/flexnodeclaim"
)

// FlexNodeClaimSpec defines the desired state of a
// FlexNodeClaim.
type FlexNodeClaimSpec struct {
	// NodeClassName is the name of the FlexNodeClass that
	// provides infrastructure configuration for this claim.
	// +kubebuilder:validation:Required
	NodeClassName string `json:"nodeClassName"`

	// NodeClaimName is the name of the Karpenter NodeClaim
	// that triggered this FlexNodeClaim.
	// +kubebuilder:validation:Required
	NodeClaimName string `json:"nodeClaimName"`

	// InstanceType is the Karpenter instance type requested
	// for this flex node.
	// +optional
	InstanceType string `json:"instanceType,omitempty"`
}

// FlexNodeClaimStatus defines the observed state of a
// FlexNodeClaim.
type FlexNodeClaimStatus struct {
	// Phase is the current lifecycle phase.
	// +optional
	Phase FlexNodeClaimPhase `json:"phase,omitempty"`

	// ProviderID is the unique identifier assigned to the
	// node by the accr provider (accr://<env>/<fncName>).
	// Set once the claim is created.
	// +optional
	ProviderID string `json:"providerID,omitempty"`

	// NodeName is the Kubernetes node name once the flex
	// node has joined the workload cluster.
	// +optional
	NodeName string `json:"nodeName,omitempty"`

	// OperationID is the Operation-Location path returned
	// by the cluster provider for async operations.
	// +optional
	OperationID string `json:"operationID,omitempty"`

	// TraceParent is the W3C traceparent header for the
	// in-flight operation. Used internally for
	// cross-reconcile span correlation. Cleared when the
	// operation completes.
	// +optional
	TraceParent string `json:"traceParent,omitempty"`

	// LastOperationTraceID is the trace ID of the most
	// recent create/update/delete operation. Persists
	// after completion for debugging.
	// +optional
	LastOperationTraceID string `json:"lastOperationTraceId,omitempty"`

	// Conditions represent the latest available observations.
	// +optional
	Conditions []metav1.Condition `json:"conditions,omitempty"`
}

// +kubebuilder:object:root=true
// +kubebuilder:subresource:status
// +kubebuilder:resource:shortName=fnclaim;flexnclaim
// +kubebuilder:printcolumn:name="Phase",type=string,JSONPath=`.status.phase`
// +kubebuilder:printcolumn:name="NodeClass",type=string,JSONPath=`.spec.nodeClassName`
// +kubebuilder:printcolumn:name="NodeName",type=string,JSONPath=`.status.nodeName`
// +kubebuilder:printcolumn:name="TraceID",type=string,JSONPath=`.status.lastOperationTraceId`,priority=1
// +kubebuilder:printcolumn:name="Age",type=date,JSONPath=`.metadata.creationTimestamp`

// FlexNodeClaim is the Schema for the flexnodeclaims API.
// It is the bridge CR between the Karpenter accr
// CloudProvider and the cluster-provider-client. The
// provider creates a FlexNodeClaim when Karpenter
// provisions a NodeClaim; the FlexNodeClaim controller
// reconciles it by calling the cluster-provider-client to
// add a flex node to the workload cluster.
type FlexNodeClaim struct {
	metav1.TypeMeta   `json:",inline"`
	metav1.ObjectMeta `json:"metadata,omitempty"`

	Spec   FlexNodeClaimSpec   `json:"spec,omitempty"`
	Status FlexNodeClaimStatus `json:"status,omitempty"`
}

// +kubebuilder:object:root=true

// FlexNodeClaimList contains a list of FlexNodeClaim.
type FlexNodeClaimList struct {
	metav1.TypeMeta `json:",inline"`
	metav1.ListMeta `json:"metadata,omitempty"`
	Items           []FlexNodeClaim `json:"items"`
}

func init() {
	SchemeBuilder.Register(
		&FlexNodeClaim{},
		&FlexNodeClaimList{},
	)
}
