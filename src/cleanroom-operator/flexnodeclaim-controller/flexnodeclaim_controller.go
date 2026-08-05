package flexnodeclaimctrl

import (
	"context"
	"fmt"
	"time"

	corev1 "k8s.io/api/core/v1"
	apierrors "k8s.io/apimachinery/pkg/api/errors"
	"k8s.io/apimachinery/pkg/api/meta"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/runtime"
	"k8s.io/client-go/tools/record"
	ctrl "sigs.k8s.io/controller-runtime"
	ctrlclient "sigs.k8s.io/controller-runtime/pkg/client"
	"sigs.k8s.io/controller-runtime/pkg/controller/controllerutil"
	ctrllog "sigs.k8s.io/controller-runtime/pkg/log"

	v1alpha1 "github.com/Azure/azure-cleanroom/cleanroom-operator/api/v1alpha1"
	"github.com/Azure/azure-cleanroom/cleanroom-operator/client"
)

const (
	fncRequeueDelay = 15 * time.Second
)

// FlexNodeClaimReconciler reconciles FlexNodeClaim objects.
// It bridges between the Karpenter accr CloudProvider and
// the cluster-provider-client by calling the REST API to
// add/remove flex nodes.
type FlexNodeClaimReconciler struct {
	ctrlclient.Client
	Scheme        *runtime.Scheme
	ClusterClient *client.ClusterClient
	Recorder      record.EventRecorder
}

// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=flexnodeclaims,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=flexnodeclaims/status,verbs=get;update;patch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=flexnodeclaims/finalizers,verbs=update
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=flexnodeclasses,verbs=get;list;watch
// +kubebuilder:rbac:groups="",resources=nodes,verbs=get;list;watch
// +kubebuilder:rbac:groups="",resources=secrets,verbs=get;list;watch
// +kubebuilder:rbac:groups="",resources=events,verbs=create;patch

// Reconcile handles the reconciliation loop for
// FlexNodeClaim.
func (r *FlexNodeClaimReconciler) Reconcile(
	ctx context.Context,
	req ctrl.Request,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	var fnc v1alpha1.FlexNodeClaim
	if err := r.Get(
		ctx, req.NamespacedName, &fnc,
	); err != nil {
		if apierrors.IsNotFound(err) {
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"fetching FlexNodeClaim: %w", err,
		)
	}

	ctx = resolveTraceContext(
		ctx, &fnc, fnc.Status.TraceParent,
	)
	ctx, span := startSpan(
		ctx, "FlexNodeClaim", "Reconcile", fnc.Name,
	)
	defer span.End()

	log.Info("Reconciling FlexNodeClaim",
		"name", fnc.Name,
		"phase", fnc.Status.Phase)

	// Handle deletion.
	if !fnc.DeletionTimestamp.IsZero() {
		return r.reconcileDelete(ctx, &fnc)
	}

	// Ensure finalizer.
	if !controllerutil.ContainsFinalizer(
		&fnc, v1alpha1.FlexNodeClaimFinalizer,
	) {
		controllerutil.AddFinalizer(
			&fnc, v1alpha1.FlexNodeClaimFinalizer,
		)
		if err := r.Update(ctx, &fnc); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"adding finalizer: %w", err,
			)
		}
		return ctrl.Result{Requeue: true}, nil
	}

	// Route based on phase.
	switch fnc.Status.Phase {
	case "", v1alpha1.FlexNodeClaimPhasePending:
		return r.reconcileProvision(ctx, &fnc)

	case v1alpha1.FlexNodeClaimPhaseProvisioning:
		return r.reconcileProvisioning(ctx, &fnc)

	case v1alpha1.FlexNodeClaimPhaseReady:
		return ctrl.Result{}, nil

	case v1alpha1.FlexNodeClaimPhaseFailed:
		return ctrl.Result{}, nil

	default:
		return ctrl.Result{}, nil
	}
}

// reconcileProvision calls the cluster provider to create
// the flex node.
func (r *FlexNodeClaimReconciler) reconcileProvision(
	ctx context.Context,
	fnc *v1alpha1.FlexNodeClaim,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	// Resolve FlexNodeClass for infrastructure config.
	nodeClass, err := r.resolveNodeClass(ctx, fnc)
	if err != nil {
		r.fncSetCondition(fnc,
			"NodeClassResolved",
			metav1.ConditionFalse,
			"ResolveFailed",
			err.Error())
		r.Recorder.Eventf(fnc,
			corev1.EventTypeWarning,
			"NodeClassResolveFailed",
			"Failed to resolve FlexNodeClass %s: %v",
			fnc.Spec.NodeClassName, err)
		return r.fncSetPhaseAndRequeue(
			ctx, fnc,
			v1alpha1.FlexNodeClaimPhasePending,
			fncRequeueDelay,
		)
	}

	clusterName := nodeClass.Spec.ClusterName

	// Build providerID.
	providerID := fmt.Sprintf(
		"accr://%s/%s", clusterName, fnc.Name,
	)

	// Resolve the policy signing certificate.
	certPem, err := r.resolvePolicySigningCert(
		ctx, nodeClass,
	)
	if err != nil {
		r.fncSetCondition(fnc,
			"Provisioned",
			metav1.ConditionFalse,
			"CertResolveFailed",
			err.Error())
		r.Recorder.Eventf(fnc,
			corev1.EventTypeWarning,
			"CertResolveFailed",
			"Failed to resolve policy signing cert: %v",
			err)
		return r.fncSetPhaseAndRequeue(
			ctx, fnc,
			v1alpha1.FlexNodeClaimPhaseFailed,
			fncRequeueDelay,
		)
	}

	// Call the cluster provider to create the flex node.
	log.Info("Creating flex node via cluster provider",
		"cluster", clusterName,
		"nodeName", fnc.Name,
		"providerID", providerID)

	opLocation, err := r.ClusterClient.CreateFlexNode(
		ctx, clusterName, fnc.Name,
		&client.CreateFlexNodeInput{
			InfraType:            nodeClass.Spec.InfraType,
			ProviderID:           providerID,
			PolicySigningCertPem: certPem,
			ProviderConfig:       nodeClass.Spec.ProviderConfig,
		},
	)
	if err != nil {
		if client.IsRetryable(err) {
			log.Info(
				"Retryable error creating flex node, "+
					"will retry",
				"error", err.Error())
			r.fncSetCondition(fnc,
				"Provisioned",
				metav1.ConditionFalse,
				"ProvisionRetrying",
				err.Error())
			r.Recorder.Eventf(fnc,
				corev1.EventTypeWarning,
				"ProvisionRetrying",
				"Retryable error creating flex node "+
					"(will retry): %v", err)
			return r.fncSetPhaseAndRequeue(
				ctx, fnc,
				v1alpha1.FlexNodeClaimPhasePending,
				fncRequeueDelay,
			)
		}

		r.fncSetCondition(fnc,
			"Provisioned",
			metav1.ConditionFalse,
			"ProvisionFailed",
			err.Error())
		r.Recorder.Eventf(fnc,
			corev1.EventTypeWarning,
			"ProvisionFailed",
			"Failed to create flex node: %v", err)
		return r.fncSetPhaseAndRequeue(
			ctx, fnc,
			v1alpha1.FlexNodeClaimPhaseFailed,
			fncRequeueDelay,
		)
	}

	// Update status.
	fnc.Status.ProviderID = providerID
	fnc.Status.OperationID = opLocation
	fnc.Status.Phase =
		v1alpha1.FlexNodeClaimPhaseProvisioning
	traceParent, traceID := saveTrace(ctx)
	fnc.Status.TraceParent = traceParent
	fnc.Status.LastOperationTraceID = traceID
	r.fncSetCondition(fnc,
		"Provisioned",
		metav1.ConditionFalse,
		"Creating",
		"Flex node creation initiated")
	if err := r.Status().Update(ctx, fnc); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status to Provisioning: %w", err,
		)
	}

	r.Recorder.Eventf(fnc,
		corev1.EventTypeNormal,
		"Provisioning",
		"Flex node %s creation initiated on cluster %s",
		fnc.Name, clusterName)

	return ctrl.Result{
		RequeueAfter: fncRequeueDelay,
	}, nil
}

// reconcileProvisioning polls the async operation and
// once complete checks whether the flex node has joined
// the workload cluster as a Kubernetes node.
func (r *FlexNodeClaimReconciler) reconcileProvisioning(
	ctx context.Context,
	fnc *v1alpha1.FlexNodeClaim,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	// If there is an outstanding operation, poll it.
	if fnc.Status.OperationID != "" {
		op, err := r.ClusterClient.GetOperation(
			ctx, fnc.Status.OperationID,
		)
		if err != nil {
			log.Error(err, "Failed to poll operation",
				"operationId", fnc.Status.OperationID)
			return ctrl.Result{
				RequeueAfter: fncRequeueDelay,
			}, nil
		}

		if op == nil {
			// Operation not found (provider restarted).
			// Clear and let node-check proceed.
			log.Info(
				"Operation not found, checking node",
				"operationId", fnc.Status.OperationID,
			)
			r.Recorder.Eventf(fnc,
				corev1.EventTypeWarning,
				"OperationLost",
				"Operation %s not found, checking node",
				fnc.Status.OperationID)
			fnc.Status.OperationID = ""
			_ = r.Status().Update(ctx, fnc)
		} else if op.Status == "Running" ||
			op.Status == "Queued" {
			log.Info("Operation still in progress",
				"operationId", fnc.Status.OperationID,
				"status", op.Status)
			return ctrl.Result{
				RequeueAfter: fncRequeueDelay,
			}, nil
		} else if op.Status == "Succeeded" {
			log.Info("Operation succeeded",
				"operationId", fnc.Status.OperationID)
			fnc.Status.OperationID = ""
			r.fncSetCondition(fnc,
				"Provisioned",
				metav1.ConditionTrue,
				"Created",
				"Flex node creation completed")
			_ = r.Status().Update(ctx, fnc)
			r.Recorder.Eventf(fnc,
				corev1.EventTypeNormal,
				"Provisioned",
				"Flex node creation completed")
		} else if op.Status == "Failed" {
			log.Info("Operation failed",
				"operationId", fnc.Status.OperationID,
				"error", string(op.Error))
			fnc.Status.OperationID = ""
			fnc.Status.TraceParent = ""
			r.fncSetCondition(fnc,
				"Provisioned",
				metav1.ConditionFalse,
				"OperationFailed",
				string(op.Error))
			r.Recorder.Eventf(fnc,
				corev1.EventTypeWarning,
				"OperationFailed",
				"Flex node operation failed: %s",
				string(op.Error))
			return r.fncSetPhaseAndRequeue(
				ctx, fnc,
				v1alpha1.FlexNodeClaimPhaseFailed,
				fncRequeueDelay,
			)
		}
	}

	// Operation complete (or was sync) — check if node
	// has registered with matching providerID.
	var nodeList corev1.NodeList
	if err := r.List(ctx, &nodeList); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"listing nodes: %w", err,
		)
	}

	for i := range nodeList.Items {
		node := &nodeList.Items[i]
		if node.Spec.ProviderID == fnc.Status.ProviderID {
			log.Info("Flex node registered",
				"node", node.Name,
				"providerID", fnc.Status.ProviderID)

			fnc.Status.NodeName = node.Name
			fnc.Status.Phase =
				v1alpha1.FlexNodeClaimPhaseReady
			fnc.Status.TraceParent = ""
			r.fncSetCondition(fnc,
				"Ready",
				metav1.ConditionTrue,
				"NodeRegistered",
				fmt.Sprintf(
					"Node %s registered",
					node.Name,
				))
			if err := r.Status().Update(
				ctx, fnc,
			); err != nil {
				return ctrl.Result{}, fmt.Errorf(
					"updating status to Ready: %w",
					err,
				)
			}

			r.Recorder.Eventf(fnc,
				corev1.EventTypeNormal,
				"Ready",
				"Flex node registered as %s",
				node.Name)

			return ctrl.Result{}, nil
		}
	}

	log.Info("Flex node not yet registered, requeueing",
		"providerID", fnc.Status.ProviderID)

	return ctrl.Result{
		RequeueAfter: fncRequeueDelay,
	}, nil
}

// reconcileDelete handles FlexNodeClaim deletion by
// calling the cluster provider to remove the flex node.
func (r *FlexNodeClaimReconciler) reconcileDelete(
	ctx context.Context,
	fnc *v1alpha1.FlexNodeClaim,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	if !controllerutil.ContainsFinalizer(
		fnc, v1alpha1.FlexNodeClaimFinalizer,
	) {
		return ctrl.Result{}, nil
	}

	// If we have an in-flight delete operation, poll it.
	if fnc.Status.OperationID != "" {
		op, err := r.ClusterClient.GetOperation(
			ctx, fnc.Status.OperationID,
		)
		if err != nil {
			log.Error(err, "Failed to poll delete op",
				"operationId", fnc.Status.OperationID)
			return ctrl.Result{
				RequeueAfter: fncRequeueDelay,
			}, nil
		}

		if op == nil {
			// Operation lost — proceed to remove finalizer.
			log.Info("Delete operation not found, proceeding")
		} else if op.Status == "Running" ||
			op.Status == "Queued" {
			log.Info("Delete operation in progress",
				"operationId", fnc.Status.OperationID)
			return ctrl.Result{
				RequeueAfter: fncRequeueDelay,
			}, nil
		} else if op.Status == "Failed" {
			log.Error(
				fmt.Errorf("%s", string(op.Error)),
				"Delete operation failed",
				"operationId", fnc.Status.OperationID,
			)
			r.Recorder.Eventf(fnc,
				corev1.EventTypeWarning,
				"DeleteFailed",
				"Delete operation failed: %s",
				string(op.Error))
			// Clear op and retry the delete on next
			// reconcile.
			fnc.Status.OperationID = ""
			_ = r.Status().Update(ctx, fnc)
			return ctrl.Result{
				RequeueAfter: fncRequeueDelay,
			}, nil
		}
		// Succeeded or lost — fall through to remove
		// finalizer.
		fnc.Status.OperationID = ""
		_ = r.Status().Update(ctx, fnc)
	} else {
		// No operation in flight — initiate the delete.
		nodeClass, err := r.resolveNodeClass(ctx, fnc)
		if err != nil {
			// If NodeClass is gone, skip infra cleanup.
			log.Info(
				"NodeClass not found, skipping delete",
				"error", err.Error())
		} else {
			clusterName := nodeClass.Spec.ClusterName
			nodeName := fnc.Status.NodeName
			if nodeName == "" {
				nodeName = fnc.Name
			}

			log.Info(
				"Deleting flex node via cluster provider",
				"cluster", clusterName,
				"nodeName", nodeName)

			opLocation, delErr :=
				r.ClusterClient.DeleteFlexNode(
					ctx, clusterName, nodeName,
					&client.GetClusterInput{
						InfraType:      nodeClass.Spec.InfraType,
						ProviderConfig: nodeClass.Spec.ProviderConfig,
					},
				)
			if delErr != nil {
				r.Recorder.Eventf(fnc,
					corev1.EventTypeWarning,
					"DeleteFailed",
					"Failed to delete flex node %s: %v",
					nodeName, delErr)
				return ctrl.Result{
						RequeueAfter: fncRequeueDelay,
					}, fmt.Errorf(
						"deleting flex node %s: %w",
						nodeName, delErr,
					)
			}

			// If async, store operation and requeue.
			if opLocation != "" {
				fnc.Status.Phase =
					v1alpha1.FlexNodeClaimPhaseDeleting
				fnc.Status.OperationID = opLocation
				traceParent, traceID := saveTrace(ctx)
				fnc.Status.TraceParent = traceParent
				fnc.Status.LastOperationTraceID = traceID
				_ = r.Status().Update(ctx, fnc)
				r.Recorder.Eventf(fnc,
					corev1.EventTypeNormal,
					"Deleting",
					"Flex node deletion initiated")
				return ctrl.Result{
					RequeueAfter: fncRequeueDelay,
				}, nil
			}
		}
	}

	controllerutil.RemoveFinalizer(
		fnc, v1alpha1.FlexNodeClaimFinalizer,
	)
	if err := r.Update(ctx, fnc); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"removing finalizer: %w", err,
		)
	}

	log.Info("FlexNodeClaim finalizer removed",
		"name", fnc.Name)

	return ctrl.Result{}, nil
}

// fncSetPhaseAndRequeue updates the FlexNodeClaim status
// and requeues.
func (r *FlexNodeClaimReconciler) fncSetPhaseAndRequeue(
	ctx context.Context,
	fnc *v1alpha1.FlexNodeClaim,
	phase v1alpha1.FlexNodeClaimPhase,
	delay time.Duration,
) (ctrl.Result, error) {
	fnc.Status.Phase = phase
	if err := r.Status().Update(ctx, fnc); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating phase to %s: %w", phase, err,
		)
	}
	return ctrl.Result{RequeueAfter: delay}, nil
}

// fncSetCondition sets a condition on the FlexNodeClaim.
func (r *FlexNodeClaimReconciler) fncSetCondition(
	fnc *v1alpha1.FlexNodeClaim,
	condType string,
	status metav1.ConditionStatus,
	reason string,
	message string,
) {
	meta.SetStatusCondition(
		&fnc.Status.Conditions,
		metav1.Condition{
			Type:               condType,
			Status:             status,
			Reason:             reason,
			Message:            message,
			ObservedGeneration: fnc.Generation,
		},
	)
}

// resolveNodeClass resolves the FlexNodeClass for a
// FlexNodeClaim by looking up the cluster-scoped resource.
func (r *FlexNodeClaimReconciler) resolveNodeClass(
	ctx context.Context,
	fnc *v1alpha1.FlexNodeClaim,
) (*v1alpha1.FlexNodeClass, error) {
	var nodeClass v1alpha1.FlexNodeClass
	if err := r.Get(ctx, ctrlclient.ObjectKey{
		Name: fnc.Spec.NodeClassName,
	}, &nodeClass); err != nil {
		if apierrors.IsNotFound(err) {
			return nil, fmt.Errorf(
				"FlexNodeClass %s not found",
				fnc.Spec.NodeClassName,
			)
		}
		return nil, fmt.Errorf(
			"fetching FlexNodeClass %s: %w",
			fnc.Spec.NodeClassName, err,
		)
	}
	return &nodeClass, nil
}

// resolvePolicySigningCert resolves the policy signing
// certificate from the FlexNodeClass. It checks inline PEM
// and secret reference in that order.
func (r *FlexNodeClaimReconciler) resolvePolicySigningCert(
	ctx context.Context,
	nodeClass *v1alpha1.FlexNodeClass,
) (string, error) {
	// Inline PEM takes precedence.
	if nodeClass.Spec.PolicySigningCertPem != "" {
		return nodeClass.Spec.PolicySigningCertPem, nil
	}

	// Try secret reference.
	if nodeClass.Spec.PolicySigningCertSecret != nil {
		ref := nodeClass.Spec.PolicySigningCertSecret
		var secret corev1.Secret
		key := ctrlclient.ObjectKey{
			Namespace: "default",
			Name:      ref.Name,
		}
		if err := r.Get(ctx, key, &secret); err != nil {
			return "", fmt.Errorf(
				"getting secret %s: %w",
				ref.Name, err,
			)
		}
		data, ok := secret.Data[ref.Key]
		if !ok {
			return "", fmt.Errorf(
				"key %q not found in secret %s",
				ref.Key, ref.Name,
			)
		}
		return string(data), nil
	}

	return "", nil
}

// SetupWithManager sets up the controller with the
// Manager.
func (r *FlexNodeClaimReconciler) SetupWithManager(
	mgr ctrl.Manager,
) error {
	return ctrl.NewControllerManagedBy(mgr).
		For(&v1alpha1.FlexNodeClaim{}).
		Complete(r)
}
