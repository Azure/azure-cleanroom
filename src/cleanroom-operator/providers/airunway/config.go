package airunway

import (
	"context"
	"encoding/json"
	"time"

	"k8s.io/apimachinery/pkg/api/errors"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/types"
	"sigs.k8s.io/controller-runtime/pkg/client"
	"sigs.k8s.io/controller-runtime/pkg/log"

	airunwayv1alpha1 "github.com/kaito-project/airunway/controller/api/v1alpha1"
)

const (
	// ProviderConfigName is the InferenceProviderConfig
	// CR name.
	ProviderConfigName = "accr-conf-inferencing"

	// HeartbeatInterval is the interval for updating
	// the provider heartbeat.
	HeartbeatInterval = 1 * time.Minute
)

// shimVersion is injected at build time via ldflags.
var shimVersion = "dev"

// ProviderVersion is reported in InferenceProviderConfig
// status.
var ProviderVersion = ProviderConfigName +
	"-provider:" + shimVersion

// ProviderConfigManager handles registration and
// heartbeat for the accr-conf-inferencing provider.
type ProviderConfigManager struct {
	client client.Client
}

// NewProviderConfigManager creates a new provider config
// manager.
func NewProviderConfigManager(
	c client.Client,
) *ProviderConfigManager {
	return &ProviderConfigManager{client: c}
}

// GetProviderConfigSpec returns the
// InferenceProviderConfigSpec for this provider.
func GetProviderConfigSpec() airunwayv1alpha1.InferenceProviderConfigSpec {
	return airunwayv1alpha1.InferenceProviderConfigSpec{
		Capabilities: &airunwayv1alpha1.ProviderCapabilities{
			Engines: []airunwayv1alpha1.EngineCapability{
				{
					Name: airunwayv1alpha1.EngineTypeVLLM,
					ServingModes: []airunwayv1alpha1.ServingMode{
						airunwayv1alpha1.ServingModeAggregated,
					},
					GPUSupport: true,
					CPUSupport: false,
				},
				{
					Name: airunwayv1alpha1.EngineTypeLlamaCpp,
					ServingModes: []airunwayv1alpha1.ServingMode{
						airunwayv1alpha1.ServingModeAggregated,
					},
					GPUSupport: true,
					CPUSupport: true,
				},
			},
		},
		// Explicit-only: never auto-selected.
		SelectionRules: nil,
	}
}

// Register creates or updates the
// InferenceProviderConfig CR.
func (m *ProviderConfigManager) Register(
	ctx context.Context,
) error {
	logger := log.FromContext(ctx)

	spec := GetProviderConfigSpec()
	specJSON, err := json.Marshal(spec)
	if err != nil {
		return err
	}

	var existing airunwayv1alpha1.InferenceProviderConfig
	err = m.client.Get(ctx, types.NamespacedName{
		Name: ProviderConfigName,
	}, &existing)

	if errors.IsNotFound(err) {
		cfg := &airunwayv1alpha1.InferenceProviderConfig{
			ObjectMeta: metav1.ObjectMeta{
				Name: ProviderConfigName,
			},
		}
		if jsonErr := json.Unmarshal(
			specJSON, &cfg.Spec,
		); jsonErr != nil {
			return jsonErr
		}
		if createErr := m.client.Create(
			ctx, cfg,
		); createErr != nil {
			return createErr
		}
		logger.Info("Created InferenceProviderConfig",
			"name", ProviderConfigName)
		return m.updateStatus(ctx, cfg)
	}
	if err != nil {
		return err
	}

	if jsonErr := json.Unmarshal(
		specJSON, &existing.Spec,
	); jsonErr != nil {
		return jsonErr
	}
	if updateErr := m.client.Update(
		ctx, &existing,
	); updateErr != nil {
		return updateErr
	}
	logger.Info("Updated InferenceProviderConfig",
		"name", ProviderConfigName)
	return m.updateStatus(ctx, &existing)
}

// Unregister removes the InferenceProviderConfig CR.
func (m *ProviderConfigManager) Unregister(
	ctx context.Context,
) error {
	cfg := &airunwayv1alpha1.InferenceProviderConfig{
		ObjectMeta: metav1.ObjectMeta{
			Name: ProviderConfigName,
		},
	}
	return client.IgnoreNotFound(
		m.client.Delete(ctx, cfg),
	)
}

// StartHeartbeat periodically updates the provider
// status to signal liveness.
func (m *ProviderConfigManager) StartHeartbeat(
	ctx context.Context,
) {
	logger := log.FromContext(ctx)
	go func() {
		ticker := time.NewTicker(HeartbeatInterval)
		defer ticker.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-ticker.C:
				var cfg airunwayv1alpha1.InferenceProviderConfig
				if err := m.client.Get(
					ctx,
					types.NamespacedName{
						Name: ProviderConfigName,
					},
					&cfg,
				); err != nil {
					logger.Error(err,
						"heartbeat: get failed")
					continue
				}
				if err := m.updateStatus(
					ctx, &cfg,
				); err != nil {
					logger.Error(err,
						"heartbeat: status update failed")
				}
			}
		}
	}()
}

func (m *ProviderConfigManager) updateStatus(
	ctx context.Context,
	cfg *airunwayv1alpha1.InferenceProviderConfig,
) error {
	cfg.Status.Ready = true
	cfg.Status.Version = ProviderVersion
	cfg.Status.LastHeartbeat = &metav1.Time{
		Time: time.Now(),
	}
	return m.client.Status().Update(ctx, cfg)
}
