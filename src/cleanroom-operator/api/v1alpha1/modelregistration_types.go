package v1alpha1

import (
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
)

// ModelRegistrationSpec defines the desired state of a
// ModelRegistration resource.
type ModelRegistrationSpec struct {
	// EnvironmentRef is the name of the Environment CR
	// this model governance is bound to.
	// +kubebuilder:validation:Required
	// +kubebuilder:validation:MinLength=1
	EnvironmentRef string `json:"environmentRef"`

	// Model defines the model to be deployed.
	// +kubebuilder:validation:Required
	Model ModelSpec `json:"model"`

	// Storage defines the Azure storage configuration
	// for model data.
	// +kubebuilder:validation:Required
	Storage ModelStorageSpec `json:"storage"`

	// Upload controls how the model is uploaded to blob
	// storage.
	// +optional
	Upload ModelUploadSpec `json:"upload,omitempty"`

	// VMSize is the Azure VM SKU to use for flex node.
	// If empty, the cluster provider picks a default.
	// +optional
	VMSize string `json:"vmSize,omitempty"`

	// AutoDeploy controls whether a
	// ModelRegistrationInstance is automatically created
	// once governance setup is complete.
	// +optional
	AutoDeploy bool `json:"autoDeploy,omitempty"`
}

// ModelSpec defines the model to deploy.
type ModelSpec struct {
	// ID is the HuggingFace model identifier (e.g.
	// "TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF").
	// +kubebuilder:validation:Required
	// +kubebuilder:validation:MinLength=1
	ID string `json:"id"`

	// Path is the blob path to the model within the
	// storage container (e.g.
	// "models/tinyllama-chat-gguf/model.gguf"). This
	// path is combined with the dataset document ID
	// to form the modelDir in the governance document.
	// Used in skip mode only.
	// +optional
	Path string `json:"path,omitempty"`

	// BlobPrefix overrides the default blob prefix
	// derived from the model ID. For example, setting
	// "models/my-custom-prefix" will upload model
	// files under that prefix instead of the default
	// "models/<repo-name>".
	// +optional
	BlobPrefix string `json:"blobPrefix,omitempty"`

	// SourceFile selects a specific file from the
	// HuggingFace repo to upload. Required for GGUF
	// repos that contain multiple quantization
	// variants (e.g. "tinyllama-1.1b-chat-v1.0.
	// Q4_K_M.gguf"). If the repo contains exactly
	// one GGUF file, it is auto-selected.
	// +optional
	SourceFile string `json:"sourceFile,omitempty"`
}

// ModelStorageSpec defines Azure storage configuration.
type ModelStorageSpec struct {
	// StorageAccountId is the full ARM resource ID of the
	// Azure storage account.
	// +kubebuilder:validation:Required
	// +kubebuilder:validation:MinLength=1
	StorageAccountId string `json:"storageAccountId"`

	// ManagedIdentityId is the full ARM resource ID of
	// the user-assigned managed identity for blob access.
	// +kubebuilder:validation:Required
	// +kubebuilder:validation:MinLength=1
	ManagedIdentityId string `json:"managedIdentityId"`

	// ContainerName is the blob container name.
	// +kubebuilder:default="models"
	// +optional
	ContainerName string `json:"containerName,omitempty"`

	// EncryptionMode is the storage encryption mode.
	// +kubebuilder:default="SSE"
	// +kubebuilder:validation:Enum="SSE";"CSE"
	// +optional
	EncryptionMode string `json:"encryptionMode,omitempty"`
}

// ModelUploadSpec controls model upload behavior.
type ModelUploadSpec struct {
	// Mode controls whether the controller uploads the
	// model to blob storage.
	// "auto" means the controller downloads from
	// HuggingFace and uploads to blob.
	// "skip" means the controller expects the model to
	// already exist in blob storage.
	// +kubebuilder:default="auto"
	// +kubebuilder:validation:Enum="auto";"skip"
	// +optional
	Mode string `json:"mode,omitempty"`
}

// ModelRegistrationPhase represents the lifecycle phase.
// +kubebuilder:validation:Enum=Pending;Configuring;Ready;Failed
type ModelRegistrationPhase string

const (
	ModelRegistrationPhasePending     ModelRegistrationPhase = "Pending"
	ModelRegistrationPhaseConfiguring ModelRegistrationPhase = "Configuring"
	ModelRegistrationPhaseReady       ModelRegistrationPhase = "Ready"
	ModelRegistrationPhaseFailed      ModelRegistrationPhase = "Failed"
)

// ModelRegistrationStatus defines the observed state of a
// ModelRegistration resource.
type ModelRegistrationStatus struct {
	// Phase is the current lifecycle phase.
	// +optional
	Phase ModelRegistrationPhase `json:"phase,omitempty"`

	// Message is a human-readable summary of why the
	// resource is in its current phase.
	// +optional
	Message string `json:"message,omitempty"`

	// ObservedGeneration is the most recent generation
	// observed by the controller.
	// +optional
	ObservedGeneration int64 `json:"observedGeneration,omitempty"`

	// TraceParent is the W3C traceparent header for the
	// in-flight operation.
	// +optional
	TraceParent string `json:"traceParent,omitempty"`

	// LastOperationTraceId is the trace ID of the most
	// recent operation.
	// +optional
	LastOperationTraceId string `json:"lastOperationTraceId,omitempty"`

	// OidcIssuerUrl is the OIDC issuer URL configured on
	// the storage account static website.
	// +optional
	OidcIssuerUrl string `json:"oidcIssuerUrl,omitempty"`

	// LastHandledReconcileAt is the timestamp of the last
	// handled reconcile annotation request.
	// +optional
	LastHandledReconcileAt string `json:"lastHandledReconcileAt,omitempty"`

	// ModelBlobPath is the blob path to the model
	// within the storage container, resolved during
	// the upload step. For skip mode this is copied
	// from spec.model.path; for auto mode it is
	// derived by the upload logic.
	// +optional
	ModelBlobPath string `json:"modelBlobPath,omitempty"`

	// DatasetDocId is the resolved document ID for the
	// dataset user document in CGS.
	// +optional
	DatasetDocId string `json:"datasetDocId,omitempty"`

	// DatasetProposalId is the proposal ID for the
	// dataset document.
	// +optional
	DatasetProposalId string `json:"datasetProposalId,omitempty"`

	// ModelDocId is the resolved document ID for the
	// model governance user document in CGS.
	// +optional
	ModelDocId string `json:"modelDocId,omitempty"`

	// ModelDocProposalId is the proposal ID for the
	// model governance document.
	// +optional
	ModelDocProposalId string `json:"modelDocProposalId,omitempty"`

	// InstanceRef is the name of the auto-created
	// ModelRegistrationInstance child resource.
	// +optional
	InstanceRef string `json:"instanceRef,omitempty"`

	// Conditions represent the latest available
	// observations of the resource's state.
	// +optional
	Conditions []metav1.Condition `json:"conditions,omitempty"`
}

// OidcStorageAccountName is the pre-provisioned storage
// account used for OIDC discovery documents.
const OidcStorageAccountName = "cleanroomoidc"

// Condition types for ModelRegistration.
const (
	ConditionTypeMRFlexNodeReady    = "FlexNodeReady"
	ConditionTypeMRCcfUserReady     = "CcfUserReady"
	ConditionTypeMRModelUploaded    = "ModelUploaded"
	ConditionTypeMROidcIssuerReady  = "OidcIssuerReady"
	ConditionTypeMRAccessConfigured = "AccessConfigured"
	ConditionTypeMRDatasetDocReady  = "DatasetDocReady"
	ConditionTypeMRModelDocReady    = "ModelDocReady"
)

// +kubebuilder:object:root=true
// +kubebuilder:subresource:status
// +kubebuilder:resource:shortName=mr
// +kubebuilder:printcolumn:name="Phase",type=string,JSONPath=`.status.phase`
// +kubebuilder:printcolumn:name="Model",type=string,JSONPath=`.spec.model.id`
// +kubebuilder:printcolumn:name="Environment",type=string,JSONPath=`.spec.environmentRef`
// +kubebuilder:printcolumn:name="Age",type=date,JSONPath=`.metadata.creationTimestamp`

// ModelRegistration is the Schema for the
// modelregistrations API.
type ModelRegistration struct {
	metav1.TypeMeta   `json:",inline"`
	metav1.ObjectMeta `json:"metadata,omitempty"`

	Spec   ModelRegistrationSpec   `json:"spec,omitempty"`
	Status ModelRegistrationStatus `json:"status,omitempty"`
}

// +kubebuilder:object:root=true

// ModelRegistrationList contains a list of ModelRegistration.
type ModelRegistrationList struct {
	metav1.TypeMeta `json:",inline"`
	metav1.ListMeta `json:"metadata,omitempty"`
	Items           []ModelRegistration `json:"items"`
}

func init() {
	SchemeBuilder.Register(
		&ModelRegistration{},
		&ModelRegistrationList{},
	)
}
