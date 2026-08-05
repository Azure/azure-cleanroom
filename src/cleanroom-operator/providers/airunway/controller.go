package airunway

import (
	"context"
	"fmt"
	"time"

	"k8s.io/apimachinery/pkg/api/errors"
	"k8s.io/apimachinery/pkg/api/meta"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/runtime"
	"k8s.io/apimachinery/pkg/types"
	ctrl "sigs.k8s.io/controller-runtime"
	"sigs.k8s.io/controller-runtime/pkg/client"
	"sigs.k8s.io/controller-runtime/pkg/controller/controllerutil"
	"sigs.k8s.io/controller-runtime/pkg/log"

	cleanroomv1alpha1 "github.com/Azure/azure-cleanroom/cleanroom-operator/api/v1alpha1"
	airunwayv1alpha1 "github.com/kaito-project/airunway/controller/api/v1alpha1"
)

const (
	// ProviderName is the name of this provider.
	ProviderName = "accr-conf-inferencing"

	// FinalizerName is the finalizer used by this
	// controller.
	FinalizerName = "airunway.ai/accr-conf-inferencing"

	// RequeueInterval is the default requeue interval.
	RequeueInterval = 30 * time.Second
)

// ProviderReconciler reconciles AIRunway
// ModelDeployment resources for the
// accr-conf-inferencing provider.
type ProviderReconciler struct {
	client.Client
	Scheme *runtime.Scheme

	// Defaults are cluster-wide fallbacks applied when a
	// ModelDeployment's provider.overrides omit storage /
	// identity / SKU settings.
	Defaults ProviderDefaults
}

// NewProviderReconciler creates a new provider
// reconciler.
func NewProviderReconciler(
	c client.Client,
	scheme *runtime.Scheme,
	defaults ProviderDefaults,
) *ProviderReconciler {
	return &ProviderReconciler{
		Client:   c,
		Scheme:   scheme,
		Defaults: defaults,
	}
}

// Reconcile handles the reconciliation loop for
// ModelDeployments assigned to this provider.
func (r *ProviderReconciler) Reconcile(
	ctx context.Context,
	req ctrl.Request,
) (ctrl.Result, error) {
	logger := log.FromContext(ctx)

	// Fetch the AIRunway ModelDeployment.
	var md airunwayv1alpha1.ModelDeployment
	if err := r.Get(
		ctx, req.NamespacedName, &md,
	); err != nil {
		return ctrl.Result{},
			client.IgnoreNotFound(err)
	}

	// Only process if this provider is selected.
	if md.Status.Provider == nil ||
		md.Status.Provider.Name != ProviderName {
		return ctrl.Result{}, nil
	}

	logger.Info(
		"Reconciling ModelDeployment",
		"name", md.Name,
		"namespace", md.Namespace)

	// Handle deletion.
	if !md.DeletionTimestamp.IsZero() {
		if controllerutil.ContainsFinalizer(
			&md, FinalizerName,
		) {
			controllerutil.RemoveFinalizer(
				&md, FinalizerName)
			if err := r.Update(
				ctx, &md,
			); err != nil {
				return ctrl.Result{}, err
			}
		}
		return ctrl.Result{}, nil
	}

	// Add finalizer if needed.
	if !controllerutil.ContainsFinalizer(
		&md, FinalizerName,
	) {
		controllerutil.AddFinalizer(
			&md, FinalizerName)
		if err := r.Update(
			ctx, &md,
		); err != nil {
			return ctrl.Result{}, err
		}
	}

	// Parse provider overrides.
	overrides, err := parseOverrides(&md)
	if err != nil {
		r.setProviderIncompatible(
			&md, err.Error())
		return ctrl.Result{},
			r.Status().Update(ctx, &md)
	}

	// Apply cluster-wide defaults for any settings the
	// ModelDeployment did not override.
	applyDefaults(overrides, r.Defaults)

	// The cleanroom ModelRegistration requires a storage
	// account and managed identity; the in-cluster provider
	// cannot provision them, so they must come from the
	// overrides or the provider defaults.
	if overrides.StorageAccountID == "" ||
		overrides.ManagedIdentityID == "" {
		r.setProviderIncompatible(&md,
			"storageAccountId and managedIdentityId are "+
				"required (set them in "+
				"provider.overrides or configure "+
				"provider defaults)")
		return ctrl.Result{},
			r.Status().Update(ctx, &md)
	}

	// Validate engine compatibility.
	if err := r.validateCompatibility(
		&md,
	); err != nil {
		r.setProviderIncompatible(
			&md, err.Error())
		return ctrl.Result{},
			r.Status().Update(ctx, &md)
	}

	// Set ProviderCompatible = True.
	meta.SetStatusCondition(
		&md.Status.Conditions,
		metav1.Condition{
			Type:   airunwayv1alpha1.ConditionTypeProviderCompatible,
			Status: metav1.ConditionTrue,
			Reason: "Compatible",
			Message: "Configuration is compatible " +
				"with accr-conf-inferencing",
		})

	// Validate the Environment exists and is Ready.
	var env cleanroomv1alpha1.Environment
	if err := r.Get(ctx, types.NamespacedName{
		Name:      overrides.EnvironmentRef,
		Namespace: md.Namespace,
	}, &env); err != nil {
		if errors.IsNotFound(err) {
			r.setProviderIncompatible(&md,
				fmt.Sprintf(
					"Environment %q not found",
					overrides.EnvironmentRef))
			return ctrl.Result{},
				r.Status().Update(ctx, &md)
		}
		return ctrl.Result{}, err
	}
	if env.Status.Phase !=
		cleanroomv1alpha1.EnvironmentPhaseReady {
		logger.Info(
			"Environment not yet Ready, requeueing",
			"envRef", overrides.EnvironmentRef,
			"phase", env.Status.Phase)
		md.Status.Phase =
			airunwayv1alpha1.DeploymentPhaseDeploying
		md.Status.Message =
			fmt.Sprintf(
				"Waiting for Environment %q "+
					"(phase: %s)",
				overrides.EnvironmentRef,
				env.Status.Phase)
		if err := r.Status().Update(
			ctx, &md,
		); err != nil {
			return ctrl.Result{}, err
		}
		return ctrl.Result{
			RequeueAfter: RequeueInterval,
		}, nil
	}

	// Step 1: Ensure cleanroom ModelRegistration.
	mrResult, err := r.ensureModelRegistration(
		ctx, &md, overrides)
	if err != nil {
		return ctrl.Result{}, err
	}
	if mrResult != nil {
		return *mrResult, nil
	}

	// Step 2: Ensure cleanroom ModelDeployment.
	mdResult, err := r.ensureModelDeployment(
		ctx, &md)
	if err != nil {
		return ctrl.Result{}, err
	}
	if mdResult != nil {
		return *mdResult, nil
	}

	return ctrl.Result{
		RequeueAfter: RequeueInterval,
	}, nil
}

func (r *ProviderReconciler) ensureModelRegistration(
	ctx context.Context,
	md *airunwayv1alpha1.ModelDeployment,
	overrides *ProviderOverrides,
) (*ctrl.Result, error) {
	logger := log.FromContext(ctx)

	desired := BuildModelRegistration(md, overrides)
	if err := controllerutil.SetControllerReference(
		md, desired, r.Scheme,
	); err != nil {
		return nil, fmt.Errorf(
			"setting owner reference on MR: %w", err)
	}

	var existing cleanroomv1alpha1.ModelRegistration
	err := r.Get(ctx, types.NamespacedName{
		Name:      desired.Name,
		Namespace: desired.Namespace,
	}, &existing)

	if errors.IsNotFound(err) {
		logger.Info(
			"Creating ModelRegistration",
			"name", desired.Name)
		if createErr := r.Create(
			ctx, desired,
		); createErr != nil {
			return nil, fmt.Errorf(
				"creating ModelRegistration: %w",
				createErr)
		}
		md.Status.Phase =
			airunwayv1alpha1.DeploymentPhaseDeploying
		md.Status.Message =
			"ModelRegistration created"
		meta.SetStatusCondition(
			&md.Status.Conditions,
			metav1.Condition{
				Type:    airunwayv1alpha1.ConditionTypeResourceCreated,
				Status:  metav1.ConditionTrue,
				Reason:  "ModelRegistrationCreated",
				Message: "Cleanroom ModelRegistration created",
			})
		if statusErr := r.Status().Update(
			ctx, md,
		); statusErr != nil {
			return nil, statusErr
		}
		result := ctrl.Result{
			RequeueAfter: RequeueInterval,
		}
		return &result, nil
	}
	if err != nil {
		return nil, err
	}

	// Wait for ModelRegistration to reach Ready.
	if existing.Status.Phase !=
		cleanroomv1alpha1.ModelRegistrationPhaseReady {
		logger.Info(
			"ModelRegistration not Ready, requeueing",
			"phase", existing.Status.Phase)
		md.Status.Phase =
			airunwayv1alpha1.DeploymentPhaseDeploying
		md.Status.Message = fmt.Sprintf(
			"Waiting for ModelRegistration "+
				"(phase: %s)",
			existing.Status.Phase)
		if statusErr := r.Status().Update(
			ctx, md,
		); statusErr != nil {
			return nil, statusErr
		}

		if existing.Status.Phase ==
			cleanroomv1alpha1.ModelRegistrationPhaseFailed {
			md.Status.Phase =
				airunwayv1alpha1.DeploymentPhaseFailed
			md.Status.Message = fmt.Sprintf(
				"ModelRegistration failed: %s",
				existing.Status.Message)
			if statusErr := r.Status().Update(
				ctx, md,
			); statusErr != nil {
				return nil, statusErr
			}
			result := ctrl.Result{}
			return &result, nil
		}

		result := ctrl.Result{
			RequeueAfter: RequeueInterval,
		}
		return &result, nil
	}

	return nil, nil
}

func (r *ProviderReconciler) ensureModelDeployment(
	ctx context.Context,
	md *airunwayv1alpha1.ModelDeployment,
) (*ctrl.Result, error) {
	logger := log.FromContext(ctx)

	desired := BuildModelDeployment(md)
	// Do NOT set the AIRunway ModelDeployment as the controller
	// owner of the cleanroom ModelDeployment. The cleanroom
	// operator claims that controller slot to make the cleanroom
	// ModelRegistration the owner of the ModelDeployment
	// (MR-delete cascades to MD). Cascade deletion still works
	// through the chain: AIRunway MD (controller) -> cleanroom
	// ModelRegistration (controller) -> cleanroom ModelDeployment.

	var existing cleanroomv1alpha1.ModelDeployment
	err := r.Get(ctx, types.NamespacedName{
		Name:      desired.Name,
		Namespace: desired.Namespace,
	}, &existing)

	if errors.IsNotFound(err) {
		logger.Info(
			"Creating ModelDeployment",
			"name", desired.Name)
		if createErr := r.Create(
			ctx, desired,
		); createErr != nil {
			return nil, fmt.Errorf(
				"creating ModelDeployment: %w",
				createErr)
		}
		md.Status.Phase =
			airunwayv1alpha1.DeploymentPhaseDeploying
		md.Status.Message =
			"ModelDeployment created"
		if statusErr := r.Status().Update(
			ctx, md,
		); statusErr != nil {
			return nil, statusErr
		}
		result := ctrl.Result{
			RequeueAfter: RequeueInterval,
		}
		return &result, nil
	}
	if err != nil {
		return nil, err
	}

	// Sync status from cleanroom MD → AIRunway MD.
	phase, message, endpoint := TranslateStatus(
		&existing)
	md.Status.Phase = phase
	md.Status.Message = message
	md.Status.Endpoint = endpoint
	md.Status.Provider.ResourceName = existing.Name
	md.Status.Provider.ResourceKind = "ModelDeployment"

	if phase == airunwayv1alpha1.DeploymentPhaseRunning {
		meta.SetStatusCondition(
			&md.Status.Conditions,
			metav1.Condition{
				Type:    airunwayv1alpha1.ConditionTypeReady,
				Status:  metav1.ConditionTrue,
				Reason:  "Ready",
				Message: "Model endpoint is ready",
			})
	}

	if statusErr := r.Status().Update(
		ctx, md,
	); statusErr != nil {
		return nil, statusErr
	}

	if phase != airunwayv1alpha1.DeploymentPhaseRunning &&
		phase != airunwayv1alpha1.DeploymentPhaseFailed {
		result := ctrl.Result{
			RequeueAfter: RequeueInterval,
		}
		return &result, nil
	}

	logger.Info("Status synced",
		"phase", phase,
		"endpoint", md.Status.Endpoint)
	return nil, nil
}

func (r *ProviderReconciler) validateCompatibility(
	md *airunwayv1alpha1.ModelDeployment,
) error {
	engine := md.ResolvedEngineType()
	if engine != airunwayv1alpha1.EngineTypeVLLM &&
		engine != airunwayv1alpha1.EngineTypeLlamaCpp &&
		engine != "" {
		return fmt.Errorf(
			"accr-conf-inferencing does not "+
				"support %s engine", engine)
	}

	if md.ResolvedServingMode() !=
		airunwayv1alpha1.ServingModeAggregated {
		return fmt.Errorf(
			"accr-conf-inferencing does not " +
				"support disaggregated mode")
	}

	if md.Spec.Gateway != nil &&
		md.Spec.Gateway.Enabled != nil &&
		*md.Spec.Gateway.Enabled {
		return fmt.Errorf(
			"accr-conf-inferencing requires " +
				"gateway.enabled: false " +
				"(TLS terminates in TEE)")
	}

	return nil
}

func (r *ProviderReconciler) setProviderIncompatible(
	md *airunwayv1alpha1.ModelDeployment,
	message string,
) {
	meta.SetStatusCondition(
		&md.Status.Conditions,
		metav1.Condition{
			Type:    airunwayv1alpha1.ConditionTypeProviderCompatible,
			Status:  metav1.ConditionFalse,
			Reason:  "Incompatible",
			Message: message,
		})
	md.Status.Phase =
		airunwayv1alpha1.DeploymentPhaseFailed
	md.Status.Message = message
}

// SetupWithManager registers the controller with the
// manager.
func (r *ProviderReconciler) SetupWithManager(
	mgr ctrl.Manager,
) error {
	return ctrl.NewControllerManagedBy(mgr).
		For(&airunwayv1alpha1.ModelDeployment{}).
		Owns(&cleanroomv1alpha1.ModelRegistration{}).
		Owns(&cleanroomv1alpha1.ModelDeployment{}).
		Complete(r)
}
