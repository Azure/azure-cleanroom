package controller

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"
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
)

const (
	envFinalizerName       = "cleanroom.azure.com/env-finalizer"
	envRequeueDelay        = 15 * time.Second
	envStatusSyncInterval  = 60 * time.Second
	reconcileRequestedAtAn = "reconcile.cleanroom.azure.com/requestedAt"
)

// EnvironmentReconciler reconciles Environment objects.
type EnvironmentReconciler struct {
	ctrlclient.Client
	Scheme   *runtime.Scheme
	Recorder record.EventRecorder
}

// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=environments,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=environments/status,verbs=get;update;patch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=environments/finalizers,verbs=update
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=ccfmembers,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=ccfnetworks,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=governanceservices,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=governancecontracts,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=workloadgovernances,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=clusters,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups="",resources=events,verbs=create;patch

// Reconcile handles the reconciliation loop for Environment.
func (r *EnvironmentReconciler) Reconcile(
	ctx context.Context,
	req ctrl.Request,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	var env v1alpha1.Environment
	if err := r.Get(
		ctx, req.NamespacedName, &env,
	); err != nil {
		if apierrors.IsNotFound(err) {
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"fetching Environment: %w", err,
		)
	}

	ctx = resolveTraceContext(
		ctx, &env, env.Status.TraceParent,
	)
	ctx, span := startSpan(
		ctx, "Environment", "Reconcile", env.Name,
	)
	defer span.End()

	// Handle deletion.
	if !env.DeletionTimestamp.IsZero() {
		return r.reconcileDelete(ctx, &env)
	}

	// Ensure finalizer.
	if !controllerutil.ContainsFinalizer(
		&env, envFinalizerName,
	) {
		controllerutil.AddFinalizer(
			&env, envFinalizerName,
		)
		if err := r.Update(ctx, &env); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"adding finalizer: %w", err,
			)
		}
		return ctrl.Result{Requeue: true}, nil
	}

	switch env.Status.Phase {
	case "", v1alpha1.EnvironmentPhasePending:
		return r.reconcileProvision(ctx, &env)

	case v1alpha1.EnvironmentPhaseProvisioning:
		return r.reconcileProvision(ctx, &env)

	case v1alpha1.EnvironmentPhaseReady:
		// Check for spec changes.
		if env.Generation !=
			env.Status.ObservedGeneration {
			return r.reconcileProvision(ctx, &env)
		}
		// Handle reconcile request from Ready state.
		if env.Annotations[reconcileRequestedAtAn] != "" &&
			env.Annotations[reconcileRequestedAtAn] !=
				env.Status.LastHandledReconcileAt {
			return r.propagateRetry(ctx, &env)
		}
		// Re-aggregate status periodically.
		return r.reconcileAggregateStatus(ctx, &env)

	case v1alpha1.EnvironmentPhaseFailed:
		if env.Generation !=
			env.Status.ObservedGeneration {
			return r.reconcileProvision(ctx, &env)
		}
		// Propagate retry when reconcile requested.
		if env.Annotations[reconcileRequestedAtAn] != "" &&
			env.Annotations[reconcileRequestedAtAn] !=
				env.Status.LastHandledReconcileAt {
			return r.propagateRetry(ctx, &env)
		}
		// Re-aggregate status to pick up child changes.
		return r.reconcileAggregateStatus(
			ctx, &env)

	default:
		log.Info("Unknown phase, requeueing",
			"phase", env.Status.Phase)
		return ctrl.Result{
			RequeueAfter: envRequeueDelay,
		}, nil
	}
}

// resolvedDeletionPolicy returns the effective deletion
// policy for the given Environment. If the spec field is set
// it is used directly; otherwise all types default to
// "retain".
func resolvedDeletionPolicy(
	env *v1alpha1.Environment,
) string {
	if env.Spec.DeletionPolicy != "" {
		return env.Spec.DeletionPolicy
	}
	return v1alpha1.DeletionPolicyRetain
}

// syncDeletionPolicy ensures the CcfNetwork and Cluster
// children have the current resolved deletion policy.
func (r *EnvironmentReconciler) syncDeletionPolicy(
	ctx context.Context,
	env *v1alpha1.Environment,
	networkName, clusterName string,
) {
	log := ctrllog.FromContext(ctx)
	policy := resolvedDeletionPolicy(env)

	var network v1alpha1.CcfNetwork
	if err := r.Get(ctx, types.NamespacedName{
		Name:      networkName,
		Namespace: env.Namespace,
	}, &network); err == nil {
		if network.Spec.DeletionPolicy != policy {
			network.Spec.DeletionPolicy = policy
			if err := r.Update(
				ctx, &network,
			); err != nil {
				log.Error(err,
					"Syncing deletionPolicy to "+
						"CcfNetwork")
			}
		}
	}

	var cluster v1alpha1.Cluster
	if err := r.Get(ctx, types.NamespacedName{
		Name:      clusterName,
		Namespace: env.Namespace,
	}, &cluster); err == nil {
		if cluster.Spec.DeletionPolicy != policy {
			cluster.Spec.DeletionPolicy = policy
			if err := r.Update(
				ctx, &cluster,
			); err != nil {
				log.Error(err,
					"Syncing deletionPolicy to "+
						"Cluster")
			}
		}
	}
}

// reconcileProvision ensures all child resources exist and
// aggregates their status.
func (r *EnvironmentReconciler) reconcileProvision(
	ctx context.Context,
	env *v1alpha1.Environment,
) (ctrl.Result, error) {
	// Set Provisioning phase (first entry). Create a
	// span only for this initial transition — subsequent
	// reconciles in Provisioning are polling no-ops.
	if env.Status.Phase !=
		v1alpha1.EnvironmentPhaseProvisioning {
		ctx, span := startSpan(
			ctx, "Environment", "Provision", env.Name,
		)
		defer span.End()

		env.Status.Phase =
			v1alpha1.EnvironmentPhaseProvisioning
		env.Status.ObservedGeneration = env.Generation
		env.Status.TraceParent,
			env.Status.LastOperationTraceId =
			saveTrace(ctx)
		if err := r.Status().Update(ctx, env); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"setting Provisioning phase: %w", err,
			)
		}
		r.envEvent(env, corev1.EventTypeNormal,
			"Provisioning",
			"Environment provisioning started")

		if resolvedDeletionPolicy(env) ==
			v1alpha1.DeletionPolicyDelete &&
			env.Spec.InfraType != "virtual" {
			r.envEvent(env,
				corev1.EventTypeWarning,
				"DeletionPolicyWarning",
				"deletionPolicy is \"delete\" with "+
					"non-virtual infraType; external "+
					"resources will be destroyed on "+
					"deletion")
		}

		return ctrl.Result{Requeue: true}, nil
	}

	// Step 1: Ensure operator CcfMember.
	operatorName := r.childName(env, "operator")
	networkName := r.childName(env, "network")
	if err := r.ensureCcfMember(
		ctx, env, operatorName, networkName,
	); err != nil {
		if isPermanentError(err) {
			if sErr := r.envSetFailed(
				ctx, env,
				"CcfMemberCreateRejected", err,
			); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"ensuring CcfMember: %w", err,
		)
	}

	// Step 1b: Ensure member0 (non-operator) CcfMember.
	member0Name := r.childName(env, "member0")
	if err := r.ensureInitialMember(
		ctx, env, member0Name, networkName,
	); err != nil {
		if isPermanentError(err) {
			if sErr := r.envSetFailed(
				ctx, env,
				"InitialMemberCreateRejected", err,
			); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"ensuring initial CcfMember: %w", err,
		)
	}

	// Step 2: Ensure CcfNetwork (needs member certs).
	networkCreated, err := r.ensureCcfNetwork(
		ctx, env, networkName, operatorName,
		member0Name,
	)
	if err != nil {
		if isPermanentError(err) {
			if sErr := r.envSetFailed(
				ctx, env,
				"CcfNetworkCreateRejected", err,
			); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"ensuring CcfNetwork: %w", err,
		)
	}
	if !networkCreated {
		// Waiting for member certs.
		return ctrl.Result{
			RequeueAfter: envRequeueDelay,
		}, nil
	}

	// Step 2b: Ensure GovernanceService.
	gsName := r.childName(env, "gs")
	if err := r.ensureGovernanceService(
		ctx, env, gsName, networkName,
		member0Name,
	); err != nil {
		if isPermanentError(err) {
			if sErr := r.envSetFailed(
				ctx, env,
				"GovernanceServiceCreateRejected",
				err,
			); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"ensuring GovernanceService: %w", err,
		)
	}

	// Step 3: Ensure WorkloadGovernance for inferencing.
	wgConfigMapName := ""
	if r.hasKServeInferencing(env) {
		wgName := r.childName(env, "wg-inferencing")
		wgConfigMapName = r.childName(
			env, "wg-inferencing-config",
		)
		if err := r.ensureWorkloadGovernance(
			ctx, env, wgName, networkName,
			member0Name, wgConfigMapName, gsName,
		); err != nil {
			if isPermanentError(err) {
				if sErr := r.envSetFailed(
					ctx, env,
					"WorkloadGovernanceCreateRejected",
					err,
				); sErr != nil {
					return ctrl.Result{}, sErr
				}
				return ctrl.Result{}, nil
			}
			return ctrl.Result{}, fmt.Errorf(
				"ensuring WorkloadGovernance: %w", err,
			)
		}
	}

	// Step 4: Ensure Cluster (created in parallel, refs
	// governance ConfigMap for readiness).
	clusterName := r.childName(env, "cluster")
	if err := r.ensureCluster(
		ctx, env, clusterName, wgConfigMapName,
	); err != nil {
		if isPermanentError(err) {
			if sErr := r.envSetFailed(
				ctx, env,
				"ClusterCreateRejected", err,
			); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"ensuring Cluster: %w", err,
		)
	}

	// Aggregate status.
	return r.reconcileAggregateStatus(ctx, env)
}

// reconcileAggregateStatus reads child resource phases and
// sets the Environment conditions and phase accordingly.
func (r *EnvironmentReconciler) reconcileAggregateStatus(
	ctx context.Context,
	env *v1alpha1.Environment,
) (ctrl.Result, error) {
	operatorName := r.childName(env, "operator")
	networkName := r.childName(env, "network")
	clusterName := r.childName(env, "cluster")

	// Sync deletion policy to children if it has drifted.
	r.syncDeletionPolicy(ctx, env, networkName, clusterName)

	allReady := true
	var failedChildren []string

	// CcfMember status.
	memberReady, memberFailed := r.checkChildCondition(
		ctx, env, operatorName,
		&v1alpha1.CcfMember{},
		v1alpha1.ConditionTypeCcfMemberReady,
		func(obj ctrlclient.Object) (bool, bool, string) {
			m := obj.(*v1alpha1.CcfMember)
			if m.Status.Phase ==
				v1alpha1.CcfMemberPhaseActive {
				return true, false, "CcfMember is Active"
			}
			if m.Status.Phase ==
				v1alpha1.CcfMemberPhaseFailed {
				msg := failedConditionMessage(
					m.Status.Conditions)
				if msg == "" {
					msg = "CcfMember failed"
				}
				return false, true, msg
			}
			phase := string(m.Status.Phase)
			if phase == "" {
				phase = "Pending"
			}
			return false, false, fmt.Sprintf(
				"CcfMember phase: %s", phase)
		},
	)
	if !memberReady {
		allReady = false
	}
	if memberFailed {
		failedChildren = append(
			failedChildren, "CcfMember")
	}

	// Initial CcfMember status.
	member0Name := r.childName(env, "member0")
	imReady, imFailed := r.checkChildCondition(
		ctx, env, member0Name,
		&v1alpha1.CcfMember{},
		v1alpha1.ConditionTypeInitialMemberReady,
		func(obj ctrlclient.Object) (bool, bool, string) {
			m := obj.(*v1alpha1.CcfMember)
			if m.Status.Phase ==
				v1alpha1.CcfMemberPhaseActive {
				return true, false,
					"InitialMember is Active"
			}
			if m.Status.Phase ==
				v1alpha1.CcfMemberPhaseFailed {
				msg := failedConditionMessage(
					m.Status.Conditions)
				if msg == "" {
					msg = "InitialMember failed"
				}
				return false, true, msg
			}
			phase := string(m.Status.Phase)
			if phase == "" {
				phase = "Pending"
			}
			return false, false, fmt.Sprintf(
				"InitialMember phase: %s", phase)
		},
	)
	if !imReady {
		allReady = false
	}
	if imFailed {
		failedChildren = append(
			failedChildren, "InitialMember")
	}

	// GovernanceService status.
	gsName := r.childName(env, "gs")
	gsReady, gsFailed := r.checkChildCondition(
		ctx, env, gsName,
		&v1alpha1.GovernanceService{},
		v1alpha1.ConditionTypeGovernanceServiceReady,
		func(obj ctrlclient.Object) (bool, bool, string) {
			gs := obj.(*v1alpha1.GovernanceService)
			if gs.Status.Phase ==
				v1alpha1.GovernanceServicePhaseReady {
				return true, false,
					"GovernanceService is Ready"
			}
			if gs.Status.Phase ==
				v1alpha1.GovernanceServicePhaseFailed {
				msg := failedConditionMessage(
					gs.Status.Conditions)
				if msg == "" {
					msg = "GovernanceService failed"
				}
				return false, true, msg
			}
			phase := string(gs.Status.Phase)
			if phase == "" {
				phase = "Pending"
			}
			return false, false, fmt.Sprintf(
				"GovernanceService phase: %s",
				phase)
		},
	)
	if !gsReady {
		allReady = false
	}
	if gsFailed {
		failedChildren = append(
			failedChildren, "GovernanceService")
	}

	// CcfNetwork status.
	networkReady, networkFailed := r.checkChildCondition(
		ctx, env, networkName,
		&v1alpha1.CcfNetwork{},
		v1alpha1.ConditionTypeCcfNetworkReady,
		func(obj ctrlclient.Object) (bool, bool, string) {
			n := obj.(*v1alpha1.CcfNetwork)
			if n.Status.Phase ==
				v1alpha1.CcfNetworkPhaseOpen {
				return true, false, "CcfNetwork is Open"
			}
			if n.Status.Phase ==
				v1alpha1.CcfNetworkPhaseFailed {
				msg := failedConditionMessage(
					n.Status.Conditions)
				if msg == "" {
					msg = "CcfNetwork failed"
				}
				return false, true, msg
			}
			phase := string(n.Status.Phase)
			if phase == "" {
				phase = "Pending"
			}
			// Surface any failing conditions from
			// the CcfNetwork (e.g. TransitionToOpen
			// failures) for better debuggability.
			detail := failedConditionMessage(
				n.Status.Conditions)
			if detail != "" {
				return false, false, fmt.Sprintf(
					"CcfNetwork phase: %s (%s)",
					phase, detail)
			}
			return false, false, fmt.Sprintf(
				"CcfNetwork phase: %s", phase)
		},
	)
	if !networkReady {
		allReady = false
	}
	if networkFailed {
		failedChildren = append(
			failedChildren, "CcfNetwork")
	}

	// WorkloadGovernance status (if kserve-inferencing).
	if r.hasKServeInferencing(env) {
		wgName := r.childName(env, "wg-inferencing")
		wgReady, wgFailed := r.checkChildCondition(
			ctx, env, wgName,
			&v1alpha1.WorkloadGovernance{},
			v1alpha1.ConditionTypeWorkloadGovernanceReady,
			func(obj ctrlclient.Object) (bool, bool, string) {
				wg := obj.(*v1alpha1.WorkloadGovernance)
				if wg.Status.Phase ==
					v1alpha1.WorkloadGovernancePhaseReady {
					return true, false,
						"WorkloadGovernance is Ready"
				}
				if wg.Status.Phase ==
					v1alpha1.WorkloadGovernancePhaseFailed {
					msg := failedConditionMessage(
						wg.Status.Conditions)
					if msg == "" {
						msg = "WorkloadGovernance failed"
					}
					return false, true, msg
				}
				phase := string(wg.Status.Phase)
				if phase == "" {
					phase = "Pending"
				}
				return false, false, fmt.Sprintf(
					"WorkloadGovernance phase: %s", phase)
			},
		)
		if !wgReady {
			allReady = false
		}
		if wgFailed {
			failedChildren = append(
				failedChildren, "WorkloadGovernance")
		}
	}

	// Cluster running status — satisfied once the cluster
	// infrastructure is up, regardless of workload profile
	// readiness.
	r.checkChildCondition(
		ctx, env, clusterName,
		&v1alpha1.Cluster{},
		v1alpha1.ConditionTypeClusterRunning,
		func(obj ctrlclient.Object) (bool, bool, string) {
			c := obj.(*v1alpha1.Cluster)
			if c.Status.Phase ==
				v1alpha1.PhaseRunning {
				return true, false,
					"Cluster is running"
			}
			if c.Status.Phase ==
				v1alpha1.PhaseFailed {
				msg := failedConditionMessage(
					c.Status.Conditions)
				if msg == "" {
					msg = "Cluster failed"
				}
				return false, true, msg
			}
			phase := string(c.Status.Phase)
			if phase == "" {
				phase = "Pending"
			}
			return false, false, fmt.Sprintf(
				"Cluster phase: %s", phase)
		},
	)

	// Cluster ready status — satisfied only when the
	// cluster is running and all workload profiles are
	// applied.
	clusterReady, clusterFailed := r.checkChildCondition(
		ctx, env, clusterName,
		&v1alpha1.Cluster{},
		v1alpha1.ConditionTypeClusterReady,
		func(obj ctrlclient.Object) (bool, bool, string) {
			c := obj.(*v1alpha1.Cluster)
			if c.Status.Phase ==
				v1alpha1.PhaseRunning {
				readyCond := meta.FindStatusCondition(
					c.Status.Conditions,
					v1alpha1.ConditionTypeReady,
				)
				if readyCond != nil &&
					readyCond.Status ==
						metav1.ConditionFalse {
					return false, false,
						readyCond.Message
				}
				return true, false,
					"Cluster is ready"
			}
			if c.Status.Phase ==
				v1alpha1.PhaseFailed {
				msg := failedConditionMessage(
					c.Status.Conditions)
				if msg == "" {
					msg = "Cluster failed"
				}
				return false, true, msg
			}
			phase := string(c.Status.Phase)
			if phase == "" {
				phase = "Pending"
			}
			return false, false, fmt.Sprintf(
				"Cluster phase: %s", phase)
		},
	)
	if !clusterReady {
		allReady = false
	}
	if clusterFailed {
		failedChildren = append(
			failedChildren, "Cluster")
	}

	if allReady {
		env.Status.Phase = v1alpha1.EnvironmentPhaseReady
		env.Status.Message = "All child resources are ready"
		env.Status.TraceParent = ""
		meta.SetStatusCondition(
			&env.Status.Conditions,
			metav1.Condition{
				Type:   v1alpha1.ConditionTypeEnvironmentReady,
				Status: metav1.ConditionTrue,
				Reason: "AllChildrenReady",
				Message: "All child resources are " +
					"ready",
			},
		)
		if err := r.Status().Update(ctx, env); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"updating status to Ready: %w", err,
			)
		}
		r.envEvent(env, corev1.EventTypeNormal,
			"Ready", "Environment is ready")
		return ctrl.Result{
			RequeueAfter: envStatusSyncInterval,
		}, nil
	}

	if len(failedChildren) > 0 {
		env.Status.Phase = v1alpha1.EnvironmentPhaseFailed
		env.Status.TraceParent = ""

		// Build a detailed message by collecting the
		// failure reason from each failed child's
		// condition on the Environment.
		var details []string
		for _, name := range failedChildren {
			detail := r.childFailureDetail(
				env, name,
			)
			details = append(details, detail)
		}
		failMsg := strings.Join(details, "; ")
		env.Status.Message = failMsg
		meta.SetStatusCondition(
			&env.Status.Conditions,
			metav1.Condition{
				Type:    v1alpha1.ConditionTypeEnvironmentReady,
				Status:  metav1.ConditionFalse,
				Reason:  "ChildFailed",
				Message: failMsg,
			},
		)
		if err := r.Status().Update(ctx, env); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"updating status to Failed: %w", err,
			)
		}
		r.envEvent(env, corev1.EventTypeWarning,
			"Failed",
			fmt.Sprintf("Environment failed: %s",
				failMsg))
		return ctrl.Result{
			RequeueAfter: envStatusSyncInterval,
		}, nil
	}

	// Not all ready, none failed — update status and requeue.
	// If we were previously Failed, transition back to
	// Provisioning now that children are progressing.
	if env.Status.Phase == v1alpha1.EnvironmentPhaseFailed {
		env.Status.Phase =
			v1alpha1.EnvironmentPhaseProvisioning
	}
	env.Status.Message = "Waiting for child resources"
	meta.SetStatusCondition(
		&env.Status.Conditions,
		metav1.Condition{
			Type:    v1alpha1.ConditionTypeEnvironmentReady,
			Status:  metav1.ConditionFalse,
			Reason:  "ChildrenNotReady",
			Message: "Waiting for child resources",
		},
	)
	if err := r.Status().Update(ctx, env); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating aggregate status: %w", err,
		)
	}

	return ctrl.Result{
		RequeueAfter: envRequeueDelay,
	}, nil
}

// checkChildCondition reads a child resource and sets a
// condition on the Environment based on the checker result.
// Returns (ready, failed).
func (r *EnvironmentReconciler) checkChildCondition(
	ctx context.Context,
	env *v1alpha1.Environment,
	name string,
	obj ctrlclient.Object,
	conditionType string,
	checker func(ctrlclient.Object) (bool, bool, string),
) (bool, bool) {
	err := r.Get(ctx, types.NamespacedName{
		Name:      name,
		Namespace: env.Namespace,
	}, obj)
	if err != nil {
		meta.SetStatusCondition(
			&env.Status.Conditions,
			metav1.Condition{
				Type:   conditionType,
				Status: metav1.ConditionFalse,
				Reason: "NotFound",
				Message: fmt.Sprintf(
					"%s not found", name),
			},
		)
		return false, false
	}

	ready, failed, msg := checker(obj)

	// If a child is failed but has a pending retry
	// annotation, treat it as retrying (not failed) to
	// avoid a race where the Environment sees stale
	// failure before the child processes its retry.
	if failed {
		annotations := obj.GetAnnotations()
		if annotations[retryAnnotation] != "" {
			failed = false
			msg = fmt.Sprintf(
				"%s (retry pending)", msg)
		}
	}

	status := metav1.ConditionFalse
	reason := "NotReady"
	if ready {
		status = metav1.ConditionTrue
		reason = "Ready"
	} else if failed {
		reason = "Failed"
	}
	meta.SetStatusCondition(
		&env.Status.Conditions,
		metav1.Condition{
			Type:    conditionType,
			Status:  status,
			Reason:  reason,
			Message: msg,
		},
	)
	return ready, failed
}

// failedConditionMessage returns the message from the first
// condition whose Reason indicates a failure. Checks both
// Status=False conditions with a "Failed" reason and
// Status=True conditions with type "Failed" (as used by
// GovernanceService).
func failedConditionMessage(
	conditions []metav1.Condition,
) string {
	// Check Status=True conditions with type "Failed"
	// (e.g. GovernanceService's Failed condition).
	for _, c := range conditions {
		if c.Type == "Failed" &&
			c.Status == metav1.ConditionTrue &&
			c.Message != "" {
			return c.Message
		}
	}
	for _, c := range conditions {
		if c.Status == metav1.ConditionFalse &&
			strings.Contains(c.Reason, "Failed") {
			return c.Message
		}
	}
	for _, c := range conditions {
		if c.Status == metav1.ConditionFalse &&
			c.Message != "" {
			return c.Message
		}
	}
	return ""
}

// childFailureDetail returns a diagnostic string for a
// failed child by looking up the child's condition message
// from the Environment's own conditions.
func (r *EnvironmentReconciler) childFailureDetail(
	env *v1alpha1.Environment,
	childName string,
) string {
	// Map child name to its condition type on the
	// Environment.
	condMap := map[string]string{
		"CcfMember":          v1alpha1.ConditionTypeCcfMemberReady,
		"InitialMember":      v1alpha1.ConditionTypeInitialMemberReady,
		"GovernanceService":  v1alpha1.ConditionTypeGovernanceServiceReady,
		"CcfNetwork":         v1alpha1.ConditionTypeCcfNetworkReady,
		"GovernanceContract": v1alpha1.ConditionTypeGovernanceContractReady,
		"Cluster":            v1alpha1.ConditionTypeClusterReady,
	}
	condType, ok := condMap[childName]
	if !ok {
		return childName + " failed"
	}
	cond := meta.FindStatusCondition(
		env.Status.Conditions, condType,
	)
	if cond != nil && cond.Message != "" {
		// Truncate very long messages for readability.
		msg := cond.Message
		const maxLen = 256
		if len(msg) > maxLen {
			msg = msg[:maxLen] + "..."
		}
		return childName + ": " + msg
	}
	return childName + " failed"
}

// ensureCcfMember creates the CcfMember child if it doesn't
// exist.
func (r *EnvironmentReconciler) ensureCcfMember(
	ctx context.Context,
	env *v1alpha1.Environment,
	name string,
	networkName string,
) error {
	var existing v1alpha1.CcfMember
	err := r.Get(ctx, types.NamespacedName{
		Name:      name,
		Namespace: env.Namespace,
	}, &existing)
	if err == nil {
		return nil // Already exists.
	}
	if !apierrors.IsNotFound(err) {
		return fmt.Errorf("checking CcfMember: %w", err)
	}

	identifier := "operator"
	if env.Spec.Member != nil &&
		env.Spec.Member.Identifier != "" {
		identifier = env.Spec.Member.Identifier
	}

	// Default to generating an encryption key so the
	// operator member can act as a recovery member —
	// CCF requires at least one to issue recovery shares
	// during transitionToOpen.
	generateEncKey := true
	cgsImage := ""
	if env.Spec.Member != nil {
		if env.Spec.Member.GenerateEncryptionKey {
			generateEncKey = true
		}
		cgsImage = env.Spec.Member.CgsImage
	}

	member := &v1alpha1.CcfMember{
		ObjectMeta: metav1.ObjectMeta{
			Name:      name,
			Namespace: env.Namespace,
			Labels:    r.childLabels(env),
		},
		Spec: v1alpha1.CcfMemberSpec{
			Identifier:            identifier,
			IsOperator:            true,
			GenerateEncryptionKey: generateEncKey,
			SecretRef: v1alpha1.SecretRef{
				Name: fmt.Sprintf(
					"%s-operator-certs", env.Name,
				),
			},
			CgsImage:   cgsImage,
			NetworkRef: networkName,
		},
	}

	if err := controllerutil.SetControllerReference(
		env, member, r.Scheme,
	); err != nil {
		return fmt.Errorf(
			"setting owner reference: %w", err,
		)
	}

	injectTraceAnnotation(ctx, member)
	if err := r.Create(ctx, member); err != nil {
		if apierrors.IsAlreadyExists(err) {
			return nil
		}
		return wrapPermanentAPIError(
			err, "creating CcfMember",
		)
	}

	r.envEvent(env, corev1.EventTypeNormal,
		"CcfMemberCreated",
		fmt.Sprintf("CcfMember %s created", name))

	return nil
}

// ensureCcfNetwork creates the CcfNetwork child once the
// member certs are available. Returns (true, nil) if the
// network was created or already exists.
func (r *EnvironmentReconciler) ensureCcfNetwork(
	ctx context.Context,
	env *v1alpha1.Environment,
	networkName string,
	memberName string,
	initialMemberName string,
) (bool, error) {
	// Check if network already exists.
	var existing v1alpha1.CcfNetwork
	err := r.Get(ctx, types.NamespacedName{
		Name:      networkName,
		Namespace: env.Namespace,
	}, &existing)
	if err == nil {
		// Ensure the existing network belongs to this
		// Environment and is not a stale resource.
		ownerUID := controllerOwnerUID(&existing)
		if ownerUID != "" && ownerUID != env.UID {
			// Stale network from previous Environment
			// incarnation, wait for GC.
			return false, nil
		}
		return true, nil
	}
	if !apierrors.IsNotFound(err) {
		return false, fmt.Errorf(
			"checking CcfNetwork: %w", err,
		)
	}

	// Read member certs from Secret.
	var member v1alpha1.CcfMember
	if err := r.Get(ctx, types.NamespacedName{
		Name:      memberName,
		Namespace: env.Namespace,
	}, &member); err != nil {
		return false, nil // Member not ready yet.
	}

	// Skip stale member from previous Environment.
	memberOwnerUID := controllerOwnerUID(&member)
	if memberOwnerUID != "" &&
		memberOwnerUID != env.UID {
		return false, nil
	}

	if member.Status.Phase !=
		v1alpha1.CcfMemberPhaseCertsGenerated &&
		member.Status.Phase !=
			v1alpha1.CcfMemberPhaseGovClientDeployed &&
		member.Status.Phase !=
			v1alpha1.CcfMemberPhaseActivating &&
		member.Status.Phase !=
			v1alpha1.CcfMemberPhaseActive {
		return false, nil // Certs not ready yet.
	}

	secretName := member.Status.SecretRef
	if secretName == "" {
		return false, nil
	}

	var secret corev1.Secret
	if err := r.Get(ctx, types.NamespacedName{
		Name:      secretName,
		Namespace: env.Namespace,
	}, &secret); err != nil {
		return false, nil
	}

	certPEM := string(
		secret.Data[v1alpha1.SecretKeyCert],
	)
	encPubKey := string(
		secret.Data[v1alpha1.SecretKeyEncPublicKey],
	)

	if certPEM == "" {
		return false, nil
	}

	// Build member data.
	memberData, _ := json.Marshal(map[string]interface{}{
		"identifier": func() string {
			if env.Spec.Member != nil &&
				env.Spec.Member.Identifier != "" {
				return env.Spec.Member.Identifier
			}
			return "operator"
		}(),
		"isOperator": true,
	})

	memberSpec := v1alpha1.MemberSpec{
		Certificate:         certPEM,
		EncryptionPublicKey: encPubKey,
		MemberData: &runtime.RawExtension{
			Raw: memberData,
		},
	}

	members := []v1alpha1.MemberSpec{memberSpec}

	// Add the initial member's certs so the network
	// starts with both members in the consortium.
	imSpec, ok := r.readMemberCerts(
		ctx, env, initialMemberName,
	)
	if ok {
		members = append(members, imSpec)
	} else {
		// Waiting for initial member certs.
		return false, nil
	}

	nodeCount := 1
	nodeLogLevel := ""
	var providerConfig *runtime.RawExtension
	if env.Spec.CcfNetwork != nil {
		if env.Spec.CcfNetwork.NodeCount != nil &&
			*env.Spec.CcfNetwork.NodeCount > 0 {
			nodeCount = *env.Spec.CcfNetwork.NodeCount
		}
		nodeLogLevel = env.Spec.CcfNetwork.NodeLogLevel
		providerConfig = env.Spec.CcfNetwork.ProviderConfig
	}

	// Fall back to prereqs ConfigMap for CCF provider config.
	if providerConfig == nil {
		pc, err := r.readPrereqsProviderConfig(
			ctx, env, "ccfProviderConfig",
		)
		if err != nil {
			return false, err
		}
		providerConfig = pc
	}

	infraType := env.Spec.InfraType
	if infraType == "aks" {
		// AKS environments use CACI for confidential CCF
		// networking with SEV-SNP hardware attestation.
		infraType = "caci"
	}

	network := &v1alpha1.CcfNetwork{
		ObjectMeta: metav1.ObjectMeta{
			Name:      networkName,
			Namespace: env.Namespace,
			Labels:    r.childLabels(env),
			Annotations: map[string]string{
				memberSecretAnnotation: secretName,
				memberRefAnnotation:    memberName,
			},
		},
		Spec: v1alpha1.CcfNetworkSpec{
			InfraType:      infraType,
			NodeCount:      nodeCount,
			Members:        members,
			NodeLogLevel:   nodeLogLevel,
			ProviderConfig: providerConfig,
			DeletionPolicy: resolvedDeletionPolicy(env),
			SecurityPolicyCreationOption: func() string {
				if env.Spec.CcfNetwork != nil &&
					env.Spec.CcfNetwork.
						SecurityPolicyCreationOption != "" {
					return env.Spec.CcfNetwork.
						SecurityPolicyCreationOption
				}
				return "allowAll"
			}(),
		},
	}

	if err := controllerutil.SetControllerReference(
		env, network, r.Scheme,
	); err != nil {
		return false, fmt.Errorf(
			"setting owner reference: %w", err,
		)
	}

	injectTraceAnnotation(ctx, network)
	if err := r.Create(ctx, network); err != nil {
		if apierrors.IsAlreadyExists(err) {
			return true, nil
		}
		return false, wrapPermanentAPIError(
			err, "creating CcfNetwork",
		)
	}

	r.envEvent(env, corev1.EventTypeNormal,
		"CcfNetworkCreated",
		fmt.Sprintf("CcfNetwork %s created", networkName))

	return true, nil
}

// ensureGovernanceContract creates the GovernanceContract
// child if it doesn't exist.
func (r *EnvironmentReconciler) ensureGovernanceContract(
	ctx context.Context,
	env *v1alpha1.Environment,
	contractName string,
	networkName string,
	memberName string,
	configMapName string,
	gsName string,
) error {
	var existing v1alpha1.GovernanceContract
	err := r.Get(ctx, types.NamespacedName{
		Name:      contractName,
		Namespace: env.Namespace,
	}, &existing)
	if err == nil {
		return nil
	}
	if !apierrors.IsNotFound(err) {
		return fmt.Errorf(
			"checking GovernanceContract: %w", err,
		)
	}

	contractId := env.Name
	if env.Spec.ContractId != "" {
		contractId = env.Spec.ContractId
	}

	gc := &v1alpha1.GovernanceContract{
		ObjectMeta: metav1.ObjectMeta{
			Name:      contractName,
			Namespace: env.Namespace,
			Labels:    r.childLabels(env),
		},
		Spec: v1alpha1.GovernanceContractSpec{
			ContractId:           contractId,
			NetworkRef:           networkName,
			MemberRef:            memberName,
			DeploymentSpec:       env.Spec.DeploymentSpec,
			CleanRoomPolicy:      env.Spec.CleanRoomPolicy,
			AutoApprove:          env.Spec.AutoApprove,
			RuntimeOptions:       env.Spec.RuntimeOptions,
			EnableCA:             env.Spec.EnableCA,
			OutputConfigMapRef:   configMapName,
			GovernanceServiceRef: gsName,
		},
	}

	if err := controllerutil.SetControllerReference(
		env, gc, r.Scheme,
	); err != nil {
		return fmt.Errorf(
			"setting owner reference: %w", err,
		)
	}

	injectTraceAnnotation(ctx, gc)
	if err := r.Create(ctx, gc); err != nil {
		if apierrors.IsAlreadyExists(err) {
			return nil
		}
		return wrapPermanentAPIError(
			err, "creating GovernanceContract",
		)
	}

	r.envEvent(env, corev1.EventTypeNormal,
		"GovernanceContractCreated",
		fmt.Sprintf("GovernanceContract %s created",
			contractName))

	return nil
}

// hasKServeInferencing returns true if the environment has
// KServe inferencing enabled.
func (r *EnvironmentReconciler) hasKServeInferencing(
	env *v1alpha1.Environment,
) bool {
	if env.Spec.Profiles == nil {
		return false
	}
	if env.Spec.Profiles.Inferencing == nil {
		return false
	}
	kp := env.Spec.Profiles.Inferencing.KServeProfile
	return kp != nil && kp.Enabled
}

// ensureWorkloadGovernance creates the WorkloadGovernance
// child if it doesn't exist.
func (r *EnvironmentReconciler) ensureWorkloadGovernance(
	ctx context.Context,
	env *v1alpha1.Environment,
	wgName string,
	networkName string,
	memberName string,
	configMapName string,
	gsName string,
) error {
	var existing v1alpha1.WorkloadGovernance
	err := r.Get(ctx, types.NamespacedName{
		Name:      wgName,
		Namespace: env.Namespace,
	}, &existing)
	if err == nil {
		return nil
	}
	if !apierrors.IsNotFound(err) {
		return fmt.Errorf(
			"checking WorkloadGovernance: %w", err,
		)
	}

	contractId := env.Name + "-inferencing"
	if env.Spec.ContractId != "" {
		contractId = env.Spec.ContractId + "-inferencing"
	}

	secPolicy := ""
	if env.Spec.Profiles != nil &&
		env.Spec.Profiles.Inferencing != nil &&
		env.Spec.Profiles.Inferencing.KServeProfile != nil {
		secPolicy = env.Spec.Profiles.Inferencing.
			KServeProfile.SecurityPolicyCreationOption
	}
	if secPolicy == "" {
		secPolicy = "allowAll"
	}

	wg := &v1alpha1.WorkloadGovernance{
		ObjectMeta: metav1.ObjectMeta{
			Name:      wgName,
			Namespace: env.Namespace,
			Labels:    r.childLabels(env),
		},
		Spec: v1alpha1.WorkloadGovernanceSpec{
			WorkloadType:                 "kserve-inferencing",
			ContractId:                   contractId,
			NetworkRef:                   networkName,
			MemberRef:                    memberName,
			GovernanceServiceRef:         gsName,
			AutoApprove:                  env.Spec.AutoApprove,
			EnableCA:                     env.Spec.EnableCA,
			RuntimeOptions:               env.Spec.RuntimeOptions,
			InfraType:                    env.Spec.InfraType,
			SecurityPolicyCreationOption: secPolicy,
			ProviderConfig:               env.Spec.ClusterProviderConfig,
			OutputConfigMapRef:           configMapName,
		},
	}

	if err := controllerutil.SetControllerReference(
		env, wg, r.Scheme,
	); err != nil {
		return fmt.Errorf(
			"setting owner reference: %w", err,
		)
	}

	injectTraceAnnotation(ctx, wg)
	if err := r.Create(ctx, wg); err != nil {
		if apierrors.IsAlreadyExists(err) {
			return nil
		}
		return wrapPermanentAPIError(
			err, "creating WorkloadGovernance",
		)
	}

	r.envEvent(env, corev1.EventTypeNormal,
		"WorkloadGovernanceCreated",
		fmt.Sprintf("WorkloadGovernance %s created",
			wgName))

	return nil
}

// ensureCluster creates the Cluster child if it doesn't
// exist. The cluster is created in parallel with governance
// setup and references the governance ConfigMap for
// readiness via per-profile GovernanceConfigRef.
func (r *EnvironmentReconciler) ensureCluster(
	ctx context.Context,
	env *v1alpha1.Environment,
	clusterName string,
	wgConfigMapName string,
) error {
	var existing v1alpha1.Cluster
	err := r.Get(ctx, types.NamespacedName{
		Name:      clusterName,
		Namespace: env.Namespace,
	}, &existing)
	if err == nil {
		// Cluster exists — update profiles if changed.
		return r.updateClusterProfiles(
			ctx, env, &existing, wgConfigMapName,
		)
	}
	if !apierrors.IsNotFound(err) {
		return fmt.Errorf(
			"checking Cluster: %w", err,
		)
	}

	// Resolve the cluster provider config by merging the
	// prereqs ConfigMap (base: subscription, resource group,
	// tenant, location) with the Environment's explicit
	// clusterProviderConfig (overrides: e.g. aksClusterName /
	// kindClusterName). Explicit values win on conflict. This
	// lets a caller target an existing cluster by name while
	// still supplying the Azure context from prereqs.
	prereqsPc, err := r.readPrereqsProviderConfig(
		ctx, env, "clusterProviderConfig",
	)
	if err != nil {
		return err
	}
	clusterProviderConfig, err := mergeProviderConfig(
		prereqsPc, env.Spec.ClusterProviderConfig,
	)
	if err != nil {
		return fmt.Errorf(
			"merging cluster provider config: %w", err,
		)
	}

	cluster := &v1alpha1.Cluster{
		ObjectMeta: metav1.ObjectMeta{
			Name:      clusterName,
			Namespace: env.Namespace,
			Labels:    r.childLabels(env),
		},
		Spec: v1alpha1.ClusterSpec{
			InfraType:      env.Spec.InfraType,
			ProviderConfig: clusterProviderConfig,
			DeletionPolicy: resolvedDeletionPolicy(env),
		},
	}

	// Map profiles.
	if env.Spec.Profiles != nil {
		p := env.Spec.Profiles
		cluster.Spec.ObservabilityProfile = p.Observability
		cluster.Spec.MonitoringProfile = p.Monitoring
		cluster.Spec.AnalyticsWorkloadProfile = p.Analytics
		cluster.Spec.InferencingWorkloadProfile = p.Inferencing
		cluster.Spec.FlexNodeProfile = p.FlexNode
		cluster.Spec.AadProfile = p.Aad
	}

	// Default KServe security policy to allowAll.
	if cluster.Spec.InferencingWorkloadProfile != nil &&
		cluster.Spec.InferencingWorkloadProfile.
			KServeProfile != nil &&
		cluster.Spec.InferencingWorkloadProfile.
			KServeProfile.
			SecurityPolicyCreationOption == "" {
		cluster.Spec.InferencingWorkloadProfile.
			KServeProfile.
			SecurityPolicyCreationOption = "allowAll"
	}

	// Set per-profile GovernanceConfigRef for KServe.
	if wgConfigMapName != "" &&
		cluster.Spec.InferencingWorkloadProfile != nil &&
		cluster.Spec.InferencingWorkloadProfile.
			KServeProfile != nil {
		cluster.Spec.InferencingWorkloadProfile.
			KServeProfile.GovernanceConfigRef =
			wgConfigMapName
	}

	// If flex node is enabled, ensure AAD is enabled on
	// the cluster so that EnableFlexNodeAsync can skip the
	// AAD update step when it was already done at creation.
	if cluster.Spec.FlexNodeProfile != nil &&
		cluster.Spec.FlexNodeProfile.Enabled {
		if cluster.Spec.AadProfile == nil {
			cluster.Spec.AadProfile =
				&v1alpha1.AadProfileSpec{
					AdminGroupObjectIds: []string{},
				}
		}
		cluster.Spec.AadProfile.Enabled = true
	}

	// Apply flex node defaults.
	if cluster.Spec.FlexNodeProfile != nil &&
		cluster.Spec.FlexNodeProfile.Enabled {
		// Default Insecure to true when not explicitly set.
		if cluster.Spec.FlexNodeProfile.Insecure == nil {
			t := true
			cluster.Spec.FlexNodeProfile.Insecure = &t
		}
		// Default ProvisionUsingSSH to true when not explicitly
		// set so the operator flow provisions flex node VMs via
		// SSH (stock Ubuntu CVM) and does not require a pre-baked
		// gallery image / cleanroom-image-digests artifact.
		if cluster.Spec.FlexNodeProfile.ProvisionUsingSSH == nil {
			t := true
			cluster.Spec.FlexNodeProfile.ProvisionUsingSSH = &t
		}
		// Set GovernanceConfigRef so the Cluster controller
		// can read policySigningCertPem from the ConfigMap.
		if wgConfigMapName != "" &&
			cluster.Spec.FlexNodeProfile.
				GovernanceConfigRef == "" {
			cluster.Spec.FlexNodeProfile.
				GovernanceConfigRef = wgConfigMapName
		}
	}

	if err := controllerutil.SetControllerReference(
		env, cluster, r.Scheme,
	); err != nil {
		return fmt.Errorf(
			"setting owner reference: %w", err,
		)
	}

	injectTraceAnnotation(ctx, cluster)
	if err := r.Create(ctx, cluster); err != nil {
		if apierrors.IsAlreadyExists(err) {
			return nil
		}
		return wrapPermanentAPIError(
			err, "creating Cluster",
		)
	}

	r.envEvent(env, corev1.EventTypeNormal,
		"ClusterCreated",
		fmt.Sprintf("Cluster %s created", clusterName))

	return nil
}

// updateClusterProfiles patches an existing Cluster CR
// when the Environment profiles have changed (e.g. flex
// node enabled on an existing environment).
func (r *EnvironmentReconciler) updateClusterProfiles(
	ctx context.Context,
	env *v1alpha1.Environment,
	cluster *v1alpha1.Cluster,
	wgConfigMapName string,
) error {
	log := ctrllog.FromContext(ctx)
	updated := false

	if env.Spec.Profiles != nil {
		p := env.Spec.Profiles

		if p.FlexNode != nil &&
			cluster.Spec.FlexNodeProfile == nil {
			log.Info("Adding flex node profile to "+
				"existing Cluster",
				"cluster", cluster.Name)
			cluster.Spec.FlexNodeProfile = p.FlexNode
			updated = true
		}

		if p.Observability != nil &&
			cluster.Spec.ObservabilityProfile == nil {
			cluster.Spec.ObservabilityProfile =
				p.Observability
			updated = true
		}

		if p.Monitoring != nil &&
			cluster.Spec.MonitoringProfile == nil {
			cluster.Spec.MonitoringProfile = p.Monitoring
			updated = true
		}

		if p.Analytics != nil &&
			cluster.Spec.AnalyticsWorkloadProfile == nil {
			cluster.Spec.AnalyticsWorkloadProfile =
				p.Analytics
			updated = true
		}

		if p.Inferencing != nil &&
			cluster.Spec.InferencingWorkloadProfile == nil {
			cluster.Spec.InferencingWorkloadProfile =
				p.Inferencing
			updated = true
		}

		if p.Aad != nil &&
			cluster.Spec.AadProfile == nil {
			cluster.Spec.AadProfile = p.Aad
			updated = true
		}
	}

	if !updated {
		return nil
	}

	// Apply flex node defaults on the updated profile.
	if cluster.Spec.FlexNodeProfile != nil &&
		cluster.Spec.FlexNodeProfile.Enabled {
		if cluster.Spec.FlexNodeProfile.Insecure == nil {
			t := true
			cluster.Spec.FlexNodeProfile.Insecure = &t
		}
		if cluster.Spec.FlexNodeProfile.ProvisionUsingSSH == nil {
			t := true
			cluster.Spec.FlexNodeProfile.ProvisionUsingSSH = &t
		}
		if wgConfigMapName != "" &&
			cluster.Spec.FlexNodeProfile.
				GovernanceConfigRef == "" {
			cluster.Spec.FlexNodeProfile.
				GovernanceConfigRef = wgConfigMapName
		}
	}

	// Set GovernanceConfigRef for KServe if needed.
	if wgConfigMapName != "" &&
		cluster.Spec.InferencingWorkloadProfile != nil &&
		cluster.Spec.InferencingWorkloadProfile.
			KServeProfile != nil &&
		cluster.Spec.InferencingWorkloadProfile.
			KServeProfile.GovernanceConfigRef == "" {
		cluster.Spec.InferencingWorkloadProfile.
			KServeProfile.GovernanceConfigRef =
			wgConfigMapName
	}

	// Default KServe security policy to allowAll.
	if cluster.Spec.InferencingWorkloadProfile != nil &&
		cluster.Spec.InferencingWorkloadProfile.
			KServeProfile != nil &&
		cluster.Spec.InferencingWorkloadProfile.
			KServeProfile.
			SecurityPolicyCreationOption == "" {
		cluster.Spec.InferencingWorkloadProfile.
			KServeProfile.
			SecurityPolicyCreationOption = "allowAll"
	}

	cluster.Spec.DeletionPolicy =
		resolvedDeletionPolicy(env)

	if err := r.Update(ctx, cluster); err != nil {
		return wrapPermanentAPIError(
			err, "updating Cluster profiles",
		)
	}

	r.envEvent(env, corev1.EventTypeNormal,
		"ClusterUpdated",
		fmt.Sprintf("Cluster %s profiles updated",
			cluster.Name))

	return nil
}

// ensureInitialMember creates a non-operator CcfMember for
// the initial consortium member (member0). This member is
// used by the GovernanceService to submit proposals.
func (r *EnvironmentReconciler) ensureInitialMember(
	ctx context.Context,
	env *v1alpha1.Environment,
	name string,
	networkName string,
) error {
	var existing v1alpha1.CcfMember
	err := r.Get(ctx, types.NamespacedName{
		Name:      name,
		Namespace: env.Namespace,
	}, &existing)
	if err == nil {
		return nil // Already exists.
	}
	if !apierrors.IsNotFound(err) {
		return fmt.Errorf(
			"checking initial CcfMember: %w", err,
		)
	}

	identifier := "member0"
	cgsImage := ""
	if env.Spec.InitialMember != nil {
		if env.Spec.InitialMember.Identifier != "" {
			identifier =
				env.Spec.InitialMember.Identifier
		}
		cgsImage = env.Spec.InitialMember.CgsImage
	}

	member := &v1alpha1.CcfMember{
		ObjectMeta: metav1.ObjectMeta{
			Name:      name,
			Namespace: env.Namespace,
			Labels:    r.childLabels(env),
		},
		Spec: v1alpha1.CcfMemberSpec{
			Identifier:            identifier,
			IsOperator:            false,
			GenerateEncryptionKey: false,
			SecretRef: v1alpha1.SecretRef{
				Name: fmt.Sprintf(
					"%s-member0-certs",
					env.Name,
				),
			},
			CgsImage:   cgsImage,
			NetworkRef: networkName,
		},
	}

	if err := controllerutil.SetControllerReference(
		env, member, r.Scheme,
	); err != nil {
		return fmt.Errorf(
			"setting owner reference: %w", err,
		)
	}

	injectTraceAnnotation(ctx, member)
	if err := r.Create(ctx, member); err != nil {
		if apierrors.IsAlreadyExists(err) {
			return nil
		}
		return wrapPermanentAPIError(
			err, "creating initial CcfMember",
		)
	}

	r.envEvent(env, corev1.EventTypeNormal,
		"InitialMemberCreated",
		fmt.Sprintf("Initial CcfMember %s created", name))

	return nil
}

// readMemberCerts reads the signing cert from a CcfMember's
// secret and returns a MemberSpec. Returns (spec, true) if
// certs are available, (zero, false) if not yet ready.
func (r *EnvironmentReconciler) readMemberCerts(
	ctx context.Context,
	env *v1alpha1.Environment,
	memberName string,
) (v1alpha1.MemberSpec, bool) {
	var member v1alpha1.CcfMember
	if err := r.Get(ctx, types.NamespacedName{
		Name:      memberName,
		Namespace: env.Namespace,
	}, &member); err != nil {
		return v1alpha1.MemberSpec{}, false
	}

	// Skip stale member from previous Environment.
	memberOwnerUID := controllerOwnerUID(&member)
	if memberOwnerUID != "" &&
		memberOwnerUID != env.UID {
		return v1alpha1.MemberSpec{}, false
	}

	if member.Status.Phase !=
		v1alpha1.CcfMemberPhaseCertsGenerated &&
		member.Status.Phase !=
			v1alpha1.CcfMemberPhaseGovClientDeployed &&
		member.Status.Phase !=
			v1alpha1.CcfMemberPhaseActivating &&
		member.Status.Phase !=
			v1alpha1.CcfMemberPhaseActive {
		return v1alpha1.MemberSpec{}, false
	}

	secretName := member.Status.SecretRef
	if secretName == "" {
		return v1alpha1.MemberSpec{}, false
	}

	var secret corev1.Secret
	if err := r.Get(ctx, types.NamespacedName{
		Name:      secretName,
		Namespace: env.Namespace,
	}, &secret); err != nil {
		return v1alpha1.MemberSpec{}, false
	}

	certPEM := string(
		secret.Data[v1alpha1.SecretKeyCert],
	)
	encPubKey := string(
		secret.Data[v1alpha1.SecretKeyEncPublicKey],
	)
	if certPEM == "" {
		return v1alpha1.MemberSpec{}, false
	}

	identifier := member.Spec.Identifier
	memberData, _ := json.Marshal(
		map[string]interface{}{
			"identifier": identifier,
			"isOperator": member.Spec.IsOperator,
		},
	)

	return v1alpha1.MemberSpec{
		Certificate:         certPEM,
		EncryptionPublicKey: encPubKey,
		MemberData: &runtime.RawExtension{
			Raw: memberData,
		},
	}, true
}

// ensureGovernanceService creates a GovernanceService child
// that deploys the constitution, JS app, and OIDC before
// contract creation.
func (r *EnvironmentReconciler) ensureGovernanceService(
	ctx context.Context,
	env *v1alpha1.Environment,
	gsName string,
	networkName string,
	memberName string,
) error {
	var existing v1alpha1.GovernanceService
	err := r.Get(ctx, types.NamespacedName{
		Name:      gsName,
		Namespace: env.Namespace,
	}, &existing)
	if err == nil {
		return nil
	}
	if !apierrors.IsNotFound(err) {
		return fmt.Errorf(
			"checking GovernanceService: %w", err,
		)
	}

	var constitutionImage, jsAppImage, tenantId string
	var oidcContainerName string
	if env.Spec.GovernanceService != nil {
		constitutionImage =
			env.Spec.GovernanceService.ConstitutionImage
		jsAppImage =
			env.Spec.GovernanceService.JsAppImage
		tenantId =
			env.Spec.GovernanceService.TenantId
		oidcContainerName =
			env.Spec.GovernanceService.OidcContainerName
	}

	gs := &v1alpha1.GovernanceService{
		ObjectMeta: metav1.ObjectMeta{
			Name:      gsName,
			Namespace: env.Namespace,
			Labels:    r.childLabels(env),
		},
		Spec: v1alpha1.GovernanceServiceSpec{
			NetworkRef:        networkName,
			MemberRef:         memberName,
			ConstitutionImage: constitutionImage,
			JsAppImage:        jsAppImage,
			TenantId:          tenantId,
			OidcContainerName: oidcContainerName,
		},
	}

	if err := controllerutil.SetControllerReference(
		env, gs, r.Scheme,
	); err != nil {
		return fmt.Errorf(
			"setting owner reference: %w", err,
		)
	}

	injectTraceAnnotation(ctx, gs)
	if err := r.Create(ctx, gs); err != nil {
		if apierrors.IsAlreadyExists(err) {
			return nil
		}
		return wrapPermanentAPIError(
			err, "creating GovernanceService",
		)
	}

	r.envEvent(env, corev1.EventTypeNormal,
		"GovernanceServiceCreated",
		fmt.Sprintf("GovernanceService %s created",
			gsName))

	return nil
}

func (r *EnvironmentReconciler) reconcileDelete(
	ctx context.Context,
	env *v1alpha1.Environment,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	if !controllerutil.ContainsFinalizer(
		env, envFinalizerName,
	) {
		return ctrl.Result{}, nil
	}

	log.Info("Deleting Environment resources",
		"name", env.Name)

	env.Status.Phase = v1alpha1.EnvironmentPhaseDeleting
	_ = r.Status().Update(ctx, env)

	r.envEvent(env, corev1.EventTypeNormal,
		"Deleting", "Environment deletion started")

	// Owned resources are garbage-collected via
	// ownerReferences (cascade delete).

	controllerutil.RemoveFinalizer(env, envFinalizerName)
	if err := r.Update(ctx, env); err != nil {
		if apierrors.IsNotFound(err) {
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"removing finalizer: %w", err,
		)
	}

	log.Info("Environment deleted", "name", env.Name)
	return ctrl.Result{}, nil
}

// propagateRetry sets the retry annotation on all Failed
// child resources, clears it from the Environment, and
// resets the phase to Provisioning.
func (r *EnvironmentReconciler) propagateRetry(
	ctx context.Context,
	env *v1alpha1.Environment,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "Environment", "Retry", env.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	log.Info("Propagating retry to failed children")

	// Acknowledge the reconcile request by copying the
	// requested timestamp into status.
	requestedAt := env.Annotations[reconcileRequestedAtAn]

	type childRef struct {
		name  string
		obj   ctrlclient.Object
		phase string
	}

	children := []childRef{
		{
			name: r.childName(env, "operator"),
			obj:  &v1alpha1.CcfMember{},
		},
		{
			name: r.childName(env, "member0"),
			obj:  &v1alpha1.CcfMember{},
		},
		{
			name: r.childName(env, "network"),
			obj:  &v1alpha1.CcfNetwork{},
		},
		{
			name: r.childName(env, "gs"),
			obj:  &v1alpha1.GovernanceService{},
		},
		{
			name: r.childName(env, "wg-inferencing-gc"),
			obj:  &v1alpha1.GovernanceContract{},
		},
		{
			name: r.childName(env, "cluster"),
			obj:  &v1alpha1.Cluster{},
		},
		{
			name: r.childName(env, "wg-inferencing"),
			obj:  &v1alpha1.WorkloadGovernance{},
		},
	}

	for _, child := range children {
		if err := r.Get(ctx, types.NamespacedName{
			Name:      child.name,
			Namespace: env.Namespace,
		}, child.obj); err != nil {
			continue
		}

		// Only set retry on Failed children.
		var isFailed bool
		switch o := child.obj.(type) {
		case *v1alpha1.CcfMember:
			isFailed = o.Status.Phase ==
				v1alpha1.CcfMemberPhaseFailed
		case *v1alpha1.CcfNetwork:
			isFailed = o.Status.Phase ==
				v1alpha1.CcfNetworkPhaseFailed
		case *v1alpha1.GovernanceService:
			isFailed = o.Status.Phase ==
				v1alpha1.GovernanceServicePhaseFailed
		case *v1alpha1.GovernanceContract:
			isFailed = o.Status.Phase ==
				v1alpha1.GovernanceContractPhaseFailed
		case *v1alpha1.Cluster:
			isFailed = o.Status.Phase ==
				v1alpha1.PhaseFailed
		case *v1alpha1.WorkloadGovernance:
			isFailed = o.Status.Phase ==
				v1alpha1.WorkloadGovernancePhaseFailed
		}

		if !isFailed {
			continue
		}

		annotations := child.obj.GetAnnotations()
		if annotations == nil {
			annotations = map[string]string{}
		}
		annotations[retryAnnotation] = "true"
		child.obj.SetAnnotations(annotations)
		injectTraceAnnotation(ctx, child.obj)
		if err := r.Update(
			ctx, child.obj,
		); err != nil {
			log.Error(err,
				"Failed to set retry annotation",
				"child", child.name)
		}
	}

	// Clear reconcile annotation from Environment and
	// acknowledge in status.
	delete(env.Annotations, reconcileRequestedAtAn)
	if err := r.Update(ctx, env); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"clearing reconcile annotation: %w", err,
		)
	}

	// Reset to Provisioning and store acknowledgment.
	env.Status.Phase =
		v1alpha1.EnvironmentPhaseProvisioning
	env.Status.LastHandledReconcileAt = requestedAt
	env.Status.TraceParent,
		env.Status.LastOperationTraceId =
		saveTrace(ctx)
	if err := r.Status().Update(ctx, env); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"resetting phase to Provisioning: %w", err,
		)
	}

	r.envEvent(env, corev1.EventTypeNormal,
		"RetryPropagated",
		"Retry annotation propagated to failed children")

	return ctrl.Result{Requeue: true}, nil
}

// childName returns the name of a child resource.
func (r *EnvironmentReconciler) childName(
	env *v1alpha1.Environment,
	suffix string,
) string {
	return fmt.Sprintf("%s-%s", env.Name, suffix)
}

// childLabels returns common labels for child resources.
func (r *EnvironmentReconciler) childLabels(
	env *v1alpha1.Environment,
) map[string]string {
	return map[string]string{
		"app.kubernetes.io/managed-by":    "cleanroom-operator",
		"cleanroom.azure.com/environment": env.Name,
	}
}

// envEvent emits a Kubernetes event.
// readPrereqsProviderConfig reads a provider config JSON
// string from the prereqs ConfigMap referenced by the
// Environment. Returns nil if PrereqsConfigRef is empty or
// the key is missing.
func (r *EnvironmentReconciler) readPrereqsProviderConfig(
	ctx context.Context,
	env *v1alpha1.Environment,
	key string,
) (*runtime.RawExtension, error) {
	if env.Spec.PrereqsConfigRef == "" {
		return nil, nil
	}

	var cm corev1.ConfigMap
	err := r.Get(ctx, types.NamespacedName{
		Name:      env.Spec.PrereqsConfigRef,
		Namespace: env.Namespace,
	}, &cm)
	if err != nil {
		return nil, fmt.Errorf(
			"reading prereqs ConfigMap %q: %w",
			env.Spec.PrereqsConfigRef, err,
		)
	}

	raw, ok := cm.Data[key]
	if !ok || raw == "" {
		return nil, nil
	}

	// Validate it is valid JSON.
	var tmp map[string]interface{}
	if err := json.Unmarshal(
		[]byte(raw), &tmp,
	); err != nil {
		return nil, fmt.Errorf(
			"parsing prereqs ConfigMap key %q: %w",
			key, err,
		)
	}

	return &runtime.RawExtension{
		Raw: []byte(raw),
	}, nil
}

// mergeProviderConfig merges two provider config JSON blobs.
// base supplies the defaults (e.g. from the prereqs ConfigMap)
// and override's keys win on conflict (e.g. an explicit
// aksClusterName / kindClusterName on the Environment). Either
// argument may be nil. Returns nil only when both are nil.
func mergeProviderConfig(
	base, override *runtime.RawExtension,
) (*runtime.RawExtension, error) {
	if base == nil {
		return override, nil
	}
	if override == nil {
		return base, nil
	}

	merged := map[string]interface{}{}
	if len(base.Raw) > 0 {
		if err := json.Unmarshal(
			base.Raw, &merged,
		); err != nil {
			return nil, fmt.Errorf(
				"parsing base provider config: %w", err,
			)
		}
	}

	overrideMap := map[string]interface{}{}
	if len(override.Raw) > 0 {
		if err := json.Unmarshal(
			override.Raw, &overrideMap,
		); err != nil {
			return nil, fmt.Errorf(
				"parsing override provider config: %w", err,
			)
		}
	}
	for k, v := range overrideMap {
		merged[k] = v
	}

	raw, err := json.Marshal(merged)
	if err != nil {
		return nil, fmt.Errorf(
			"marshaling merged provider config: %w", err,
		)
	}
	return &runtime.RawExtension{Raw: raw}, nil
}

func (r *EnvironmentReconciler) envEvent(
	env *v1alpha1.Environment,
	eventType string,
	reason string,
	message string,
) {
	if r.Recorder != nil {
		r.Recorder.Event(
			env, eventType, reason, message,
		)
	}
}

// envSetFailed transitions the Environment to Failed phase.
func (r *EnvironmentReconciler) envSetFailed(
	ctx context.Context,
	env *v1alpha1.Environment,
	reason string,
	err error,
) error {
	log := ctrllog.FromContext(ctx)
	log.Error(err, "Operation failed: "+reason)
	recordContextError(ctx, err)

	message := truncateMessage(
		err.Error(), maxConditionMessageLen,
	)
	env.Status.Phase = v1alpha1.EnvironmentPhaseFailed
	env.Status.Message = message
	env.Status.TraceParent = ""
	meta.SetStatusCondition(
		&env.Status.Conditions,
		metav1.Condition{
			Type:    v1alpha1.ConditionTypeEnvironmentReady,
			Status:  metav1.ConditionFalse,
			Reason:  reason,
			Message: message,
		},
	)
	if sErr := r.Status().Update(ctx, env); sErr != nil {
		return fmt.Errorf(
			"updating failed status: %w", sErr,
		)
	}
	r.envEvent(env, corev1.EventTypeWarning,
		reason, message)
	return nil
}

// SetupWithManager sets up the controller with the Manager.
func (r *EnvironmentReconciler) SetupWithManager(
	mgr ctrl.Manager,
) error {
	return ctrl.NewControllerManagedBy(mgr).
		For(&v1alpha1.Environment{}).
		Owns(&v1alpha1.CcfMember{}).
		Owns(&v1alpha1.CcfNetwork{}).
		Owns(&v1alpha1.GovernanceService{}).
		Owns(&v1alpha1.GovernanceContract{}).
		Owns(&v1alpha1.Cluster{}).
		Complete(r)
}
