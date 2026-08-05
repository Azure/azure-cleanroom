package v1alpha1

import (
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
)

// GovernanceServiceSpec defines the desired state of a
// GovernanceService.
type GovernanceServiceSpec struct {
	// NetworkRef is the name of the CcfNetwork CR this
	// service is associated with. Must be in the same
	// namespace.
	// +kubebuilder:validation:Required
	NetworkRef string `json:"networkRef"`

	// MemberRef is the name of the CcfMember CR to use
	// for submitting governance proposals. Must be in the
	// same namespace.
	// +kubebuilder:validation:Required
	MemberRef string `json:"memberRef"`

	// ConstitutionImage is the OCI image reference for the
	// constitution bundle. Resolved from env var
	// CGS_CONSTITUTION_IMAGE if not set.
	// +optional
	ConstitutionImage string `json:"constitutionImage,omitempty"`

	// JsAppImage is the OCI image reference for the JS app
	// bundle. Resolved from env var CGS_JS_APP_IMAGE if
	// not set.
	// +optional
	JsAppImage string `json:"jsAppImage,omitempty"`

	// TenantId is the Azure AD tenant ID for JWT issuer
	// configuration. When set, a tenant-specific JWT issuer
	// is registered in addition to the common issuer.
	// +optional
	TenantId string `json:"tenantId,omitempty"`

	// OidcContainerName is the container name used in the
	// shared OIDC storage account for this environment's
	// OpenID configuration documents. Must be unique
	// across all developers/CI runs.
	// +optional
	OidcContainerName string `json:"oidcContainerName,omitempty"`
}

// GovernanceServicePhase represents the lifecycle phase.
// +kubebuilder:validation:Enum=Pending;WaitingForDeps;Deploying;Ready;Failed
type GovernanceServicePhase string

const (
	GovernanceServicePhasePending   GovernanceServicePhase = "Pending"
	GovernanceServicePhaseWaiting   GovernanceServicePhase = "WaitingForDeps"
	GovernanceServicePhaseDeploying GovernanceServicePhase = "Deploying"
	GovernanceServicePhaseReady     GovernanceServicePhase = "Ready"
	GovernanceServicePhaseFailed    GovernanceServicePhase = "Failed"
)

// GovernanceServiceStatus defines the observed state of a
// GovernanceService.
type GovernanceServiceStatus struct {
	// Phase is the current lifecycle phase.
	// +optional
	Phase GovernanceServicePhase `json:"phase,omitempty"`

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

	// OidcIssuerUrl is the OIDC issuer URL configured on
	// the storage account static website.
	// +optional
	OidcIssuerUrl string `json:"oidcIssuerUrl,omitempty"`

	// Conditions represent the latest available observations
	// of the service's state.
	// +optional
	Conditions []metav1.Condition `json:"conditions,omitempty"`
}

// Condition types for GovernanceService.
const (
	ConditionTypeCaCertBundleSet       = "CaCertBundleSet"
	ConditionTypeJwtIssuersConfigured  = "JwtIssuersConfigured"
	ConditionTypeConstitutionDeployed  = "ConstitutionDeployed"
	ConditionTypeJsRuntimeConfigured   = "JsRuntimeConfigured"
	ConditionTypeJsAppDeployed         = "JsAppDeployed"
	ConditionTypeOidcIssuerEnabled     = "OidcIssuerEnabled"
	ConditionTypeOidcDocumentsUploaded = "OidcDocumentsUploaded"
)

// +kubebuilder:object:root=true
// +kubebuilder:subresource:status
// +kubebuilder:resource:shortName=gs
// +kubebuilder:printcolumn:name="Phase",type=string,JSONPath=`.status.phase`
// +kubebuilder:printcolumn:name="Network",type=string,JSONPath=`.spec.networkRef`
// +kubebuilder:printcolumn:name="Member",type=string,JSONPath=`.spec.memberRef`
// +kubebuilder:printcolumn:name="Age",type=date,JSONPath=`.metadata.creationTimestamp`

// GovernanceService is the Schema for the
// governanceservices API. It automates the governance
// service deployment (CA certs, JWT issuers, constitution,
// JS app, OIDC) using the referenced member's CGS client.
type GovernanceService struct {
	metav1.TypeMeta   `json:",inline"`
	metav1.ObjectMeta `json:"metadata,omitempty"`

	Spec   GovernanceServiceSpec   `json:"spec,omitempty"`
	Status GovernanceServiceStatus `json:"status,omitempty"`
}

// +kubebuilder:object:root=true

// GovernanceServiceList contains a list of GovernanceService.
type GovernanceServiceList struct {
	metav1.TypeMeta `json:",inline"`
	metav1.ListMeta `json:"metadata,omitempty"`
	Items           []GovernanceService `json:"items"`
}

func init() {
	SchemeBuilder.Register(
		&GovernanceService{},
		&GovernanceServiceList{},
	)
}
