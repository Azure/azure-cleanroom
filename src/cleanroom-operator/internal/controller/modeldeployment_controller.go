package controller

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"sync"
	"time"

	corev1 "k8s.io/api/core/v1"
	apierrors "k8s.io/apimachinery/pkg/api/errors"
	"k8s.io/apimachinery/pkg/api/meta"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/runtime"
	"k8s.io/apimachinery/pkg/types"
	"k8s.io/client-go/tools/record"
	ctrl "sigs.k8s.io/controller-runtime"
	ctrlclient "sigs.k8s.io/controller-runtime/pkg/client"
	"sigs.k8s.io/controller-runtime/pkg/controller/controllerutil"
	ctrllog "sigs.k8s.io/controller-runtime/pkg/log"

	v1alpha1 "github.com/Azure/azure-cleanroom/cleanroom-operator/api/v1alpha1"
	"github.com/Azure/azure-cleanroom/cleanroom-operator/client"
)

const (
	mdRequeueDelay      = 15 * time.Second
	mdDeploymentTimeout = 30 * time.Minute
)

// ModelDeploymentReconciler reconciles
// ModelDeployment objects.
type ModelDeploymentReconciler struct {
	ctrlclient.Client
	Scheme                   *runtime.Scheme
	Recorder                 record.EventRecorder
	CgsClient                *client.CgsClient
	InferencingClientFactory *client.InferencingClientFactory

	// emittedEvents tracks event messages already
	// emitted per MD to avoid redundant emissions
	// that exhaust the K8s event recorder's spam
	// filter (burst=25 per involvedObject).
	// Key: MD UID, Value: map[message]bool.
	emittedEvents sync.Map
}

// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=modeldeployments,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=modeldeployments/status,verbs=get;update;patch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=modeldeployments/finalizers,verbs=update
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=modelregistrations,verbs=get;list;watch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=environments,verbs=get;list;watch;update;patch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=clusters,verbs=get;list;watch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=ccfusers,verbs=get;list;watch

// Reconcile handles the reconciliation loop for
// ModelDeployment.
func (r *ModelDeploymentReconciler) Reconcile(
	ctx context.Context,
	req ctrl.Request,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	var md v1alpha1.ModelDeployment
	if err := r.Get(
		ctx, req.NamespacedName, &md,
	); err != nil {
		if apierrors.IsNotFound(err) {
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"fetching ModelDeployment: %w",
			err,
		)
	}

	log.Info("Reconciling ModelDeployment",
		"name", md.Name,
		"phase", md.Status.Phase)

	ctx = resolveTraceContext(
		ctx, &md, md.Status.TraceParent,
	)
	ctx, span := startSpan(
		ctx, "ModelDeployment", "Reconcile",
		md.Name,
	)
	defer span.End()

	// Route based on phase.
	switch md.Status.Phase {
	case "",
		v1alpha1.ModelDeploymentPhasePending:
		return r.reconcileDeploying(ctx, &md)

	case v1alpha1.ModelDeploymentPhaseDeploying:
		return r.reconcileDeploying(ctx, &md)

	case v1alpha1.ModelDeploymentPhaseReady:
		if md.Annotations[reconcileRequestedAtAn] != "" &&
			md.Annotations[reconcileRequestedAtAn] !=
				md.Status.LastHandledReconcileAt {
			return r.mdHandleReconcile(ctx, &md)
		}
		return ctrl.Result{}, nil

	case v1alpha1.ModelDeploymentPhaseFailed:
		if md.Annotations[reconcileRequestedAtAn] != "" &&
			md.Annotations[reconcileRequestedAtAn] !=
				md.Status.LastHandledReconcileAt {
			return r.mdHandleReconcile(ctx, &md)
		}
		if md.Annotations[retryAnnotation] != "" {
			delete(md.Annotations, retryAnnotation)
			if err := r.Update(
				ctx, &md,
			); err != nil {
				return ctrl.Result{}, fmt.Errorf(
					"clearing retry annotation: %w",
					err,
				)
			}
			md.Status.Conditions = nil
			return r.mdStartDeploying(ctx, &md)
		}
		return ctrl.Result{}, nil
	}

	return ctrl.Result{}, nil
}

// mdStartDeploying transitions to Deploying phase.
func (r *ModelDeploymentReconciler) mdStartDeploying(
	ctx context.Context,
	md *v1alpha1.ModelDeployment,
) (ctrl.Result, error) {
	md.Status.Phase =
		v1alpha1.ModelDeploymentPhaseDeploying
	md.Status.ObservedGeneration = md.Generation
	md.Status.TraceParent,
		md.Status.LastOperationTraceId =
		saveTrace(ctx)
	if err := r.Status().Update(ctx, md); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating phase to Deploying: %w", err,
		)
	}
	return ctrl.Result{Requeue: true}, nil
}

// reconcileDeploying executes the condition-tracked
// step machine for model deployment.
func (r *ModelDeploymentReconciler) reconcileDeploying(
	ctx context.Context,
	md *v1alpha1.ModelDeployment,
) (ctrl.Result, error) {
	// Set phase on first entry.
	if md.Status.Phase == "" ||
		md.Status.Phase ==
			v1alpha1.ModelDeploymentPhasePending {
		return r.mdStartDeploying(ctx, md)
	}

	// Step 1: Ensure flex node is enabled on the
	// Environment (only when NodeProvisioningMode is
	// set).
	if md.Spec.NodeProvisioningMode != "" &&
		!r.mdConditionIsTrue(md,
			v1alpha1.ConditionTypeMDFlexNodeReady) {
		return r.stepEnsureFlexNode(ctx, md)
	}

	// Step 2: Check parent ModelRegistration is Ready.
	if !r.mdConditionIsTrue(md,
		v1alpha1.ConditionTypeMDDeploymentReady) {
		return r.stepCheckModelRegistration(ctx, md)
	}

	// Step 3: Deploy endpoint via inferencing agent.
	if !r.mdConditionIsTrue(md,
		v1alpha1.ConditionTypeMDEndpointDeployed) {
		return r.stepDeployEndpoint(ctx, md)
	}

	// All steps complete — mark Ready.
	r.mdClearEmittedEvents(md)
	md.Status.Phase =
		v1alpha1.ModelDeploymentPhaseReady
	md.Status.Message = ""
	md.Status.TraceParent = ""
	if err := r.Status().Update(ctx, md); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating phase to Ready: %w", err,
		)
	}

	r.mdEvent(md, corev1.EventTypeNormal,
		"Ready",
		"ModelDeployment is ready")

	return ctrl.Result{}, nil
}

// stepEnsureFlexNode patches the Environment to enable
// flex nodes based on the NodeProvisioningMode. "auto"
// sets the flex node mode to auto (Karpenter). "manual"
// uses the default manual (explicit) behaviour.
func (r *ModelDeploymentReconciler) stepEnsureFlexNode(
	ctx context.Context,
	md *v1alpha1.ModelDeployment,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "ModelDeployment",
		"EnsureFlexNode", md.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)

	// Look up the parent ModelRegistration to get its
	// EnvironmentRef.
	var mr v1alpha1.ModelRegistration
	if err := r.Get(ctx, types.NamespacedName{
		Name:      md.Spec.ModelRegistrationRef,
		Namespace: md.Namespace,
	}, &mr); err != nil {
		if apierrors.IsNotFound(err) {
			return ctrl.Result{}, r.mdSetFailed(
				ctx, md,
				"ModelRegistrationNotFound",
				fmt.Errorf(
					"ModelRegistration %q not found",
					md.Spec.ModelRegistrationRef,
				),
			)
		}
		return ctrl.Result{}, fmt.Errorf(
			"fetching ModelRegistration %s: %w",
			md.Spec.ModelRegistrationRef, err,
		)
	}

	log.Info("Ensuring flex node on Environment",
		"envRef", mr.Spec.EnvironmentRef,
		"mode", md.Spec.NodeProvisioningMode)

	var env v1alpha1.Environment
	if err := r.Get(ctx, types.NamespacedName{
		Name:      mr.Spec.EnvironmentRef,
		Namespace: md.Namespace,
	}, &env); err != nil {
		if sErr := r.mdSetFailed(ctx, md,
			"EnvironmentNotFound", fmt.Errorf(
				"fetching Environment %s: %w",
				mr.Spec.EnvironmentRef, err,
			)); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{}, nil
	}

	// Map NodeProvisioningMode to FlexNodeMode.
	var flexMode v1alpha1.FlexNodeMode
	switch md.Spec.NodeProvisioningMode {
	case v1alpha1.NodeProvisioningModeAuto:
		flexMode = v1alpha1.FlexNodeModeAuto
	default:
		flexMode = v1alpha1.FlexNodeModeManual
	}

	needsPatch := false
	if env.Spec.Profiles == nil {
		env.Spec.Profiles =
			&v1alpha1.EnvironmentProfiles{}
	}

	if env.Spec.Profiles.FlexNode == nil {
		env.Spec.Profiles.FlexNode =
			&v1alpha1.FlexNodeProfileSpec{
				Enabled: true,
				Mode:    flexMode,
			}
		needsPatch = true
	} else {
		if !env.Spec.Profiles.FlexNode.Enabled {
			env.Spec.Profiles.FlexNode.Enabled = true
			needsPatch = true
		}
		if env.Spec.Profiles.FlexNode.Mode !=
			flexMode {
			env.Spec.Profiles.FlexNode.Mode = flexMode
			needsPatch = true
		}
	}

	if mr.Spec.VMSize != "" &&
		(env.Spec.Profiles.FlexNode.VmSize == "" ||
			env.Spec.Profiles.FlexNode.VmSize !=
				mr.Spec.VMSize) {
		env.Spec.Profiles.FlexNode.VmSize =
			mr.Spec.VMSize
		needsPatch = true
	}

	if needsPatch {
		if err := r.Update(ctx, &env); err != nil {
			if apierrors.IsInvalid(err) ||
				apierrors.IsForbidden(err) {
				if sErr := r.mdSetFailed(
					ctx, md,
					"EnvironmentUpdateRejected",
					fmt.Errorf(
						"patching Environment "+
							"flex node: %w",
						err,
					),
				); sErr != nil {
					return ctrl.Result{}, sErr
				}
				return ctrl.Result{}, nil
			}
			return ctrl.Result{}, fmt.Errorf(
				"patching Environment flex node: %w",
				err,
			)
		}
		log.Info(
			"Patched Environment to enable flex node",
			"mode", flexMode,
		)
	}

	r.mdSetConditionTrue(md,
		v1alpha1.ConditionTypeMDFlexNodeReady,
		"Enabled",
		fmt.Sprintf(
			"Flex node enabled on Environment "+
				"(mode: %s)", flexMode,
		))
	if err := r.Status().Update(ctx, md); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after flex node: %w",
			err,
		)
	}

	r.mdEvent(md, corev1.EventTypeNormal,
		"FlexNodeEnabled",
		fmt.Sprintf(
			"Flex node enabled on Environment "+
				"(mode: %s)", flexMode,
		))

	return ctrl.Result{Requeue: true}, nil
}

// stepCheckModelRegistration verifies the parent
// ModelRegistration is in Ready phase.
func (r *ModelDeploymentReconciler) stepCheckModelRegistration(
	ctx context.Context,
	md *v1alpha1.ModelDeployment,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "ModelDeployment",
		"CheckModelRegistration", md.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	log.Info("Checking parent ModelRegistration",
		"ref", md.Spec.ModelRegistrationRef)

	var mr v1alpha1.ModelRegistration
	if err := r.Get(ctx, types.NamespacedName{
		Name:      md.Spec.ModelRegistrationRef,
		Namespace: md.Namespace,
	}, &mr); err != nil {
		if apierrors.IsNotFound(err) {
			return ctrl.Result{}, r.mdSetFailed(
				ctx, md,
				"ModelRegistrationNotFound",
				fmt.Errorf(
					"ModelRegistration %q not found",
					md.Spec.ModelRegistrationRef,
				),
			)
		}
		return ctrl.Result{}, fmt.Errorf(
			"fetching ModelRegistration %s: %w",
			md.Spec.ModelRegistrationRef, err,
		)
	}

	// Set ModelRegistration as the owner of this MD
	// so deleting the MR cascades to all its MDs.
	if !hasOwnerReference(md, &mr) {
		if err := controllerutil.SetControllerReference(
			&mr, md, r.Scheme,
		); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"setting owner reference: %w", err,
			)
		}
		if err := r.Update(ctx, md); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"updating owner reference: %w", err,
			)
		}
	}

	if mr.Status.Phase ==
		v1alpha1.ModelRegistrationPhaseFailed {
		return ctrl.Result{}, r.mdSetFailed(
			ctx, md,
			"ModelRegistrationFailed",
			fmt.Errorf(
				"ModelRegistration %q is in Failed phase",
				md.Spec.ModelRegistrationRef,
			),
		)
	}

	if mr.Status.Phase !=
		v1alpha1.ModelRegistrationPhaseReady {
		log.Info("ModelRegistration not yet Ready, "+
			"requeueing",
			"phase", mr.Status.Phase)
		r.mdEvent(md, corev1.EventTypeNormal,
			"WaitingForModelRegistration",
			fmt.Sprintf(
				"Waiting for ModelRegistration %q "+
					"(phase: %s)",
				md.Spec.ModelRegistrationRef,
				mr.Status.Phase,
			),
		)
		return ctrl.Result{
			RequeueAfter: mdRequeueDelay,
		}, nil
	}

	r.mdSetConditionTrue(md,
		v1alpha1.ConditionTypeMDDeploymentReady,
		"Ready",
		fmt.Sprintf("ModelRegistration %q is Ready",
			md.Spec.ModelRegistrationRef))
	if err := r.Status().Update(ctx, md); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating ModelRegistrationReady "+
				"condition: %w", err,
		)
	}

	r.mdEvent(md, corev1.EventTypeNormal,
		"ModelRegistrationReady",
		fmt.Sprintf("ModelRegistration %q is Ready",
			md.Spec.ModelRegistrationRef))

	return ctrl.Result{Requeue: true}, nil
}

// stepDeployEndpoint calls the inferencing agent to
// deploy the model endpoint.
func (r *ModelDeploymentReconciler) stepDeployEndpoint(
	ctx context.Context,
	md *v1alpha1.ModelDeployment,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "ModelDeployment",
		"DeployEndpoint", md.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	log.Info("Deploying model endpoint",
		"ref", md.Spec.ModelRegistrationRef)

	// Fetch parent ModelRegistration for model info.
	var mr v1alpha1.ModelRegistration
	if err := r.Get(ctx, types.NamespacedName{
		Name:      md.Spec.ModelRegistrationRef,
		Namespace: md.Namespace,
	}, &mr); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"fetching ModelRegistration %s: %w",
			md.Spec.ModelRegistrationRef, err,
		)
	}

	// Resolve inferencing agent endpoint from Cluster
	// status.
	clusterName, infraType, providerConfig, agentEndpoint, err :=
		r.resolveInferencingEndpoint(ctx, &mr)
	if err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"resolving inferencing endpoint: %w", err,
		)
	}

	// Create a per-cluster inferencing client via
	// the API server proxy.
	infClient, err :=
		r.InferencingClientFactory.ClientForCluster(
			ctx, clusterName, infraType,
			providerConfig,
			agentEndpoint,
		)
	if err != nil {
		return ctrl.Result{}, r.mdSetFailed(
			ctx, md,
			"EndpointDeployFailed",
			fmt.Errorf(
				"creating inferencing client: %w",
				err,
			),
		)
	}

	// Get publisher's governance client endpoint for
	// access token.
	cgsEndpoint, err :=
		r.resolveMDUserCgsEndpoint(ctx, &mr)
	if err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"resolving CGS endpoint: %w", err,
		)
	}

	// Get access token from governance client.
	token, err := r.CgsClient.GetAccessToken(
		ctx, cgsEndpoint,
	)
	if err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"getting access token: %w", err,
		)
	}

	// Ensure we have a correlation ID (persisted for
	// cross-restart resume).
	if md.Status.CorrelationID == "" {
		md.Status.CorrelationID = generateRequestID()
		if err := r.Status().Update(
			ctx, md,
		); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"persisting correlation ID: %w", err,
			)
		}
		return ctrl.Result{Requeue: true}, nil
	}

	correlationID := md.Status.CorrelationID
	clientRequestID := generateRequestID()

	// Check if deployment was already submitted by
	// polling status first.
	status, err := infClient.GetDeploymentStatus(
		ctx, token, md.Name,
		correlationID, clientRequestID,
	)
	if err != nil &&
		!errors.Is(err, client.ErrDeploymentNotFound) {
		// Non-404 error (DNS, connection, 5xx, etc.)
		// is fatal.
		return ctrl.Result{}, r.mdSetFailed(
			ctx, md,
			"EndpointDeployFailed",
			fmt.Errorf(
				"checking deployment status: %w", err,
			),
		)
	}
	if err != nil {
		// 404 — deployment doesn't exist yet, submit.
		log.Info("Deployment not found, submitting")

		body := r.buildDeploymentBody(md, &mr)
		submitErr :=
			infClient.SubmitDeployment(
				ctx, token,
				correlationID, generateRequestID(),
				body,
			)
		if submitErr != nil {
			return ctrl.Result{}, r.mdSetFailed(
				ctx, md,
				"EndpointDeployFailed",
				fmt.Errorf(
					"submitting deployment: %w",
					submitErr,
				),
			)
		}

		r.mdSetConditionTrue(md,
			v1alpha1.ConditionTypeMDEndpointSubmitted,
			"Submitted",
			fmt.Sprintf(
				"Deployment submitted to %s",
				agentEndpoint,
			))
		if err := r.Status().Update(
			ctx, md,
		); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"updating EndpointSubmitted "+
					"condition: %w", err,
			)
		}

		r.mdEvent(md, corev1.EventTypeNormal,
			"DeploymentSubmitted",
			fmt.Sprintf(
				"Submitted deployment to %s",
				agentEndpoint,
			))

		return ctrl.Result{
			RequeueAfter: mdRequeueDelay,
		}, nil
	}

	// The inferencing service exists (non-404). Set
	// InferenceServiceCreated if not already set.
	if !r.mdConditionIsTrue(md,
		v1alpha1.ConditionTypeMDInferenceServiceCreated) {
		r.mdSetConditionTrue(md,
			v1alpha1.ConditionTypeMDInferenceServiceCreated,
			"Created",
			"Inferencing service created, "+
				"waiting for readiness")
		if err := r.Status().Update(
			ctx, md,
		); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"updating "+
					"InferenceServiceCreated "+
					"condition: %w", err,
			)
		}
	}

	// Check if deployment is ready.
	if !status.IsReady() {
		log.Info("Deployment not yet ready, "+
			"requeueing",
			"url", status.URL)

		// Surface pod-level errors via a condition.
		if errMsg :=
			status.FirstContainerError(); errMsg != "" {
			r.mdSetConditionFalse(md,
				v1alpha1.ConditionTypeMDDeploymentHealthy,
				"ContainerError",
				errMsg)
			if err := r.Status().Update(
				ctx, md,
			); err != nil {
				return ctrl.Result{}, fmt.Errorf(
					"updating DeploymentHealthy "+
						"condition: %w", err,
				)
			}
			r.mdEvent(md, corev1.EventTypeWarning,
				"ContainerError", errMsg)
		}

		// Emit progress events only for containers
		// whose state has changed since the last
		// reconcile. This avoids exhausting the K8s
		// event recorder's spam filter (burst=25).
		for _, msg := range status.ContainerStateMessages() {
			if !r.mdEventEmitted(md, msg) {
				r.mdEvent(md, corev1.EventTypeNormal,
					"ContainerState", msg)
				r.mdTrackEvent(md, msg)
			}
		}

		// Time out the deployment if it has been in
		// Deploying phase for too long.
		if r.mdDeploymentTimedOut(md) {
			reason := "Deployment timed out after " +
				mdDeploymentTimeout.String()
			if errMsg :=
				status.FirstContainerError(); errMsg != "" {
				reason += ": " + errMsg
			}
			return ctrl.Result{}, r.mdSetFailed(
				ctx, md,
				"DeploymentTimeout",
				fmt.Errorf("%s", reason),
			)
		}

		waitMsg := "Waiting for inferencing " +
			"service to be ready"
		if !r.mdEventEmitted(md, waitMsg) {
			r.mdEvent(md, corev1.EventTypeNormal,
				"WaitingForDeployment", waitMsg)
			r.mdTrackEvent(md, waitMsg)
		}
		return ctrl.Result{
			RequeueAfter: mdRequeueDelay,
		}, nil
	}

	// Deployment is ready — store the cluster's
	// KServe endpoint as the service endpoint.
	md.Status.ServiceEndpoint = agentEndpoint + "/ai"
	log.Info("Endpoint deployed",
		"url", md.Status.ServiceEndpoint)

	// Resolve the CA certificate from the
	// GovernanceContract.
	caCert, err := r.resolveServiceCaCert(ctx, &mr)
	if err != nil {
		log.Error(err,
			"Failed to resolve service CA cert")
	} else if caCert != "" {
		md.Status.ServiceCaCert = caCert
	}

	r.mdSetConditionTrue(md,
		v1alpha1.ConditionTypeMDEndpointDeployed,
		"Deployed",
		"Endpoint deployed via inferencing agent")
	if err := r.Status().Update(ctx, md); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating EndpointDeployed "+
				"condition: %w", err,
		)
	}

	r.mdEvent(md, corev1.EventTypeNormal,
		"EndpointDeployed",
		fmt.Sprintf(
			"Model endpoint deployed at %s",
			md.Status.ServiceEndpoint,
		))

	return ctrl.Result{Requeue: true}, nil
}

// resolveInferencingEndpoint looks up the Cluster CR for
// the ModelRegistration's environment and returns the
// cluster name, infra type, and KServe inferencing agent
// endpoint.
func (r *ModelDeploymentReconciler) resolveInferencingEndpoint(
	ctx context.Context,
	mr *v1alpha1.ModelRegistration,
) (clusterName string, infraType string,
	providerConfig *runtime.RawExtension,
	endpoint string, err error) {
	clusterName = mr.Spec.EnvironmentRef + "-cluster"

	var cluster v1alpha1.Cluster
	if err := r.Get(ctx, types.NamespacedName{
		Name:      clusterName,
		Namespace: mr.Namespace,
	}, &cluster); err != nil {
		return "", "", nil, "", fmt.Errorf(
			"fetching Cluster %s: %w",
			clusterName, err,
		)
	}

	if cluster.Status.InferencingWorkloadProfile == nil ||
		cluster.Status.InferencingWorkloadProfile.KServeProfile == nil {
		return "", "", nil, "", fmt.Errorf(
			"Cluster %s has no KServe inferencing "+
				"profile",
			clusterName,
		)
	}

	endpoint =
		cluster.Status.InferencingWorkloadProfile.
			KServeProfile.Endpoint
	if endpoint == "" {
		return "", "", nil, "", fmt.Errorf(
			"Cluster %s KServe endpoint is empty",
			clusterName,
		)
	}

	return clusterName, cluster.Spec.InfraType,
		cluster.Spec.ProviderConfig,
		endpoint, nil
}

// resolveMDUserCgsEndpoint looks up the publisher
// CcfUser's governance client endpoint for access token
// retrieval.
func (r *ModelDeploymentReconciler) resolveMDUserCgsEndpoint(
	ctx context.Context,
	mr *v1alpha1.ModelRegistration,
) (string, error) {
	userName := mr.Name + "-publisher"

	var user v1alpha1.CcfUser
	if err := r.Get(ctx, types.NamespacedName{
		Name:      userName,
		Namespace: mr.Namespace,
	}, &user); err != nil {
		return "", fmt.Errorf(
			"fetching CcfUser %s: %w",
			userName, err,
		)
	}

	if user.Status.GovernanceClientEndpoint == "" {
		return "", fmt.Errorf(
			"CcfUser %s has no governance client "+
				"endpoint",
			userName,
		)
	}

	return user.Status.GovernanceClientEndpoint, nil
}

// resolveServiceCaCert looks up the GovernanceContract
// owned by the environment's WorkloadGovernance and
// returns its CA certificate from status.
func (r *ModelDeploymentReconciler) resolveServiceCaCert(
	ctx context.Context,
	mr *v1alpha1.ModelRegistration,
) (string, error) {
	gcName := mr.Spec.EnvironmentRef +
		"-wg-inferencing-gc"

	var gc v1alpha1.GovernanceContract
	if err := r.Get(ctx, types.NamespacedName{
		Name:      gcName,
		Namespace: mr.Namespace,
	}, &gc); err != nil {
		return "", fmt.Errorf(
			"fetching GovernanceContract %s: %w",
			gcName, err,
		)
	}

	return gc.Status.CaCert, nil
}

// buildDeploymentBody constructs the JSON body for the
// inferencing agent POST /inferenceServices request.
func (r *ModelDeploymentReconciler) buildDeploymentBody(
	md *v1alpha1.ModelDeployment,
	mr *v1alpha1.ModelRegistration,
) map[string]any {
	body := map[string]any{
		"name":    md.Name,
		"modelId": mr.Status.ModelDocId,
	}

	// Build predictor from MDI spec if provided.
	if md.Spec.Predictor != nil {
		predictor := map[string]any{}
		p := md.Spec.Predictor

		if p.MinReplicas != nil {
			predictor["minReplicas"] = *p.MinReplicas
		}
		if p.MaxReplicas != nil {
			predictor["maxReplicas"] = *p.MaxReplicas
		}
		if p.Timeout != nil {
			predictor["timeout"] = *p.Timeout
		}

		if p.Model != nil {
			model := map[string]any{}
			m := p.Model

			if m.ModelFormat != nil {
				model["modelFormat"] = map[string]any{
					"name": m.ModelFormat.Name,
				}
			}
			if m.Runtime != "" {
				model["runtime"] = m.Runtime
			}
			if m.Resources != nil {
				resources := map[string]any{}
				if m.Resources.Requests != nil {
					resources["requests"] =
						m.Resources.Requests
				}
				if m.Resources.Limits != nil {
					resources["limits"] =
						m.Resources.Limits
				}
				model["resources"] = resources
			}
			if len(m.Args) > 0 {
				model["args"] = m.Args
			}
			if len(m.Env) > 0 {
				envList := make(
					[]map[string]string, len(m.Env),
				)
				for i, e := range m.Env {
					envList[i] = map[string]string{
						"name":  e.Name,
						"value": e.Value,
					}
				}
				model["env"] = envList
			}

			predictor["model"] = model
		}

		body["predictor"] = predictor
	}

	return body
}

// generateRequestID produces a random hex string for use
// as correlation or client request IDs.
func generateRequestID() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

// mdHandleReconcile handles the reconcile annotation
// by resetting the phase to Deploying.
func (r *ModelDeploymentReconciler) mdHandleReconcile(
	ctx context.Context,
	md *v1alpha1.ModelDeployment,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "ModelDeployment", "Retry",
		md.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	log.Info("Handling reconcile request")

	requestedAt :=
		md.Annotations[reconcileRequestedAtAn]

	// Clear reconcile annotation.
	delete(md.Annotations, reconcileRequestedAtAn)
	if err := r.Update(ctx, md); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"clearing reconcile annotation: %w", err,
		)
	}

	// Reset to Deploying and store acknowledgment.
	md.Status.Phase =
		v1alpha1.ModelDeploymentPhaseDeploying
	md.Status.Conditions = nil
	md.Status.Message = ""
	md.Status.LastHandledReconcileAt = requestedAt
	md.Status.TraceParent,
		md.Status.LastOperationTraceId =
		saveTrace(ctx)
	if err := r.Status().Update(ctx, md); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"resetting phase to Deploying: %w", err,
		)
	}

	r.mdEvent(md, corev1.EventTypeNormal,
		"ReconcileRequested",
		"Reconciliation requested, retrying")

	return ctrl.Result{Requeue: true}, nil
}

// mdConditionIsTrue checks if a condition is True.
func (r *ModelDeploymentReconciler) mdConditionIsTrue(
	md *v1alpha1.ModelDeployment,
	conditionType string,
) bool {
	return meta.IsStatusConditionTrue(
		md.Status.Conditions, conditionType,
	)
}

// mdSetConditionTrue sets a condition to True.
func (r *ModelDeploymentReconciler) mdSetConditionTrue(
	md *v1alpha1.ModelDeployment,
	conditionType string,
	reason string,
	message string,
) {
	meta.SetStatusCondition(
		&md.Status.Conditions,
		metav1.Condition{
			Type:    conditionType,
			Status:  metav1.ConditionTrue,
			Reason:  reason,
			Message: message,
		},
	)
}

// mdSetConditionFalse sets a condition to False.
func (r *ModelDeploymentReconciler) mdSetConditionFalse(
	md *v1alpha1.ModelDeployment,
	conditionType string,
	reason string,
	message string,
) {
	meta.SetStatusCondition(
		&md.Status.Conditions,
		metav1.Condition{
			Type:   conditionType,
			Status: metav1.ConditionFalse,
			Reason: reason,
			Message: truncateMessage(
				message, maxConditionMessageLen,
			),
		},
	)
}

// mdDeploymentTimedOut checks whether the MDI has been
// in Deploying phase longer than the deployment timeout.
// It uses the InferenceServiceCreated condition's
// LastTransitionTime as the start marker.
func (r *ModelDeploymentReconciler) mdDeploymentTimedOut(
	md *v1alpha1.ModelDeployment,
) bool {
	cond := meta.FindStatusCondition(
		md.Status.Conditions,
		v1alpha1.ConditionTypeMDInferenceServiceCreated,
	)
	if cond == nil {
		return false
	}
	return time.Since(
		cond.LastTransitionTime.Time,
	) > mdDeploymentTimeout
}

// mdSetFailed sets the phase to Failed with a
// condition.
func (r *ModelDeploymentReconciler) mdSetFailed(
	ctx context.Context,
	md *v1alpha1.ModelDeployment,
	reason string,
	err error,
) error {
	r.mdClearEmittedEvents(md)
	log := ctrllog.FromContext(ctx)
	log.Error(err, "Operation failed: "+reason)
	recordContextError(ctx, err)

	message := truncateMessage(
		err.Error(), maxConditionMessageLen,
	)
	md.Status.Phase =
		v1alpha1.ModelDeploymentPhaseFailed
	md.Status.Message = message
	md.Status.TraceParent = ""
	meta.SetStatusCondition(
		&md.Status.Conditions,
		metav1.Condition{
			Type:    "Ready",
			Status:  metav1.ConditionFalse,
			Reason:  reason,
			Message: message,
		},
	)
	if err := r.Status().Update(ctx, md); err != nil {
		return fmt.Errorf(
			"updating failed status: %w", err,
		)
	}
	return nil
}

// mdEvent emits a Kubernetes event for the
// ModelDeployment resource.
func (r *ModelDeploymentReconciler) mdEvent(
	md *v1alpha1.ModelDeployment,
	eventType string,
	reason string,
	message string,
) {
	r.Recorder.Event(
		md, eventType, reason, message,
	)
}

// mdEventEmitted returns true if the given message
// was already emitted for this MDI.
func (r *ModelDeploymentReconciler) mdEventEmitted(
	md *v1alpha1.ModelDeployment,
	message string,
) bool {
	raw, ok := r.emittedEvents.Load(md.UID)
	if !ok {
		return false
	}
	emitted := raw.(map[string]bool)
	return emitted[message]
}

// mdTrackEvent records that the given message has
// been emitted for this MDI.
func (r *ModelDeploymentReconciler) mdTrackEvent(
	md *v1alpha1.ModelDeployment,
	message string,
) {
	raw, _ := r.emittedEvents.LoadOrStore(
		md.UID, make(map[string]bool),
	)
	emitted := raw.(map[string]bool)
	emitted[message] = true
}

// mdClearEmittedEvents removes the emitted-event
// tracking for the given MDI (called when the MDI
// reaches a terminal state).
func (r *ModelDeploymentReconciler) mdClearEmittedEvents(
	md *v1alpha1.ModelDeployment,
) {
	r.emittedEvents.Delete(md.UID)
}

// SetupWithManager registers the controller with the
// manager.
func (r *ModelDeploymentReconciler) SetupWithManager(
	mgr ctrl.Manager,
) error {
	return ctrl.NewControllerManagedBy(mgr).
		For(&v1alpha1.ModelDeployment{}).
		Complete(r)
}
