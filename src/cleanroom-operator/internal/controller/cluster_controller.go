package controller

import (
	"context"
	"crypto/rand"
	"crypto/rsa"
	"crypto/x509"
	"encoding/json"
	"encoding/pem"
	"fmt"
	"strings"
	"time"

	"golang.org/x/crypto/ssh"

	corev1 "k8s.io/api/core/v1"
	apierrors "k8s.io/apimachinery/pkg/api/errors"
	"k8s.io/apimachinery/pkg/api/meta"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/runtime"
	"k8s.io/client-go/tools/record"
	ctrl "sigs.k8s.io/controller-runtime"
	ctrlclient "sigs.k8s.io/controller-runtime/pkg/client"
	"sigs.k8s.io/controller-runtime/pkg/controller/controllerutil"
	"sigs.k8s.io/controller-runtime/pkg/handler"
	ctrllog "sigs.k8s.io/controller-runtime/pkg/log"

	v1alpha1 "github.com/Azure/azure-cleanroom/cleanroom-operator/api/v1alpha1"
	"github.com/Azure/azure-cleanroom/cleanroom-operator/client"
)

const (
	finalizerName             = "cleanroom.azure.com/finalizer"
	retryAnnotation           = "cleanroom.azure.com/retry"
	clusterRequeueDelay       = 15 * time.Second
	clusterStatusSyncInterval = 60 * time.Second
	govConfigIndexKey         = ".spec.governanceConfigRef"

	flexNodeSshSecretName = "cleanroom-flex-node-ssh-key"
	sshPrivateKeyField    = "ssh-private-key"
	sshPublicKeyField     = "ssh-public-key"

	// syncFailureCountAnn tracks consecutive sync failures.
	// After maxSyncFailures the resource transitions to Failed.
	syncFailureCountAnn = "cleanroom.azure.com/sync-failure-count"
	maxSyncFailures     = 5
)

// ClusterReconciler reconciles Cluster objects.
type ClusterReconciler struct {
	ctrlclient.Client
	Scheme        *runtime.Scheme
	ClusterClient *client.ClusterClient
	Recorder      record.EventRecorder
}

// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=clusters,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=clusters/status,verbs=get;update;patch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=clusters/finalizers,verbs=update
// +kubebuilder:rbac:groups="",resources=secrets,verbs=get;list;watch;create
// +kubebuilder:rbac:groups="",resources=configmaps,verbs=get;list;watch
// +kubebuilder:rbac:groups="",resources=events,verbs=create;patch

// Reconcile handles the reconciliation loop for Cluster.
func (r *ClusterReconciler) Reconcile(
	ctx context.Context,
	req ctrl.Request,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	var cluster v1alpha1.Cluster
	if err := r.Get(ctx, req.NamespacedName, &cluster); err != nil {
		if apierrors.IsNotFound(err) {
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"fetching Cluster: %w", err,
		)
	}

	ctx = resolveTraceContext(
		ctx, &cluster, cluster.Status.TraceParent,
	)
	ctx, span := startSpan(
		ctx, "Cluster", "Reconcile", cluster.Name,
	)
	defer span.End()

	// Handle deletion.
	if !cluster.DeletionTimestamp.IsZero() {
		return r.reconcileDelete(ctx, &cluster)
	}

	// Ensure finalizer is set.
	if !controllerutil.ContainsFinalizer(&cluster, finalizerName) {
		controllerutil.AddFinalizer(&cluster, finalizerName)
		if err := r.Update(ctx, &cluster); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"adding finalizer: %w", err,
			)
		}
		return ctrl.Result{Requeue: true}, nil
	}

	// Validate spec.
	if err := r.validateSpec(ctx, &cluster); err != nil {
		log.Error(err, "Validation failed")
		return ctrl.Result{}, nil
	}

	// Route based on phase.
	switch cluster.Status.Phase {
	case "", v1alpha1.PhasePending:
		return r.reconcileCreate(ctx, &cluster)

	case v1alpha1.PhaseCreating, v1alpha1.PhaseUpdating:
		return r.reconcileInProgress(ctx, &cluster)

	case v1alpha1.PhaseRunning:
		// Check if spec changed.
		if cluster.Generation != cluster.Status.ObservedGeneration {
			return r.reconcileUpdate(ctx, &cluster)
		}
		// Check if reconcile was requested via annotation.
		if cluster.Annotations[reconcileRequestedAtAn] != "" &&
			cluster.Annotations[reconcileRequestedAtAn] !=
				cluster.Status.LastHandledReconcileAt {
			return r.reconcileFromAnnotation(
				ctx, &cluster,
			)
		}
		// Check governance readiness if configured.
		if result, done := r.reconcileGovernanceConfig(
			ctx, &cluster,
		); done {
			return result, nil
		}
		// If workload profiles were deferred, apply them
		// now that governance is ready.
		wpReady := meta.FindStatusCondition(
			cluster.Status.Conditions,
			v1alpha1.ConditionTypeWorkloadProfilesReady,
		)
		if wpReady != nil &&
			wpReady.Status != metav1.ConditionTrue {
			return r.reconcileUpdate(ctx, &cluster)
		}
		return r.reconcileStatusSync(ctx, &cluster)

	case v1alpha1.PhaseFailed:
		// Check if reconcile was requested via annotation.
		if cluster.Annotations[reconcileRequestedAtAn] != "" &&
			cluster.Annotations[reconcileRequestedAtAn] !=
				cluster.Status.LastHandledReconcileAt {
			return r.reconcileFromAnnotation(
				ctx, &cluster,
			)
		}
		// Retry if spec changed or retry annotation is set.
		if cluster.Generation != cluster.Status.ObservedGeneration ||
			cluster.Annotations[retryAnnotation] != "" {
			// Clear the retry annotation if present.
			if cluster.Annotations[retryAnnotation] != "" {
				delete(cluster.Annotations, retryAnnotation)
			}
			delete(
				cluster.Annotations,
				"cleanroom.azure.com/progress-count",
			)
			delete(
				cluster.Annotations,
				syncFailureCountAnn,
			)
			if err := r.Update(ctx, &cluster); err != nil {
				return ctrl.Result{}, fmt.Errorf(
					"clearing retry/progress annotations: %w",
					err,
				)
			}
			r.event(
				&cluster, corev1.EventTypeNormal,
				"Retrying",
				"Retry requested via annotation",
			)
			// Set a transitional phase before doing any
			// work so the environment controller does not
			// see a stale Failed phase after the retry
			// annotation has been cleared.
			// Check if previously created before
			// clearing conditions.
			wasCreated := meta.IsStatusConditionTrue(
				cluster.Status.Conditions,
				v1alpha1.ConditionTypeClusterCreated,
			)
			cluster.Status.Conditions = nil
			if wasCreated {
				cluster.Status.Phase =
					v1alpha1.PhaseUpdating
			} else {
				cluster.Status.Phase =
					v1alpha1.PhaseCreating
			}
			if err := r.Status().Update(
				ctx, &cluster,
			); err != nil {
				return ctrl.Result{}, fmt.Errorf(
					"setting transitional phase: %w",
					err,
				)
			}
			// Wait for governance ConfigMap before
			// retrying as update (profiles are needed).
			// Skip the gate for initial create — profiles
			// are deferred.
			if wasCreated {
				if result, done :=
					r.reconcileGovernanceConfig(
						ctx, &cluster,
					); done {
					return result, nil
				}
			}
			// If the cluster was previously created
			// successfully, retry as an update.
			if wasCreated {
				return r.reconcileUpdate(ctx, &cluster)
			}
			return r.reconcileCreate(ctx, &cluster)
		}
		return ctrl.Result{RequeueAfter: clusterStatusSyncInterval}, nil

	default:
		log.Info("Unknown phase, requeueing",
			"phase", cluster.Status.Phase)
		return ctrl.Result{RequeueAfter: clusterRequeueDelay}, nil
	}
}

func (r *ClusterReconciler) reconcileCreate(
	ctx context.Context,
	cluster *v1alpha1.Cluster,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "Cluster", "Create", cluster.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	log.Info("Creating cluster", "name", cluster.Name)
	r.event(
		cluster, corev1.EventTypeNormal,
		"Creating", "Cluster creation initiated",
	)

	deferGov :=
		r.getGovernanceConfigRef(cluster) != ""
	input, err := r.buildPutInput(
		ctx, cluster, deferGov,
	)
	if err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"building create input: %w", err,
		)
	}

	opLocation, err := r.ClusterClient.CreateCluster(
		ctx, cluster.Name, input,
	)
	if err != nil {
		recordError(span, err)
		r.setPhase(ctx, cluster, v1alpha1.PhaseFailed)
		cluster.Status.ObservedGeneration = cluster.Generation
		meta.SetStatusCondition(
			&cluster.Status.Conditions,
			metav1.Condition{
				Type:    v1alpha1.ConditionTypeClusterCreated,
				Status:  metav1.ConditionFalse,
				Reason:  "CreateFailed",
				Message: err.Error(),
			},
		)
		_ = r.Status().Update(ctx, cluster)
		r.eventf(
			cluster, corev1.EventTypeWarning,
			"CreateFailed", "CreateCluster API error: %s",
			err.Error(),
		)
		return ctrl.Result{RequeueAfter: clusterRequeueDelay}, nil
	}

	cluster.Status.Phase = v1alpha1.PhaseCreating
	cluster.Status.OperationId = opLocation
	cluster.Status.ObservedGeneration = cluster.Generation
	cluster.Status.TraceParent,
		cluster.Status.LastOperationTraceId =
		saveTrace(ctx)
	meta.SetStatusCondition(
		&cluster.Status.Conditions,
		metav1.Condition{
			Type:    v1alpha1.ConditionTypeClusterCreated,
			Status:  metav1.ConditionFalse,
			Reason:  "Creating",
			Message: "Cluster creation in progress",
		},
	)
	if err := r.Status().Update(ctx, cluster); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after create: %w", err,
		)
	}

	return ctrl.Result{RequeueAfter: clusterRequeueDelay}, nil
}

func (r *ClusterReconciler) reconcileUpdate(
	ctx context.Context,
	cluster *v1alpha1.Cluster,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "Cluster", "Update", cluster.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)

	// Check infraType immutability.
	getInput := r.buildGetInput(cluster)
	existing, err := r.ClusterClient.GetCluster(
		ctx, cluster.Name, getInput,
	)
	if err != nil {
		log.Error(err, "Failed to get cluster for update")
		return ctrl.Result{RequeueAfter: clusterRequeueDelay}, nil
	}

	infraTypeChanged := false
	var immutabilityMsg string
	if existing != nil && existing.InfraType != cluster.Spec.InfraType {
		infraTypeChanged = true
		immutabilityMsg = fmt.Sprintf(
			"infraType cannot be changed from %s to %s",
			existing.InfraType, cluster.Spec.InfraType,
		)
	} else if existing == nil {
		// The cluster should exist in the provider since we were in
		// Running state. A nil result likely means infraType was
		// changed (provider can't find cluster with new type).
		infraTypeChanged = true
		immutabilityMsg = fmt.Sprintf(
			"infraType is immutable; cluster not found with "+
				"infraType %s", cluster.Spec.InfraType,
		)
	}

	if infraTypeChanged {
		meta.SetStatusCondition(
			&cluster.Status.Conditions,
			metav1.Condition{
				Type:    v1alpha1.ConditionTypeValidated,
				Status:  metav1.ConditionFalse,
				Reason:  "InfraTypeImmutable",
				Message: immutabilityMsg,
			},
		)
		cluster.Status.Phase = v1alpha1.PhaseFailed
		_ = r.Status().Update(ctx, cluster)
		r.event(
			cluster, corev1.EventTypeWarning,
			"ValidationFailed", immutabilityMsg,
		)
		recordError(span, fmt.Errorf("%s",
			immutabilityMsg))
		return ctrl.Result{}, nil
	}

	log.Info("Updating cluster", "name", cluster.Name)
	r.event(
		cluster, corev1.EventTypeNormal,
		"Updating", "Cluster update initiated",
	)

	input, err := r.buildPutInput(ctx, cluster, false)
	if err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"building update input: %w", err,
		)
	}

	opLocation, err := r.ClusterClient.UpdateCluster(
		ctx, cluster.Name, input,
	)
	if err != nil {
		recordError(span, err)
		r.setPhase(ctx, cluster, v1alpha1.PhaseFailed)
		cluster.Status.ObservedGeneration = cluster.Generation
		meta.SetStatusCondition(
			&cluster.Status.Conditions,
			metav1.Condition{
				Type:    v1alpha1.ConditionTypeReady,
				Status:  metav1.ConditionFalse,
				Reason:  "UpdateFailed",
				Message: err.Error(),
			},
		)
		_ = r.Status().Update(ctx, cluster)
		r.eventf(
			cluster, corev1.EventTypeWarning,
			"UpdateFailed", "UpdateCluster API error: %s",
			err.Error(),
		)
		return ctrl.Result{RequeueAfter: clusterRequeueDelay}, nil
	}

	cluster.Status.Phase = v1alpha1.PhaseUpdating
	cluster.Status.OperationId = opLocation
	cluster.Status.ObservedGeneration = cluster.Generation
	cluster.Status.TraceParent,
		cluster.Status.LastOperationTraceId =
		saveTrace(ctx)
	if err := r.Status().Update(ctx, cluster); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after update: %w", err,
		)
	}

	return ctrl.Result{RequeueAfter: clusterRequeueDelay}, nil
}

// reconcileFromAnnotation handles the reconcile annotation
// by acknowledging the request, clearing the annotation,
// and triggering an update or create.
func (r *ClusterReconciler) reconcileFromAnnotation(
	ctx context.Context,
	cluster *v1alpha1.Cluster,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "Cluster", "ReconcileFromAnnotation",
		cluster.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	log.Info("Reconcile requested via annotation")

	requestedAt :=
		cluster.Annotations[reconcileRequestedAtAn]

	// Clear the annotation.
	delete(cluster.Annotations, reconcileRequestedAtAn)
	if err := r.Update(ctx, cluster); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"clearing reconcile annotation: %w", err,
		)
	}

	r.event(
		cluster, corev1.EventTypeNormal,
		"Reconciling",
		"Reconcile requested via annotation",
	)

	// Check if previously created before clearing
	// conditions.
	wasCreated := meta.IsStatusConditionTrue(
		cluster.Status.Conditions,
		v1alpha1.ConditionTypeClusterCreated,
	)

	// Acknowledge in status and set a transitional phase
	// so the CLI wait loop does not see stale Failed.
	cluster.Status.LastHandledReconcileAt = requestedAt
	cluster.Status.Conditions = nil
	if wasCreated {
		cluster.Status.Phase = v1alpha1.PhaseUpdating
	} else {
		cluster.Status.Phase = v1alpha1.PhaseCreating
	}
	if err := r.Status().Update(ctx, cluster); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"acknowledging reconcile: %w", err,
		)
	}

	// Wait for governance ConfigMap before retrying as
	// update. Skip the gate for initial create — profiles
	// are deferred.
	if wasCreated {
		if result, done := r.reconcileGovernanceConfig(
			ctx, cluster,
		); done {
			return result, nil
		}
	}

	// If previously created, retry as update.
	if wasCreated {
		return r.reconcileUpdate(ctx, cluster)
	}
	return r.reconcileCreate(ctx, cluster)
}

func (r *ClusterReconciler) reconcileInProgress(
	ctx context.Context,
	cluster *v1alpha1.Cluster,
) (ctrl.Result, error) {
	ctx = restoreTrace(ctx, cluster.Status.TraceParent)

	log := ctrllog.FromContext(ctx)

	if cluster.Status.OperationId != "" {
		op, err := r.ClusterClient.GetOperation(
			ctx, cluster.Status.OperationId,
		)
		if err != nil {
			log.Error(err, "Failed to poll operation")
			meta.SetStatusCondition(
				&cluster.Status.Conditions,
				metav1.Condition{
					Type:   v1alpha1.ConditionTypeReady,
					Status: metav1.ConditionFalse,
					Reason: "PollOperationFailed",
					Message: fmt.Sprintf(
						"Failed to poll operation: %s",
						err.Error()),
				},
			)
			_ = r.Status().Update(ctx, cluster)
			return ctrl.Result{RequeueAfter: clusterRequeueDelay}, nil
		}

		// Operation not found (provider client restarted).
		// Fall through to get-based recovery.
		if op == nil {
			log.Info(
				"Operation not found, falling back to get",
				"operationId", cluster.Status.OperationId,
			)
			r.event(
				cluster, corev1.EventTypeWarning,
				"OperationLost",
				"Operation not found, recovering via GET",
			)
		} else if op.Status == "Running" || op.Status == "Queued" {
			r.emitProgressEvents(ctx, cluster, op)
			log.Info("Operation still in progress",
				"operationId", cluster.Status.OperationId,
				"status", op.Status)
			return ctrl.Result{RequeueAfter: clusterRequeueDelay}, nil
		} else if op.Status == "Succeeded" {
			r.emitProgressEvents(ctx, cluster, op)
			ctx, span := startSpan(
				ctx, "Cluster",
				"OperationComplete", cluster.Name,
			)
			defer span.End()

			log = ctrllog.FromContext(ctx)
			log.Info("Operation succeeded",
				"operationId", cluster.Status.OperationId)
			cluster.Status.OperationId = ""
			cluster.Status.TraceParent = ""
			delete(cluster.Annotations, "cleanroom.azure.com/progress-count")
			if err := r.Update(ctx, cluster); err != nil {
				log.Error(err,
					"Failed to clear progress-count annotation")
			}
			opType := "Created"
			if cluster.Status.Phase == v1alpha1.PhaseUpdating {
				opType = "Updated"
				// If this was the deferred governance
				// update, mark profiles as applied.
				govRef :=
					r.getGovernanceConfigRef(cluster)
				if govRef != "" {
					meta.SetStatusCondition(
						&cluster.Status.Conditions,
						metav1.Condition{
							Type:   v1alpha1.ConditionTypeWorkloadProfilesReady,
							Status: metav1.ConditionTrue,
							Reason: "Applied",
							Message: "Workload profiles " +
								"applied successfully",
						},
					)
				}
			}
			r.eventf(
				cluster, corev1.EventTypeNormal,
				opType, "Cluster %s successfully",
				strings.ToLower(opType),
			)
			return r.syncStatusFromProvider(ctx, cluster)
		} else if op.Status == "Failed" {
			r.emitProgressEvents(ctx, cluster, op)
			ctx, span := startSpan(
				ctx, "Cluster",
				"OperationFailed", cluster.Name,
			)
			defer span.End()
			log = ctrllog.FromContext(ctx)
			opErr := fmt.Errorf(
				"%s", string(op.Error),
			)
			recordError(span, opErr)
			log.Info("Operation failed",
				"operationId", cluster.Status.OperationId)
			cluster.Status.OperationId = ""
			cluster.Status.TraceParent = ""
			delete(cluster.Annotations, "cleanroom.azure.com/progress-count")
			if err := r.Update(ctx, cluster); err != nil {
				log.Error(err,
					"Failed to clear progress-count annotation")
			}
			cluster.Status.Phase = v1alpha1.PhaseFailed
			meta.SetStatusCondition(
				&cluster.Status.Conditions,
				metav1.Condition{
					Type:    v1alpha1.ConditionTypeReady,
					Status:  metav1.ConditionFalse,
					Reason:  "OperationFailed",
					Message: string(op.Error),
				},
			)
			if err := r.Status().Update(ctx, cluster); err != nil {
				return ctrl.Result{}, fmt.Errorf(
					"updating status after op failure: %w", err,
				)
			}
			r.eventf(
				cluster, corev1.EventTypeWarning,
				"OperationFailed",
				"Operation failed: %s", string(op.Error),
			)
			return ctrl.Result{RequeueAfter: clusterStatusSyncInterval}, nil
		}
	}

	// Recovery: check if the cluster exists via get API.
	return r.syncStatusFromProvider(ctx, cluster)
}

func (r *ClusterReconciler) emitProgressEvents(
	ctx context.Context,
	cluster *v1alpha1.Cluster,
	op *client.OperationResponse,
) {
	if r.Recorder == nil || len(op.Progress) == 0 {
		return
	}
	// Emit events only for progress items not yet seen. The operator
	// stores the count of already-emitted items in an annotation so
	// that restarts don't re-emit old progress.
	const ann = "cleanroom.azure.com/progress-count"
	var seen int
	if v, ok := cluster.Annotations[ann]; ok {
		fmt.Sscanf(v, "%d", &seen)
	}
	if len(op.Progress) <= seen {
		return
	}
	for i := seen; i < len(op.Progress); i++ {
		r.Recorder.Eventf(
			cluster,
			corev1.EventTypeNormal,
			"OperationProgress",
			"%s", op.Progress[i],
		)
	}
	if cluster.Annotations == nil {
		cluster.Annotations = map[string]string{}
	}
	cluster.Annotations[ann] = fmt.Sprintf(
		"%d", len(op.Progress),
	)
	if err := r.Update(ctx, cluster); err != nil {
		log := ctrllog.FromContext(ctx)
		log.Error(err,
			"Failed to persist progress-count annotation")
	}
}

func (r *ClusterReconciler) incrementSyncFailureCount(
	ctx context.Context,
	cluster *v1alpha1.Cluster,
) int {
	var count int
	if v, ok := cluster.Annotations[syncFailureCountAnn]; ok {
		fmt.Sscanf(v, "%d", &count)
	}
	count++
	if cluster.Annotations == nil {
		cluster.Annotations = map[string]string{}
	}
	cluster.Annotations[syncFailureCountAnn] = fmt.Sprintf(
		"%d", count,
	)
	if err := r.Update(ctx, cluster); err != nil {
		log := ctrllog.FromContext(ctx)
		log.Error(err,
			"Failed to persist sync-failure-count")
	}
	return count
}

func (r *ClusterReconciler) clearSyncFailureCount(
	ctx context.Context,
	cluster *v1alpha1.Cluster,
) {
	if _, ok := cluster.Annotations[syncFailureCountAnn]; !ok {
		return
	}
	delete(cluster.Annotations, syncFailureCountAnn)
	if err := r.Update(ctx, cluster); err != nil {
		log := ctrllog.FromContext(ctx)
		log.Error(err,
			"Failed to clear sync-failure-count")
	}
}

func (r *ClusterReconciler) reconcileDelete(
	ctx context.Context,
	cluster *v1alpha1.Cluster,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "Cluster", "Delete", cluster.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)

	if !controllerutil.ContainsFinalizer(cluster, finalizerName) {
		return ctrl.Result{}, nil
	}

	log.Info("Deleting cluster", "name", cluster.Name)
	r.event(
		cluster, corev1.EventTypeNormal,
		"Deleting", "Cluster deletion initiated",
	)

	cluster.Status.Phase = v1alpha1.PhaseDeleting
	_ = r.Status().Update(ctx, cluster)

	if cluster.Spec.DeletionPolicy !=
		v1alpha1.DeletionPolicyDelete {
		log.Info(
			"Retaining external cluster "+
				"(deletionPolicy is not \"delete\")",
			"name", cluster.Name,
			"deletionPolicy",
			cluster.Spec.DeletionPolicy,
		)
		r.event(
			cluster, corev1.EventTypeNormal,
			"Retained",
			"External cluster retained per "+
				"deletionPolicy",
		)
	} else {
		getInput := r.buildGetInput(cluster)
		if err := r.ClusterClient.DeleteCluster(
			ctx, cluster.Name, getInput,
		); err != nil {
			log.Error(
				err,
				"Failed to delete cluster via provider",
			)
			recordError(span, err)
			r.eventf(
				cluster, corev1.EventTypeWarning,
				"DeleteFailed",
				"Failed to delete cluster: %s",
				err.Error(),
			)
			return ctrl.Result{
				RequeueAfter: clusterRequeueDelay,
			}, nil
		}
	}

	controllerutil.RemoveFinalizer(cluster, finalizerName)
	if err := r.Update(ctx, cluster); err != nil {
		if apierrors.IsNotFound(err) {
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"removing finalizer: %w", err,
		)
	}

	log.Info("Cluster deleted", "name", cluster.Name)
	r.event(
		cluster, corev1.EventTypeNormal,
		"Deleted", "Cluster deleted",
	)
	return ctrl.Result{}, nil
}

func (r *ClusterReconciler) reconcileStatusSync(
	ctx context.Context,
	cluster *v1alpha1.Cluster,
) (ctrl.Result, error) {
	return r.syncStatusFromProvider(ctx, cluster)
}

// reconcileGovernanceConfig checks if the governance ConfigMap
// exists when a profile-level governanceConfigRef is set.
// Returns (result, true) if the reconcile should stop (waiting
// for ConfigMap).
func (r *ClusterReconciler) reconcileGovernanceConfig(
	ctx context.Context,
	cluster *v1alpha1.Cluster,
) (ctrl.Result, bool) {
	ref := r.getGovernanceConfigRef(cluster)
	if ref == "" {
		return ctrl.Result{}, false
	}

	// Already done.
	if meta.IsStatusConditionTrue(
		cluster.Status.Conditions,
		v1alpha1.ConditionTypeGovernanceReady,
	) {
		return ctrl.Result{}, false
	}

	log := ctrllog.FromContext(ctx)

	var cm corev1.ConfigMap
	key := ctrlclient.ObjectKey{
		Namespace: cluster.Namespace,
		Name:      ref,
	}
	if err := r.Get(ctx, key, &cm); err != nil {
		if apierrors.IsNotFound(err) {
			log.Info(
				"Governance ConfigMap not found, waiting",
				"configmap", ref,
			)
			meta.SetStatusCondition(
				&cluster.Status.Conditions,
				metav1.Condition{
					Type:   v1alpha1.ConditionTypeGovernanceReady,
					Status: metav1.ConditionFalse,
					Reason: "WaitingForConfigMap",
					Message: fmt.Sprintf(
						"ConfigMap %s not found", ref,
					),
				},
			)
			_ = r.Status().Update(ctx, cluster)
			return ctrl.Result{
				RequeueAfter: clusterRequeueDelay,
			}, true
		}
		log.Error(err, "Failed to get governance ConfigMap")
		r.setPhase(ctx, cluster, v1alpha1.PhaseFailed)
		meta.SetStatusCondition(
			&cluster.Status.Conditions,
			metav1.Condition{
				Type:   v1alpha1.ConditionTypeGovernanceReady,
				Status: metav1.ConditionFalse,
				Reason: "ConfigMapError",
				Message: fmt.Sprintf(
					"Failed to get ConfigMap %s: %s",
					ref, err.Error()),
			},
		)
		_ = r.Status().Update(ctx, cluster)
		return ctrl.Result{}, true
	}

	// ConfigMap exists — check if configurationUrl is
	// populated. The WorkloadGovernance controller writes
	// this key as its final step; until then the Cluster
	// should wait.
	if cm.Data["configurationUrl"] == "" {
		log.Info(
			"Governance ConfigMap exists but "+
				"configurationUrl not yet set, waiting",
			"configmap", ref,
		)
		meta.SetStatusCondition(
			&cluster.Status.Conditions,
			metav1.Condition{
				Type:   v1alpha1.ConditionTypeGovernanceReady,
				Status: metav1.ConditionFalse,
				Reason: "WaitingForConfigurationUrl",
				Message: fmt.Sprintf(
					"ConfigMap %s exists but "+
						"configurationUrl not set",
					ref,
				),
			},
		)
		_ = r.Status().Update(ctx, cluster)
		return ctrl.Result{
			RequeueAfter: clusterRequeueDelay,
		}, true
	}

	// ConfigMap has configurationUrl — check whether flex
	// node profile also needs policySigningCertPem.
	if cluster.Spec.FlexNodeProfile != nil &&
		cluster.Spec.FlexNodeProfile.Enabled &&
		cluster.Spec.FlexNodeProfile.
			GovernanceConfigRef != "" &&
		cluster.Spec.FlexNodeProfile.
			PolicySigningCertPem == "" &&
		cluster.Spec.FlexNodeProfile.
			PolicySigningCertSecretRef == nil &&
		cm.Data["policySigningCertPem"] == "" {
		log.Info(
			"Governance ConfigMap exists but "+
				"policySigningCertPem not yet set, "+
				"waiting",
			"configmap", ref,
		)
		meta.SetStatusCondition(
			&cluster.Status.Conditions,
			metav1.Condition{
				Type:   v1alpha1.ConditionTypeGovernanceReady,
				Status: metav1.ConditionFalse,
				Reason: "WaitingForPolicySigningCert",
				Message: fmt.Sprintf(
					"ConfigMap %s exists but "+
						"policySigningCertPem not set",
					ref,
				),
			},
		)
		_ = r.Status().Update(ctx, cluster)
		return ctrl.Result{
			RequeueAfter: clusterRequeueDelay,
		}, true
	}

	// ConfigMap has configurationUrl — governance is ready.
	log.Info("Governance ConfigMap found",
		"configmap", ref,
		"contractState", cm.Data["contractState"])

	meta.SetStatusCondition(
		&cluster.Status.Conditions,
		metav1.Condition{
			Type:   v1alpha1.ConditionTypeGovernanceReady,
			Status: metav1.ConditionTrue,
			Reason: "ConfigMapReady",
			Message: fmt.Sprintf(
				"Governance ConfigMap %s is ready", ref,
			),
		},
	)
	_ = r.Status().Update(ctx, cluster)

	r.event(cluster, corev1.EventTypeNormal,
		"GovernanceReady",
		fmt.Sprintf("Governance ConfigMap %s is ready",
			ref))

	return ctrl.Result{}, false
}

// getGovernanceConfigRef returns the governance ConfigMap
// reference from profile-level fields. Currently only
// kserve-inferencing supports this.
func (r *ClusterReconciler) getGovernanceConfigRef(
	cluster *v1alpha1.Cluster,
) string {
	if cluster.Spec.InferencingWorkloadProfile != nil &&
		cluster.Spec.InferencingWorkloadProfile.
			KServeProfile != nil &&
		cluster.Spec.InferencingWorkloadProfile.
			KServeProfile.GovernanceConfigRef != "" {
		return cluster.Spec.InferencingWorkloadProfile.
			KServeProfile.GovernanceConfigRef
	}
	if cluster.Spec.FlexNodeProfile != nil &&
		cluster.Spec.FlexNodeProfile.
			GovernanceConfigRef != "" {
		return cluster.Spec.FlexNodeProfile.
			GovernanceConfigRef
	}
	return ""
}

func (r *ClusterReconciler) syncStatusFromProvider(
	ctx context.Context,
	cluster *v1alpha1.Cluster,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	getInput := r.buildGetInput(cluster)
	resp, err := r.ClusterClient.GetCluster(
		ctx, cluster.Name, getInput,
	)
	if err != nil {
		log.Error(err, "Failed to get cluster from provider")
		failMsg := fmt.Sprintf(
			"Failed to get cluster from provider: %s",
			err.Error())
		meta.SetStatusCondition(
			&cluster.Status.Conditions,
			metav1.Condition{
				Type:    v1alpha1.ConditionTypeReady,
				Status:  metav1.ConditionFalse,
				Reason:  "SyncStatusFailed",
				Message: failMsg,
			},
		)
		_ = r.Status().Update(ctx, cluster)

		// Track consecutive sync failures. After
		// maxSyncFailures the resource transitions
		// to Failed so the parent environment
		// reflects the actual state.
		count := r.incrementSyncFailureCount(
			ctx, cluster,
		)
		if count >= maxSyncFailures {
			log.Info(
				"Max sync failures reached, "+
					"transitioning to Failed",
				"count", count,
			)
			cluster.Status.Phase = v1alpha1.PhaseFailed
			r.event(
				cluster,
				corev1.EventTypeWarning,
				"SyncFailed",
				fmt.Sprintf(
					"Cluster sync failed %d "+
						"consecutive times: %s",
					count, err.Error()),
			)
			_ = r.Status().Update(ctx, cluster)
		}

		return ctrl.Result{RequeueAfter: clusterRequeueDelay}, nil
	}

	if resp == nil {
		// Cluster not found in provider. If we were Creating, it may
		// have never been created.
		if cluster.Status.Phase == v1alpha1.PhaseCreating {
			log.Info(
				"Cluster not found in provider, retrying create",
			)
			cluster.Status.Phase = v1alpha1.PhasePending
			cluster.Status.OperationId = ""
			if err := r.Status().Update(ctx, cluster); err != nil {
				return ctrl.Result{}, fmt.Errorf(
					"resetting phase: %w", err,
				)
			}
			return ctrl.Result{Requeue: true}, nil
		}

		// The cluster was previously running but the
		// provider now returns nil (backing resources may
		// have been deleted out of band). Track as a sync
		// failure so we eventually transition to Failed.
		if cluster.Status.Phase ==
			v1alpha1.PhaseRunning {
			log.Info(
				"Cluster not found in provider " +
					"but was previously running",
			)
			failMsg := "Cluster not found in provider" +
				" (backing resources may have been" +
				" deleted)"
			meta.SetStatusCondition(
				&cluster.Status.Conditions,
				metav1.Condition{
					Type:    v1alpha1.ConditionTypeReady,
					Status:  metav1.ConditionFalse,
					Reason:  "SyncStatusFailed",
					Message: failMsg,
				},
			)
			_ = r.Status().Update(ctx, cluster)

			count := r.incrementSyncFailureCount(
				ctx, cluster,
			)
			if count >= maxSyncFailures {
				log.Info(
					"Max sync failures reached, "+
						"transitioning to Failed",
					"count", count,
				)
				cluster.Status.Phase =
					v1alpha1.PhaseFailed
				r.event(
					cluster,
					corev1.EventTypeWarning,
					"SyncFailed",
					fmt.Sprintf(
						"Cluster not found in"+
							" provider after %d"+
							" consecutive checks",
						count),
				)
				_ = r.Status().Update(ctx, cluster)
			}
		}

		return ctrl.Result{RequeueAfter: clusterStatusSyncInterval}, nil
	}

	// Map provider response to CR status.
	r.mapResponseToStatus(cluster, resp)
	cluster.Status.Phase = v1alpha1.PhaseRunning
	cluster.Status.OperationId = ""
	r.clearSyncFailureCount(ctx, cluster)
	meta.SetStatusCondition(
		&cluster.Status.Conditions,
		metav1.Condition{
			Type:    v1alpha1.ConditionTypeClusterCreated,
			Status:  metav1.ConditionTrue,
			Reason:  "ClusterReady",
			Message: "Cluster is running",
		},
	)

	// When governance profiles were deferred during
	// initial create, keep Ready=False until the
	// deferred update applies them.
	govRef := r.getGovernanceConfigRef(cluster)
	wpReady := meta.FindStatusCondition(
		cluster.Status.Conditions,
		v1alpha1.ConditionTypeWorkloadProfilesReady,
	)
	if govRef != "" &&
		(wpReady == nil ||
			wpReady.Status != metav1.ConditionTrue) {
		meta.SetStatusCondition(
			&cluster.Status.Conditions,
			metav1.Condition{
				Type:   v1alpha1.ConditionTypeWorkloadProfilesReady,
				Status: metav1.ConditionFalse,
				Reason: "Deferred",
				Message: "Workload profiles deferred " +
					"pending governance setup",
			},
		)
		meta.SetStatusCondition(
			&cluster.Status.Conditions,
			metav1.Condition{
				Type:   v1alpha1.ConditionTypeReady,
				Status: metav1.ConditionFalse,
				Reason: "WorkloadProfilesPending",
				Message: "Cluster running but workload " +
					"profiles not yet applied",
			},
		)
	} else {
		meta.SetStatusCondition(
			&cluster.Status.Conditions,
			metav1.Condition{
				Type:    v1alpha1.ConditionTypeReady,
				Status:  metav1.ConditionTrue,
				Reason:  "Ready",
				Message: "Cluster is running and healthy",
			},
		)
	}

	if err := r.Status().Update(ctx, cluster); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status from provider: %w", err,
		)
	}

	return ctrl.Result{RequeueAfter: clusterStatusSyncInterval}, nil
}

func (r *ClusterReconciler) validateSpec(
	ctx context.Context,
	cluster *v1alpha1.Cluster,
) error {
	var errs []string

	// When governanceConfigRef is set on the kserve profile
	// the environment controller manages configuration —
	// skip configurationUrl checks.
	hasGovRef := r.getGovernanceConfigRef(cluster) != ""

	if cluster.Spec.AnalyticsWorkloadProfile != nil &&
		cluster.Spec.AnalyticsWorkloadProfile.Enabled &&
		cluster.Spec.AnalyticsWorkloadProfile.ConfigurationUrl == "" {
		errs = append(errs,
			"analyticsWorkloadProfile.configurationUrl is "+
				"required when analytics is enabled")
	}

	if !hasGovRef &&
		cluster.Spec.InferencingWorkloadProfile != nil &&
		cluster.Spec.InferencingWorkloadProfile.KServeProfile != nil &&
		cluster.Spec.InferencingWorkloadProfile.KServeProfile.Enabled &&
		cluster.Spec.InferencingWorkloadProfile.KServeProfile.ConfigurationUrl == "" {
		errs = append(errs,
			"inferencingWorkloadProfile.kserveProfile."+
				"configurationUrl is required when KServe is enabled")
	}

	if cluster.Spec.FlexNodeProfile != nil &&
		cluster.Spec.FlexNodeProfile.Enabled {
		hasCert := cluster.Spec.FlexNodeProfile.PolicySigningCertPem != ""
		hasRef := cluster.Spec.FlexNodeProfile.PolicySigningCertSecretRef != nil
		hasGovRef := cluster.Spec.FlexNodeProfile.GovernanceConfigRef != ""
		if !hasCert && !hasRef && !hasGovRef {
			errs = append(errs,
				"flexNodeProfile.policySigningCertPem, "+
					"policySigningCertSecretRef, or "+
					"governanceConfigRef is required "+
					"when flex nodes are enabled")
		}
		if hasCert && hasRef {
			errs = append(errs,
				"flexNodeProfile.policySigningCertPem and "+
					"policySigningCertSecretRef are mutually "+
					"exclusive")
		}
	}

	if len(errs) > 0 {
		msg := fmt.Sprintf("validation errors: %v", errs)
		meta.SetStatusCondition(
			&cluster.Status.Conditions,
			metav1.Condition{
				Type:    v1alpha1.ConditionTypeValidated,
				Status:  metav1.ConditionFalse,
				Reason:  "ValidationFailed",
				Message: msg,
			},
		)
		cluster.Status.Phase = v1alpha1.PhaseFailed
		cluster.Status.ObservedGeneration = cluster.Generation
		_ = r.Status().Update(ctx, cluster)
		r.event(
			cluster, corev1.EventTypeWarning,
			"ValidationFailed", msg,
		)
		return fmt.Errorf("%s", msg)
	}

	meta.SetStatusCondition(
		&cluster.Status.Conditions,
		metav1.Condition{
			Type:    v1alpha1.ConditionTypeValidated,
			Status:  metav1.ConditionTrue,
			Reason:  "Valid",
			Message: "Spec validation passed",
		},
	)
	return nil
}

// ensureSshKeySecret creates or retrieves the well-known
// SSH key Secret. The Secret is created without an
// ownerReference so it persists across Environment
// deletions and can be reused by multiple environments.
func (r *ClusterReconciler) ensureSshKeySecret(
	ctx context.Context,
	namespace string,
) (privateKey string, publicKey string, err error) {
	log := ctrllog.FromContext(ctx)

	var secret corev1.Secret
	key := ctrlclient.ObjectKey{
		Namespace: namespace,
		Name:      flexNodeSshSecretName,
	}
	if err := r.Get(ctx, key, &secret); err == nil {
		log.Info(
			"Reusing existing SSH key Secret",
			"secret", flexNodeSshSecretName,
		)
		return string(secret.Data[sshPrivateKeyField]),
			string(secret.Data[sshPublicKeyField]),
			nil
	} else if !apierrors.IsNotFound(err) {
		return "", "", fmt.Errorf(
			"checking SSH key Secret: %w", err,
		)
	}

	log.Info(
		"Generating RSA-4096 SSH key pair",
		"secret", flexNodeSshSecretName,
	)

	rsaKey, err := rsa.GenerateKey(rand.Reader, 4096)
	if err != nil {
		return "", "", fmt.Errorf(
			"generating RSA key: %w", err,
		)
	}

	privPem := pem.EncodeToMemory(&pem.Block{
		Type:  "RSA PRIVATE KEY",
		Bytes: x509.MarshalPKCS1PrivateKey(rsaKey),
	})

	sshPub, err := ssh.NewPublicKey(&rsaKey.PublicKey)
	if err != nil {
		return "", "", fmt.Errorf(
			"creating SSH public key: %w", err,
		)
	}
	pubBytes := ssh.MarshalAuthorizedKey(sshPub)

	secret = corev1.Secret{
		ObjectMeta: metav1.ObjectMeta{
			Name:      flexNodeSshSecretName,
			Namespace: namespace,
			Labels: map[string]string{
				"app.kubernetes.io/managed-by": "cleanroom-operator",
			},
		},
		Type: corev1.SecretTypeOpaque,
		Data: map[string][]byte{
			sshPrivateKeyField: privPem,
			sshPublicKeyField:  pubBytes,
		},
	}

	if err := r.Create(ctx, &secret); err != nil {
		if apierrors.IsAlreadyExists(err) {
			// Race: another reconcile created it.
			if err := r.Get(
				ctx, key, &secret,
			); err != nil {
				return "", "", fmt.Errorf(
					"getting SSH key Secret after "+
						"race: %w", err,
				)
			}
			return string(
					secret.Data[sshPrivateKeyField]),
				string(
					secret.Data[sshPublicKeyField]),
				nil
		}
		return "", "", fmt.Errorf(
			"creating SSH key Secret: %w", err,
		)
	}

	log.Info(
		"SSH key Secret created",
		"secret", flexNodeSshSecretName,
	)
	return string(privPem), string(pubBytes), nil
}

func (r *ClusterReconciler) buildPutInput(
	ctx context.Context,
	cluster *v1alpha1.Cluster,
	deferGovernanceProfiles bool,
) (*client.PutClusterInput, error) {
	input := &client.PutClusterInput{
		InfraType: cluster.Spec.InfraType,
	}

	if cluster.Spec.ObservabilityProfile != nil {
		input.ObservabilityProfile = &client.ObservabilityProfileInput{
			Enabled: cluster.Spec.ObservabilityProfile.Enabled,
		}
	}

	if cluster.Spec.MonitoringProfile != nil {
		input.MonitoringProfile = &client.MonitoringProfileInput{
			Enabled: cluster.Spec.MonitoringProfile.Enabled,
		}
	}

	if cluster.Spec.AnalyticsWorkloadProfile != nil {
		a := cluster.Spec.AnalyticsWorkloadProfile
		apiInput := &client.AnalyticsWorkloadProfileInput{
			Enabled:                a.Enabled,
			ConfigurationUrl:       a.ConfigurationUrl,
			ConfigurationUrlCaCert: a.ConfigurationUrlCaCert,
		}
		if a.SecurityPolicyCreationOption != "" {
			apiInput.SecurityPolicy = &client.SecurityPolicyInput{
				PolicyCreationOption: a.SecurityPolicyCreationOption,
			}
		}
		if a.PoolProfile != nil {
			apiInput.PoolProfile = &client.WorkloadPoolInput{
				NodeCount: a.PoolProfile.NodeCount,
			}
		}
		input.AnalyticsWorkloadProfile = apiInput
	}

	// Skip KServe profile when deferring governance
	// profiles — it depends on configurationUrl from
	// the governance ConfigMap.
	skipKServe := deferGovernanceProfiles &&
		cluster.Spec.InferencingWorkloadProfile != nil &&
		cluster.Spec.InferencingWorkloadProfile.
			KServeProfile != nil &&
		cluster.Spec.InferencingWorkloadProfile.
			KServeProfile.GovernanceConfigRef != ""
	if !skipKServe &&
		cluster.Spec.InferencingWorkloadProfile != nil &&
		cluster.Spec.InferencingWorkloadProfile.KServeProfile != nil {
		k := cluster.Spec.InferencingWorkloadProfile.KServeProfile
		kInput := &client.KServeProfileInput{
			Enabled:                k.Enabled,
			ConfigurationUrl:       k.ConfigurationUrl,
			ConfigurationUrlCaCert: k.ConfigurationUrlCaCert,
		}
		if k.SecurityPolicyCreationOption != "" {
			kInput.SecurityPolicy = &client.SecurityPolicyInput{
				PolicyCreationOption: k.SecurityPolicyCreationOption,
			}
		}
		input.InferencingWorkloadProfile =
			&client.InferencingWorkloadProfileInput{
				KServeProfile: kInput,
			}
	}

	// Skip FlexNode profile when deferring governance
	// profiles — it depends on policySigningCert from
	// the governance ConfigMap.
	skipFlexNode := deferGovernanceProfiles &&
		cluster.Spec.FlexNodeProfile != nil &&
		cluster.Spec.FlexNodeProfile.GovernanceConfigRef != ""
	if !skipFlexNode &&
		cluster.Spec.FlexNodeProfile != nil &&
		cluster.Spec.FlexNodeProfile.Mode ==
			v1alpha1.FlexNodeModeAuto {
		f := cluster.Spec.FlexNodeProfile

		// For auto mode, only send Mode and
		// PolicySigningCertPem. Karpenter handles the
		// rest.
		certPem, err := r.resolveSecretOrInline(
			ctx, cluster.Namespace,
			f.PolicySigningCertPem,
			f.PolicySigningCertSecretRef,
		)
		if err != nil {
			return nil, fmt.Errorf(
				"resolving policySigningCert: %w", err,
			)
		}

		if certPem == "" && f.GovernanceConfigRef != "" {
			var cm corev1.ConfigMap
			cmKey := ctrlclient.ObjectKey{
				Namespace: cluster.Namespace,
				Name:      f.GovernanceConfigRef,
			}
			if err := r.Get(
				ctx, cmKey, &cm,
			); err == nil {
				certPem = cm.Data["policySigningCertPem"]
			}
		}

		insecure := true
		if f.Insecure != nil {
			insecure = *f.Insecure
		}

		provisionUsingSSH := true
		if f.ProvisionUsingSSH != nil {
			provisionUsingSSH = *f.ProvisionUsingSSH
		}

		fInput := &client.FlexNodeProfileInput{
			Enabled:              f.Enabled,
			Mode:                 string(f.Mode),
			PolicySigningCertPem: certPem,
			Insecure:             insecure,
			ProvisionUsingSSH:    provisionUsingSSH,
			SshPrivateKeyPem:     f.SshPrivateKeyPem,
			SshPublicKey:         f.SshPublicKey,
		}

		// Resolve SSH keys from secrets if provided.
		if f.SshPrivateKeySecretRef != nil {
			sshKey, err := r.resolveSecretOrInline(
				ctx, cluster.Namespace,
				"", f.SshPrivateKeySecretRef,
			)
			if err != nil {
				return nil, fmt.Errorf(
					"resolving sshPrivateKey: %w", err,
				)
			}
			fInput.SshPrivateKeyPem = sshKey
		}
		if f.SshPublicKeySecretRef != nil {
			sshPub, err := r.resolveSecretOrInline(
				ctx, cluster.Namespace,
				"", f.SshPublicKeySecretRef,
			)
			if err != nil {
				return nil, fmt.Errorf(
					"resolving sshPublicKey: %w", err,
				)
			}
			fInput.SshPublicKey = sshPub
		}

		// Auto-generate SSH keys for non-virtual infra
		// when none were provided.
		if cluster.Spec.InfraType != "virtual" &&
			fInput.SshPrivateKeyPem == "" &&
			fInput.SshPublicKey == "" {
			privKey, pubKey, err :=
				r.ensureSshKeySecret(
					ctx, cluster.Namespace,
				)
			if err != nil {
				return nil, fmt.Errorf(
					"ensuring SSH key Secret: %w",
					err,
				)
			}
			fInput.SshPrivateKeyPem = privKey
			fInput.SshPublicKey = pubKey
		}

		input.FlexNodeProfile = fInput
	} else if !skipFlexNode &&
		cluster.Spec.FlexNodeProfile != nil {
		f := cluster.Spec.FlexNodeProfile

		insecure := true
		if f.Insecure != nil {
			insecure = *f.Insecure
		}

		requirePreProvisioned := false
		if f.RequirePreProvisionedKindNodes != nil {
			requirePreProvisioned = *f.RequirePreProvisionedKindNodes
		}

		provisionUsingSSH := true
		if f.ProvisionUsingSSH != nil {
			provisionUsingSSH = *f.ProvisionUsingSSH
		}

		fInput := &client.FlexNodeProfileInput{
			Enabled:                        f.Enabled,
			Mode:                           string(f.Mode),
			NodeCount:                      f.NodeCount,
			VmSize:                         f.VmSize,
			MaxPodsPerNode:                 f.MaxPodsPerNode,
			Insecure:                       insecure,
			ProvisionUsingSSH:              provisionUsingSSH,
			SshPrivateKeyPem:               f.SshPrivateKeyPem,
			SshPublicKey:                   f.SshPublicKey,
			RequirePreProvisionedKindNodes: requirePreProvisioned,
		}

		if f.OsDiskSizeInGB != nil {
			fInput.OsDiskSizeInGB = f.OsDiskSizeInGB
		}

		// Resolve policy signing cert from secret if needed.
		certPem, err := r.resolveSecretOrInline(
			ctx, cluster.Namespace,
			f.PolicySigningCertPem,
			f.PolicySigningCertSecretRef,
		)
		if err != nil {
			return nil, fmt.Errorf(
				"resolving policySigningCert: %w", err,
			)
		}

		// If no cert was provided inline or via secret,
		// try the governance ConfigMap.
		if certPem == "" && f.GovernanceConfigRef != "" {
			var cm corev1.ConfigMap
			cmKey := ctrlclient.ObjectKey{
				Namespace: cluster.Namespace,
				Name:      f.GovernanceConfigRef,
			}
			if err := r.Get(
				ctx, cmKey, &cm,
			); err == nil {
				certPem = cm.Data["policySigningCertPem"]
			}
		}
		if f.Enabled && certPem == "" {
			return nil, fmt.Errorf(
				"policySigningCertPem is required " +
					"for flex node deployment but " +
					"could not be resolved from " +
					"inline, secret, or ConfigMap")
		}
		fInput.PolicySigningCertPem = certPem

		// Resolve SSH keys from secrets if needed.
		if f.SshPrivateKeySecretRef != nil {
			sshKey, err := r.resolveSecretOrInline(
				ctx, cluster.Namespace,
				"", f.SshPrivateKeySecretRef,
			)
			if err != nil {
				return nil, fmt.Errorf(
					"resolving sshPrivateKey: %w", err,
				)
			}
			fInput.SshPrivateKeyPem = sshKey
		}
		if f.SshPublicKeySecretRef != nil {
			sshPub, err := r.resolveSecretOrInline(
				ctx, cluster.Namespace,
				"", f.SshPublicKeySecretRef,
			)
			if err != nil {
				return nil, fmt.Errorf(
					"resolving sshPublicKey: %w", err,
				)
			}
			fInput.SshPublicKey = sshPub
		}

		// Auto-generate SSH keys for non-virtual infra
		// when none were provided.
		if f.Enabled &&
			cluster.Spec.InfraType != "virtual" &&
			fInput.SshPrivateKeyPem == "" &&
			fInput.SshPublicKey == "" {
			privKey, pubKey, err :=
				r.ensureSshKeySecret(
					ctx, cluster.Namespace,
				)
			if err != nil {
				return nil, fmt.Errorf(
					"ensuring SSH key Secret: %w",
					err,
				)
			}
			fInput.SshPrivateKeyPem = privKey
			fInput.SshPublicKey = pubKey
		}

		input.FlexNodeProfile = fInput
	}

	if cluster.Spec.AadProfile != nil {
		input.AadProfile = &client.AadProfileInput{
			Enabled:             cluster.Spec.AadProfile.Enabled,
			AdminGroupObjectIds: cluster.Spec.AadProfile.AdminGroupObjectIds,
		}
	}

	if cluster.Spec.ProviderConfig != nil {
		input.ProviderConfig = cluster.Spec.ProviderConfig.Raw
	}

	// Override configurationUrl from governance ConfigMap
	// when kserve profile has governanceConfigRef set.
	// Skip when deferring governance profiles — ConfigMap
	// may not be populated yet.
	govRef := r.getGovernanceConfigRef(cluster)
	if !deferGovernanceProfiles && govRef != "" {
		var cm corev1.ConfigMap
		key := ctrlclient.ObjectKey{
			Namespace: cluster.Namespace,
			Name:      govRef,
		}
		if err := r.Get(ctx, key, &cm); err != nil {
			return nil, fmt.Errorf(
				"getting governance ConfigMap %s: %w",
				govRef, err,
			)
		}

		configUrl := cm.Data["configurationUrl"]
		configUrlCaCert := cm.Data["configurationUrlCaCert"]

		if configUrl != "" {
			if input.InferencingWorkloadProfile != nil &&
				input.InferencingWorkloadProfile.
					KServeProfile != nil {
				input.InferencingWorkloadProfile.
					KServeProfile.
					ConfigurationUrl = configUrl
				input.InferencingWorkloadProfile.
					KServeProfile.
					ConfigurationUrlCaCert = configUrlCaCert
			}
		}
	}

	return input, nil
}

func (r *ClusterReconciler) buildGetInput(
	cluster *v1alpha1.Cluster,
) *client.GetClusterInput {
	input := &client.GetClusterInput{
		InfraType: cluster.Spec.InfraType,
	}
	if cluster.Spec.ProviderConfig != nil {
		input.ProviderConfig = cluster.Spec.ProviderConfig.Raw
	}
	return input
}

func (r *ClusterReconciler) resolveSecretOrInline(
	ctx context.Context,
	namespace string,
	inline string,
	ref *v1alpha1.SecretKeyRef,
) (string, error) {
	if inline != "" {
		return inline, nil
	}
	if ref == nil {
		return "", nil
	}

	var secret corev1.Secret
	key := ctrlclient.ObjectKey{
		Namespace: namespace,
		Name:      ref.Name,
	}
	if err := r.Get(ctx, key, &secret); err != nil {
		return "", fmt.Errorf(
			"getting secret %s/%s: %w", namespace, ref.Name, err,
		)
	}
	data, ok := secret.Data[ref.Key]
	if !ok {
		return "", fmt.Errorf(
			"key %q not found in secret %s/%s",
			ref.Key, namespace, ref.Name,
		)
	}
	return string(data), nil
}

func (r *ClusterReconciler) mapResponseToStatus(
	cluster *v1alpha1.Cluster,
	resp *client.ClusterResponse,
) {
	if resp.ObservabilityProfile != nil {
		cluster.Status.ObservabilityProfile =
			&v1alpha1.ObservabilityProfileStatus{
				Enabled:               resp.ObservabilityProfile.Enabled,
				MetricsEndpoint:       resp.ObservabilityProfile.MetricsEndpoint,
				LogsEndpoint:          resp.ObservabilityProfile.LogsEndpoint,
				TracesEndpoint:        resp.ObservabilityProfile.TracesEndpoint,
				VisualizationEndpoint: resp.ObservabilityProfile.VisualizationEndpoint,
			}
	}

	if resp.MonitoringProfile != nil {
		cluster.Status.MonitoringProfile =
			&v1alpha1.MonitoringProfileStatus{
				Enabled: resp.MonitoringProfile.Enabled,
			}
	}

	if resp.AnalyticsWorkloadProfile != nil {
		cluster.Status.AnalyticsWorkloadProfile =
			&v1alpha1.AnalyticsWorkloadProfileStatus{
				Enabled:   resp.AnalyticsWorkloadProfile.Enabled,
				Namespace: resp.AnalyticsWorkloadProfile.Namespace,
				Endpoint:  resp.AnalyticsWorkloadProfile.Endpoint,
			}
	}

	if resp.InferencingWorkloadProfile != nil &&
		resp.InferencingWorkloadProfile.KServeProfile != nil {
		cluster.Status.InferencingWorkloadProfile =
			&v1alpha1.InferencingProfileStatus{
				KServeProfile: &v1alpha1.KServeInferencingProfileStatus{
					Enabled:   resp.InferencingWorkloadProfile.KServeProfile.Enabled,
					Namespace: resp.InferencingWorkloadProfile.KServeProfile.Namespace,
					Endpoint:  resp.InferencingWorkloadProfile.KServeProfile.Endpoint,
				},
			}
	}

	if resp.FlexNodeProfile != nil {
		fps := &v1alpha1.FlexNodeProfileStatus{
			Enabled: resp.FlexNodeProfile.Enabled,
		}
		if resp.FlexNodeProfile.Nodes != nil {
			var nodes []v1alpha1.FlexNodeStatus
			_ = json.Unmarshal(resp.FlexNodeProfile.Nodes, &nodes)
			fps.Nodes = nodes
		}
		cluster.Status.FlexNodeProfile = fps
	}

	if resp.ProviderProperties != nil {
		cluster.Status.ProviderProperties = &runtime.RawExtension{
			Raw: resp.ProviderProperties,
		}
	}
}

func (r *ClusterReconciler) setPhase(
	ctx context.Context,
	cluster *v1alpha1.Cluster,
	phase v1alpha1.ClusterPhase,
) {
	cluster.Status.Phase = phase
}

// event emits a Kubernetes event on the cluster resource.
func (r *ClusterReconciler) event(
	cluster *v1alpha1.Cluster,
	eventType string,
	reason string,
	message string,
) {
	if r.Recorder != nil {
		r.Recorder.Event(cluster, eventType, reason, message)
	}
}

// eventf emits a formatted Kubernetes event on the cluster resource.
func (r *ClusterReconciler) eventf(
	cluster *v1alpha1.Cluster,
	eventType string,
	reason string,
	msgFmt string,
	args ...interface{},
) {
	if r.Recorder != nil {
		r.Recorder.Eventf(
			cluster, eventType, reason, msgFmt, args...,
		)
	}
}

// SetupWithManager sets up the controller with the Manager.
func (r *ClusterReconciler) SetupWithManager(
	mgr ctrl.Manager,
) error {
	// Index Clusters by governanceConfigRef so we can watch
	// ConfigMaps and map them back to Clusters.
	if err := mgr.GetFieldIndexer().IndexField(
		context.Background(),
		&v1alpha1.Cluster{},
		govConfigIndexKey,
		func(o ctrlclient.Object) []string {
			c := o.(*v1alpha1.Cluster)
			var refs []string
			if c.Spec.InferencingWorkloadProfile != nil &&
				c.Spec.InferencingWorkloadProfile.
					KServeProfile != nil &&
				c.Spec.InferencingWorkloadProfile.
					KServeProfile.GovernanceConfigRef != "" {
				refs = append(refs,
					c.Spec.InferencingWorkloadProfile.
						KServeProfile.GovernanceConfigRef)
			}
			if c.Spec.FlexNodeProfile != nil &&
				c.Spec.FlexNodeProfile.
					GovernanceConfigRef != "" {
				refs = append(refs,
					c.Spec.FlexNodeProfile.
						GovernanceConfigRef)
			}
			return refs
		},
	); err != nil {
		return fmt.Errorf(
			"setting up field index: %w", err,
		)
	}

	return ctrl.NewControllerManagedBy(mgr).
		For(&v1alpha1.Cluster{}).
		Watches(
			&corev1.ConfigMap{},
			r.configMapToClusterHandler(),
		).
		Complete(r)
}

// configMapToClusterHandler returns an event handler that maps
// ConfigMap events to reconcile requests for Clusters that
// reference them via governanceConfigRef.
func (r *ClusterReconciler) configMapToClusterHandler() handler.EventHandler {
	return handler.EnqueueRequestsFromMapFunc(
		func(
			ctx context.Context,
			obj ctrlclient.Object,
		) []ctrl.Request {
			var clusters v1alpha1.ClusterList
			if err := r.List(ctx, &clusters,
				ctrlclient.InNamespace(obj.GetNamespace()),
				ctrlclient.MatchingFields{
					govConfigIndexKey: obj.GetName(),
				},
			); err != nil {
				return nil
			}

			var requests []ctrl.Request
			for i := range clusters.Items {
				requests = append(requests, ctrl.Request{
					NamespacedName: ctrlclient.ObjectKeyFromObject(
						&clusters.Items[i],
					),
				})
			}
			return requests
		},
	)
}
