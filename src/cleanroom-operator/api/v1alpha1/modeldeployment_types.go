package v1alpha1

import (
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
)

// NodeProvisioningMode controls how nodes are
// provisioned for a ModelDeployment.
// +kubebuilder:validation:Enum=auto;manual
type NodeProvisioningMode string

const (
	// NodeProvisioningModeAuto enables flex nodes in
	// auto mode, delegating node lifecycle to
	// Karpenter.
	NodeProvisioningModeAuto NodeProvisioningMode = "auto"

	// NodeProvisioningModeManual enables flex nodes in
	// manual mode, provisioning a fixed number of
	// nodes directly.
	NodeProvisioningModeManual NodeProvisioningMode = "manual"
)

// ModelDeploymentSpec defines the desired state
// of a ModelDeployment resource.
type ModelDeploymentSpec struct {
	// ModelRegistrationRef is the name of the parent
	// ModelRegistration CR this instance deploys.
	// +kubebuilder:validation:Required
	// +kubebuilder:validation:MinLength=1
	ModelRegistrationRef string `json:"modelRegistrationRef"`

	// NodeProvisioningMode controls how flex nodes are
	// provisioned for this instance. "auto" enables
	// flex nodes in auto mode (Karpenter manages
	// lifecycle). "manual" enables flex nodes in
	// manual mode (fixed node count). When empty,
	// no flex node setup is performed.
	// +optional
	NodeProvisioningMode NodeProvisioningMode `json:"nodeProvisioningMode,omitempty"`

	// Predictor defines the predictor configuration
	// for the deployed model. If nil, defaults are
	// derived from the model format.
	// +optional
	Predictor *PredictorSpec `json:"predictor,omitempty"`
}

// PredictorSpec defines the predictor configuration for
// model deployment via the inferencing agent.
type PredictorSpec struct {
	// MinReplicas is the minimum number of replicas.
	// +optional
	// +kubebuilder:default=1
	MinReplicas *int32 `json:"minReplicas,omitempty"`

	// MaxReplicas is the maximum number of replicas.
	// +optional
	MaxReplicas *int32 `json:"maxReplicas,omitempty"`

	// Timeout is the inference timeout in seconds.
	// +optional
	Timeout *int32 `json:"timeout,omitempty"`

	// Model defines the model serving configuration.
	// +optional
	Model *PredictorModelSpec `json:"model,omitempty"`
}

// PredictorModelSpec defines model-specific serving
// parameters.
type PredictorModelSpec struct {
	// ModelFormat identifies the model format
	// (e.g. "gguf", "sklearn", "safetensors").
	// +optional
	ModelFormat *ModelFormatSpec `json:"modelFormat,omitempty"`

	// Runtime is the serving runtime to use
	// (e.g. "llamacpp-server", "vllm-openai").
	// +optional
	Runtime string `json:"runtime,omitempty"`

	// Resources defines compute resource requirements.
	// +optional
	Resources *ResourceRequirements `json:"resources,omitempty"`

	// Args are additional command-line arguments
	// for the serving container.
	// +optional
	Args []string `json:"args,omitempty"`

	// Env are additional environment variables for
	// the serving container.
	// +optional
	Env []EnvVar `json:"env,omitempty"`
}

// ModelFormatSpec identifies the model format.
type ModelFormatSpec struct {
	// Name is the model format name.
	Name string `json:"name"`
}

// ResourceRequirements defines compute resources.
type ResourceRequirements struct {
	// Requests describes minimum resources required.
	// +optional
	Requests map[string]string `json:"requests,omitempty"`

	// Limits describes maximum resources allowed.
	// +optional
	Limits map[string]string `json:"limits,omitempty"`
}

// EnvVar represents an environment variable.
type EnvVar struct {
	// Name of the environment variable.
	Name string `json:"name"`

	// Value of the environment variable.
	Value string `json:"value,omitempty"`
}

// ModelDeploymentPhase represents the lifecycle
// phase.
// +kubebuilder:validation:Enum=Pending;Deploying;Ready;Failed
type ModelDeploymentPhase string

const (
	ModelDeploymentPhasePending   ModelDeploymentPhase = "Pending"
	ModelDeploymentPhaseDeploying ModelDeploymentPhase = "Deploying"
	ModelDeploymentPhaseReady     ModelDeploymentPhase = "Ready"
	ModelDeploymentPhaseFailed    ModelDeploymentPhase = "Failed"
)

// ModelDeploymentStatus defines the observed
// state of a ModelDeployment resource.
type ModelDeploymentStatus struct {
	// Phase is the current lifecycle phase.
	// +optional
	Phase ModelDeploymentPhase `json:"phase,omitempty"`

	// Message is a human-readable summary of why the
	// resource is in its current phase.
	// +optional
	Message string `json:"message,omitempty"`

	// ObservedGeneration is the most recent generation
	// observed by the controller.
	// +optional
	ObservedGeneration int64 `json:"observedGeneration,omitempty"`

	// TraceParent is the W3C traceparent header for
	// the in-flight operation.
	// +optional
	TraceParent string `json:"traceParent,omitempty"`

	// LastOperationTraceId is the trace ID of the most
	// recent operation.
	// +optional
	LastOperationTraceId string `json:"lastOperationTraceId,omitempty"`

	// LastHandledReconcileAt is the timestamp of the
	// last handled reconcile annotation request.
	// +optional
	LastHandledReconcileAt string `json:"lastHandledReconcileAt,omitempty"`

	// ServiceEndpoint is the URL of the deployed
	// inferencing endpoint.
	// +optional
	ServiceEndpoint string `json:"serviceEndpoint,omitempty"`

	// ServiceCaCert is the PEM-encoded CA certificate
	// for the service endpoint.
	// +optional
	ServiceCaCert string `json:"serviceCaCert,omitempty"`

	// CorrelationID is the correlation ID used for the
	// inferencing agent deployment request. Persisted
	// across reconcile loops to resume polling.
	// +optional
	CorrelationID string `json:"correlationId,omitempty"`

	// Conditions represent the latest available
	// observations of the resource's state.
	// +optional
	Conditions []metav1.Condition `json:"conditions,omitempty"`
}

// Condition types for ModelDeployment.
const (
	ConditionTypeMDFlexNodeReady           = "FlexNodeReady"
	ConditionTypeMDDeploymentReady         = "ModelRegistrationReady"
	ConditionTypeMDEndpointSubmitted       = "EndpointSubmitted"
	ConditionTypeMDInferenceServiceCreated = "InferenceServiceCreated"
	ConditionTypeMDEndpointDeployed        = "EndpointDeployed"
	ConditionTypeMDDeploymentHealthy       = "DeploymentHealthy"
)

// +kubebuilder:object:root=true
// +kubebuilder:subresource:status
// +kubebuilder:resource:shortName=md
// +kubebuilder:printcolumn:name="Phase",type=string,JSONPath=`.status.phase`
// +kubebuilder:printcolumn:name="ModelRegistration",type=string,JSONPath=`.spec.modelRegistrationRef`
// +kubebuilder:printcolumn:name="Age",type=date,JSONPath=`.metadata.creationTimestamp`

// ModelDeployment is the Schema for the
// modeldeployments API.
type ModelDeployment struct {
	metav1.TypeMeta   `json:",inline"`
	metav1.ObjectMeta `json:"metadata,omitempty"`

	Spec   ModelDeploymentSpec   `json:"spec,omitempty"`
	Status ModelDeploymentStatus `json:"status,omitempty"`
}

// +kubebuilder:object:root=true

// ModelDeploymentList contains a list of
// ModelDeployment.
type ModelDeploymentList struct {
	metav1.TypeMeta `json:",inline"`
	metav1.ListMeta `json:"metadata,omitempty"`
	Items           []ModelDeployment `json:"items"`
}

func init() {
	SchemeBuilder.Register(
		&ModelDeployment{},
		&ModelDeploymentList{},
	)
}
