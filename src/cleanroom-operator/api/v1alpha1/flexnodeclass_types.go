package v1alpha1

import (
	"encoding/json"

	"github.com/awslabs/operatorpkg/status"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
)

// SecretKeyReference is a reference to a key in a Secret.
type SecretKeyReference struct {
	// Name is the name of the Secret.
	Name string `json:"name"`
	// Key is the key within the Secret data.
	Key string `json:"key"`
}

// FlexNodeClassSpec defines the desired state of a
// FlexNodeClass.
type FlexNodeClassSpec struct {
	// ClusterName is the cluster-provider-client cluster
	// name used in the API path:
	// /clusters/{clusterName}/flexnodes/...
	// +kubebuilder:validation:Required
	// +kubebuilder:validation:XValidation:rule="self == oldSelf",message="clusterName is immutable once set"
	ClusterName string `json:"clusterName"`

	// InfraType selects the provider implementation
	// (virtual, aks, caci).
	// +kubebuilder:validation:Required
	// +kubebuilder:validation:XValidation:rule="self == oldSelf",message="infraType is immutable once set"
	InfraType string `json:"infraType"`

	// PolicySigningCertPem is the PEM-encoded certificate
	// for api-server-proxy policy verification.
	// +optional
	PolicySigningCertPem string `json:"policySigningCertPem,omitempty"`

	// PolicySigningCertSecret references a Secret
	// containing the policy signing certificate.
	// +optional
	PolicySigningCertSecret *SecretKeyReference `json:"policySigningCertSecret,omitempty"`

	// ProviderConfig is opaque provider-specific
	// configuration passed to the cluster-provider-client.
	// +optional
	ProviderConfig json.RawMessage `json:"providerConfig,omitempty"`
}

// FlexNodeClassStatus defines the observed state of a
// FlexNodeClass.
type FlexNodeClassStatus struct {
	// Conditions represent the latest available observations.
	// +optional
	Conditions []status.Condition `json:"conditions,omitempty"`
}

func (in *FlexNodeClass) StatusConditions() status.ConditionSet {
	return status.NewReadyConditions().For(in)
}

func (in *FlexNodeClass) GetConditions() []status.Condition {
	return in.Status.Conditions
}

func (in *FlexNodeClass) SetConditions(
	conditions []status.Condition,
) {
	in.Status.Conditions = conditions
}

// +kubebuilder:object:root=true
// +kubebuilder:subresource:status
// +kubebuilder:resource:scope=Cluster,shortName=fnc;flexnc,categories=karpenter
// +kubebuilder:printcolumn:name="Cluster",type=string,JSONPath=`.spec.clusterName`
// +kubebuilder:printcolumn:name="InfraType",type=string,JSONPath=`.spec.infraType`
// +kubebuilder:printcolumn:name="Age",type=date,JSONPath=`.metadata.creationTimestamp`

// FlexNodeClass is the Schema for the flexnodeclasses API.
// It is the Karpenter NodeClass implementation for the accr
// CloudProvider, providing infrastructure config for flex
// node provisioning.
type FlexNodeClass struct {
	metav1.TypeMeta   `json:",inline"`
	metav1.ObjectMeta `json:"metadata,omitempty"`

	Spec FlexNodeClassSpec `json:"spec,omitempty"`
	// +kubebuilder:default:={conditions: {{type: "Ready", status: "True", reason:"Ready", lastTransitionTime: "2024-01-01T01:01:01Z", message: ""}}}
	Status FlexNodeClassStatus `json:"status,omitempty"`
}

// +kubebuilder:object:root=true

// FlexNodeClassList contains a list of FlexNodeClass.
type FlexNodeClassList struct {
	metav1.TypeMeta `json:",inline"`
	metav1.ListMeta `json:"metadata,omitempty"`
	Items           []FlexNodeClass `json:"items"`
}

func init() {
	SchemeBuilder.Register(
		&FlexNodeClass{},
		&FlexNodeClassList{},
	)
}
