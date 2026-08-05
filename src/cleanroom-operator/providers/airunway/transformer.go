package airunway

import (
	"encoding/json"
	"fmt"
	"strconv"

	cleanroomv1alpha1 "github.com/Azure/azure-cleanroom/cleanroom-operator/api/v1alpha1"
	airunwayv1alpha1 "github.com/kaito-project/airunway/controller/api/v1alpha1"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
)

// ProviderOverrides holds the provider-specific
// configuration extracted from
// spec.provider.overrides.
type ProviderOverrides struct {
	// EnvironmentRef is the name of the pre-existing
	// cleanroom Environment CR.
	EnvironmentRef string `json:"environmentRef"`

	// CvmSku is the Azure CVM SKU for flex nodes.
	// +optional
	CvmSku string `json:"cvmSku,omitempty"`

	// StorageAccountID overrides the blob storage
	// account for model upload.
	// +optional
	StorageAccountID string `json:"storageAccountId,omitempty"`

	// ManagedIdentityID overrides the managed identity
	// for storage access.
	// +optional
	ManagedIdentityID string `json:"managedIdentityId,omitempty"`

	// EncryptionMode is the storage encryption mode
	// (sse or cse).
	// +optional
	EncryptionMode string `json:"encryptionMode,omitempty"`

	// SourceFile selects a specific file from the
	// HuggingFace repo (e.g. a GGUF quantization
	// variant like "tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf").
	// Required for GGUF repos with multiple variants;
	// AIRunway's own model spec has no field for this, so
	// it is exposed here as a provider escape hatch.
	// +optional
	SourceFile string `json:"sourceFile,omitempty"`
}

// ProviderDefaults holds cluster-wide defaults applied to
// every ModelDeployment when its provider.overrides omit
// them. They are configured once on the provider Deployment
// (via env vars) so per-deployment specs stay minimal — the
// in-cluster provider cannot provision Azure storage/identity
// itself the way the az CLI does.
type ProviderDefaults struct {
	StorageAccountID  string
	ManagedIdentityID string
	CvmSku            string
	EncryptionMode    string
}

// applyDefaults fills empty override fields from the
// cluster-wide provider defaults.
func applyDefaults(
	o *ProviderOverrides,
	d ProviderDefaults,
) {
	if o.StorageAccountID == "" {
		o.StorageAccountID = d.StorageAccountID
	}
	if o.ManagedIdentityID == "" {
		o.ManagedIdentityID = d.ManagedIdentityID
	}
	if o.CvmSku == "" {
		o.CvmSku = d.CvmSku
	}
	if o.EncryptionMode == "" {
		o.EncryptionMode = d.EncryptionMode
	}
}

// parseOverrides extracts ProviderOverrides from the
// raw JSON provider.overrides field.
func parseOverrides(
	md *airunwayv1alpha1.ModelDeployment,
) (*ProviderOverrides, error) {
	if md.Spec.Provider == nil ||
		md.Spec.Provider.Overrides == nil {
		return nil, fmt.Errorf(
			"provider.overrides is required")
	}
	var o ProviderOverrides
	if err := json.Unmarshal(
		md.Spec.Provider.Overrides.Raw, &o,
	); err != nil {
		return nil, fmt.Errorf(
			"parsing provider.overrides: %w", err)
	}
	if o.EnvironmentRef == "" {
		return nil, fmt.Errorf(
			"provider.overrides.environmentRef " +
				"is required")
	}
	return &o, nil
}

// BuildModelRegistration creates a cleanroom
// ModelRegistration CR from an AIRunway ModelDeployment.
func BuildModelRegistration(
	md *airunwayv1alpha1.ModelDeployment,
	overrides *ProviderOverrides,
) *cleanroomv1alpha1.ModelRegistration {
	mr := &cleanroomv1alpha1.ModelRegistration{
		ObjectMeta: metav1.ObjectMeta{
			Name:      md.Name,
			Namespace: md.Namespace,
		},
		Spec: cleanroomv1alpha1.ModelRegistrationSpec{
			EnvironmentRef: overrides.EnvironmentRef,
			Model: cleanroomv1alpha1.ModelSpec{
				ID: md.Spec.Model.ID,
			},
		},
	}

	if overrides.SourceFile != "" {
		mr.Spec.Model.SourceFile = overrides.SourceFile
	}

	if overrides.CvmSku != "" {
		mr.Spec.VMSize = overrides.CvmSku
	}

	if overrides.StorageAccountID != "" {
		mr.Spec.Storage.StorageAccountId =
			overrides.StorageAccountID
	}
	if overrides.ManagedIdentityID != "" {
		mr.Spec.Storage.ManagedIdentityId =
			overrides.ManagedIdentityID
	}
	if overrides.EncryptionMode != "" {
		mr.Spec.Storage.EncryptionMode =
			overrides.EncryptionMode
	}

	return mr
}

// BuildModelDeployment creates a cleanroom
// ModelDeployment CR from an AIRunway ModelDeployment.
func BuildModelDeployment(
	md *airunwayv1alpha1.ModelDeployment,
) *cleanroomv1alpha1.ModelDeployment {
	deploy := &cleanroomv1alpha1.ModelDeployment{
		ObjectMeta: metav1.ObjectMeta{
			Name:      md.Name,
			Namespace: md.Namespace,
		},
		Spec: cleanroomv1alpha1.ModelDeploymentSpec{
			ModelRegistrationRef: md.Name,
			Predictor:            buildPredictor(md),
		},
	}

	return deploy
}

func buildPredictor(
	md *airunwayv1alpha1.ModelDeployment,
) *cleanroomv1alpha1.PredictorSpec {
	engine := md.ResolvedEngineType()
	predictor := &cleanroomv1alpha1.PredictorSpec{
		Model: &cleanroomv1alpha1.PredictorModelSpec{
			ModelFormat: &cleanroomv1alpha1.ModelFormatSpec{
				Name: mapEngineToModelFormat(engine),
			},
			Runtime: mapEngineToRuntime(engine),
		},
	}

	// Scaling.
	if md.Spec.Scaling != nil &&
		md.Spec.Scaling.Replicas > 0 {
		r := md.Spec.Scaling.Replicas
		predictor.MinReplicas = &r
		predictor.MaxReplicas = &r
	}

	// Resources.
	if md.Spec.Resources != nil {
		res := &cleanroomv1alpha1.ResourceRequirements{
			Requests: map[string]string{},
			Limits:   map[string]string{},
		}

		if md.Spec.Resources.GPU != nil &&
			md.Spec.Resources.GPU.Count > 0 {
			gpuType := "nvidia.com/gpu"
			if md.Spec.Resources.GPU.Type != "" {
				gpuType = md.Spec.Resources.GPU.Type
			}
			count := strconv.FormatInt(
				int64(md.Spec.Resources.GPU.Count), 10)
			res.Requests[gpuType] = count
			res.Limits[gpuType] = count
		}
		if md.Spec.Resources.Memory != "" {
			res.Requests["memory"] =
				md.Spec.Resources.Memory
			res.Limits["memory"] =
				md.Spec.Resources.Memory
		}
		if md.Spec.Resources.CPU != "" {
			res.Requests["cpu"] =
				md.Spec.Resources.CPU
			res.Limits["cpu"] =
				md.Spec.Resources.CPU
		}

		predictor.Model.Resources = res
	}

	return predictor
}

// mapEngineToRuntime converts an AIRunway engine type to
// a cleanroom-operator serving runtime name.
func mapEngineToRuntime(
	engine airunwayv1alpha1.EngineType,
) string {
	switch engine {
	case airunwayv1alpha1.EngineTypeVLLM:
		return "vllm-openai"
	case airunwayv1alpha1.EngineTypeLlamaCpp:
		return "llamacpp-server"
	default:
		return string(engine)
	}
}

// mapEngineToModelFormat converts an AIRunway engine type
// to the cleanroom-operator model format name. llama.cpp
// serves GGUF; vLLM serves HuggingFace/safetensors weights.
func mapEngineToModelFormat(
	engine airunwayv1alpha1.EngineType,
) string {
	switch engine {
	case airunwayv1alpha1.EngineTypeLlamaCpp:
		return "gguf"
	case airunwayv1alpha1.EngineTypeVLLM:
		return "safetensors"
	default:
		return "gguf"
	}
}
