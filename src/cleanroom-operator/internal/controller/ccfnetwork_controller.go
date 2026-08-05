package controller

import (
	"context"
	"crypto/tls"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
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
	ccfFinalizerName = "cleanroom.azure.com/ccf-finalizer"
	// memberSecretAnnotation stores the name of the Secret
	// containing the operator member's signing cert and key.
	memberSecretAnnotation = "cleanroom.azure.com/member-secret"
	// memberRefAnnotation stores the name of the CcfMember
	// resource that is the operator member for this network.
	memberRefAnnotation = "cleanroom.azure.com/member-ref"

	networkRequeueDelay       = 15 * time.Second
	networkStatusSyncInterval = 60 * time.Second
)

// CcfNetworkReconciler reconciles CcfNetwork objects.
type CcfNetworkReconciler struct {
	ctrlclient.Client
	Scheme           *runtime.Scheme
	CcfNetworkClient *client.CcfNetworkClient
	Recorder         record.EventRecorder
}

// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=ccfnetworks,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=ccfnetworks/status,verbs=get;update;patch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=ccfnetworks/finalizers,verbs=update
// +kubebuilder:rbac:groups="",resources=events,verbs=create;patch

// Reconcile handles the reconciliation loop for CcfNetwork.
func (r *CcfNetworkReconciler) Reconcile(
	ctx context.Context,
	req ctrl.Request,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	var network v1alpha1.CcfNetwork
	if err := r.Get(ctx, req.NamespacedName, &network); err != nil {
		if apierrors.IsNotFound(err) {
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"fetching CcfNetwork: %w", err,
		)
	}

	ctx = resolveTraceContext(
		ctx, &network, network.Status.TraceParent,
	)
	ctx, span := startSpan(
		ctx, "CcfNetwork", "Reconcile", network.Name,
	)
	defer span.End()

	// Handle deletion.
	if !network.DeletionTimestamp.IsZero() {
		return r.reconcileDelete(ctx, &network)
	}

	// Ensure finalizer is set.
	if !controllerutil.ContainsFinalizer(
		&network, ccfFinalizerName,
	) {
		controllerutil.AddFinalizer(&network, ccfFinalizerName)
		if err := r.Update(ctx, &network); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"adding finalizer: %w", err,
			)
		}
		return ctrl.Result{Requeue: true}, nil
	}

	// Route based on phase.
	switch network.Status.Phase {
	case "", v1alpha1.CcfNetworkPhasePending:
		return r.reconcileCreate(ctx, &network)

	case v1alpha1.CcfNetworkPhaseCreating:
		return r.reconcileInProgress(ctx, &network)

	case v1alpha1.CcfNetworkPhaseRunning:
		return r.reconcileTransitionToOpen(ctx, &network)

	case v1alpha1.CcfNetworkPhaseOpen:
		return r.reconcileStatusSync(ctx, &network)

	case v1alpha1.CcfNetworkPhaseFailed:
		if network.Generation != network.Status.ObservedGeneration ||
			network.Annotations[retryAnnotation] != "" {
			if network.Annotations[retryAnnotation] != "" {
				delete(network.Annotations, retryAnnotation)
			}
			delete(
				network.Annotations,
				"cleanroom.azure.com/progress-count",
			)
			delete(
				network.Annotations,
				syncFailureCountAnn,
			)
			if err := r.Update(ctx, &network); err != nil {
				return ctrl.Result{}, fmt.Errorf(
					"clearing retry/progress annotations: %w",
					err,
				)
			}
			r.event(
				&network, corev1.EventTypeNormal,
				"Retrying",
				"Retry requested via annotation",
			)
			// Set transitional phase immediately so
			// the parent controller does not see a
			// stale Failed phase after the retry
			// annotation has been cleared.
			network.Status.Phase = v1alpha1.CcfNetworkPhasePending
			meta.RemoveStatusCondition(
				&network.Status.Conditions,
				v1alpha1.ConditionTypeCcfNetworkCreated,
			)
			meta.RemoveStatusCondition(
				&network.Status.Conditions,
				v1alpha1.ConditionTypeCcfNetworkOpen,
			)
			if err := r.Status().Update(
				ctx, &network,
			); err != nil {
				return ctrl.Result{}, fmt.Errorf(
					"resetting phase to Pending: %w", err,
				)
			}
			return ctrl.Result{Requeue: true}, nil
		}
		return ctrl.Result{RequeueAfter: networkStatusSyncInterval}, nil

	default:
		log.Info("Unknown phase, requeueing",
			"phase", network.Status.Phase)
		return ctrl.Result{RequeueAfter: networkRequeueDelay}, nil
	}
}

func (r *CcfNetworkReconciler) reconcileCreate(
	ctx context.Context,
	network *v1alpha1.CcfNetwork,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "CcfNetwork", "Create", network.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	log.Info("Creating CCF network", "name", network.Name)
	r.event(
		network, corev1.EventTypeNormal,
		"Creating", "CCF network creation initiated",
	)

	input := r.buildCreateInput(network)

	opLocation, err := r.CcfNetworkClient.CreateNetwork(
		ctx, network.Name, input,
	)
	if err != nil {
		recordError(span, err)
		network.Status.Phase = v1alpha1.CcfNetworkPhaseFailed
		network.Status.ObservedGeneration = network.Generation
		meta.SetStatusCondition(
			&network.Status.Conditions,
			metav1.Condition{
				Type:    v1alpha1.ConditionTypeCcfNetworkCreated,
				Status:  metav1.ConditionFalse,
				Reason:  "CreateFailed",
				Message: err.Error(),
			},
		)
		_ = r.Status().Update(ctx, network)
		r.eventf(
			network, corev1.EventTypeWarning,
			"CreateFailed", "CreateNetwork API error: %s",
			err.Error(),
		)
		return ctrl.Result{RequeueAfter: networkRequeueDelay}, nil
	}

	network.Status.Phase = v1alpha1.CcfNetworkPhaseCreating
	network.Status.OperationId = opLocation
	network.Status.ObservedGeneration = network.Generation
	network.Status.TraceParent,
		network.Status.LastOperationTraceId =
		saveTrace(ctx)
	meta.SetStatusCondition(
		&network.Status.Conditions,
		metav1.Condition{
			Type:    v1alpha1.ConditionTypeCcfNetworkCreated,
			Status:  metav1.ConditionFalse,
			Reason:  "Creating",
			Message: "CCF network creation in progress",
		},
	)
	if err := r.Status().Update(ctx, network); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after create: %w", err,
		)
	}

	return ctrl.Result{RequeueAfter: networkRequeueDelay}, nil
}

func (r *CcfNetworkReconciler) reconcileInProgress(
	ctx context.Context,
	network *v1alpha1.CcfNetwork,
) (ctrl.Result, error) {
	ctx = restoreTrace(ctx, network.Status.TraceParent)

	log := ctrllog.FromContext(ctx)

	if network.Status.OperationId != "" {
		op, err := r.CcfNetworkClient.GetOperation(
			ctx, network.Status.OperationId,
		)
		if err != nil {
			log.Error(err, "Failed to poll operation")
			meta.SetStatusCondition(
				&network.Status.Conditions,
				metav1.Condition{
					Type:   v1alpha1.ConditionTypeReady,
					Status: metav1.ConditionFalse,
					Reason: "PollOperationFailed",
					Message: fmt.Sprintf(
						"Failed to poll operation: %s",
						err.Error()),
				},
			)
			_ = r.Status().Update(ctx, network)
			return ctrl.Result{RequeueAfter: networkRequeueDelay}, nil
		}

		if op == nil {
			log.Info(
				"Operation not found, falling back to get",
				"operationId", network.Status.OperationId,
			)
			r.event(
				network, corev1.EventTypeWarning,
				"OperationLost",
				"Operation not found, recovering via GET",
			)
		} else if op.Status == "Running" || op.Status == "Queued" {
			r.emitProgressEvents(ctx, network, op)
			log.Info("Operation still in progress",
				"operationId", network.Status.OperationId,
				"status", op.Status)
			return ctrl.Result{RequeueAfter: networkRequeueDelay}, nil
		} else if op.Status == "Succeeded" {
			r.emitProgressEvents(ctx, network, op)
			_, span := startSpan(
				ctx, "CcfNetwork",
				"OperationComplete", network.Name,
			)
			defer span.End()

			log.Info("Operation succeeded",
				"operationId", network.Status.OperationId)
			network.Status.OperationId = ""
			network.Status.TraceParent = ""
			delete(
				network.Annotations,
				"cleanroom.azure.com/progress-count",
			)
			if err := r.Update(ctx, network); err != nil {
				log.Error(err,
					"Failed to clear progress-count annotation")
			}
			r.event(
				network, corev1.EventTypeNormal,
				"Created", "CCF network created successfully",
			)
			return r.syncStatusFromProvider(ctx, network)
		} else if op.Status == "Failed" {
			r.emitProgressEvents(ctx, network, op)
			_, span := startSpan(
				ctx, "CcfNetwork",
				"OperationFailed", network.Name,
			)
			defer span.End()
			opErr := fmt.Errorf("%s", string(op.Error))
			recordError(span, opErr)
			log.Info("Operation failed",
				"operationId", network.Status.OperationId)
			network.Status.OperationId = ""
			network.Status.TraceParent = ""
			delete(
				network.Annotations,
				"cleanroom.azure.com/progress-count",
			)
			if err := r.Update(ctx, network); err != nil {
				log.Error(err,
					"Failed to clear progress-count annotation")
			}
			network.Status.Phase = v1alpha1.CcfNetworkPhaseFailed
			meta.SetStatusCondition(
				&network.Status.Conditions,
				metav1.Condition{
					Type:    v1alpha1.ConditionTypeReady,
					Status:  metav1.ConditionFalse,
					Reason:  "OperationFailed",
					Message: string(op.Error),
				},
			)
			if err := r.Status().Update(ctx, network); err != nil {
				return ctrl.Result{}, fmt.Errorf(
					"updating status after op failure: %w",
					err,
				)
			}
			r.eventf(
				network, corev1.EventTypeWarning,
				"OperationFailed",
				"Operation failed: %s", string(op.Error),
			)
			return ctrl.Result{
				RequeueAfter: networkStatusSyncInterval,
			}, nil
		}
	}

	// Recovery: check if the network exists via get API.
	return r.syncStatusFromProvider(ctx, network)
}

func (r *CcfNetworkReconciler) emitProgressEvents(
	ctx context.Context,
	network *v1alpha1.CcfNetwork,
	op *client.OperationResponse,
) {
	if r.Recorder == nil || len(op.Progress) == 0 {
		return
	}
	const ann = "cleanroom.azure.com/progress-count"
	var seen int
	if v, ok := network.Annotations[ann]; ok {
		fmt.Sscanf(v, "%d", &seen)
	}
	if len(op.Progress) <= seen {
		return
	}
	for i := seen; i < len(op.Progress); i++ {
		r.Recorder.Eventf(
			network,
			corev1.EventTypeNormal,
			"OperationProgress",
			"%s", op.Progress[i],
		)
	}
	if network.Annotations == nil {
		network.Annotations = map[string]string{}
	}
	network.Annotations[ann] = fmt.Sprintf(
		"%d", len(op.Progress),
	)
	if err := r.Update(ctx, network); err != nil {
		log := ctrllog.FromContext(ctx)
		log.Error(err,
			"Failed to persist progress-count annotation")
	}
}

func (r *CcfNetworkReconciler) incrementSyncFailureCount(
	ctx context.Context,
	network *v1alpha1.CcfNetwork,
) int {
	var count int
	if v, ok := network.Annotations[syncFailureCountAnn]; ok {
		fmt.Sscanf(v, "%d", &count)
	}
	count++
	if network.Annotations == nil {
		network.Annotations = map[string]string{}
	}
	network.Annotations[syncFailureCountAnn] = fmt.Sprintf(
		"%d", count,
	)
	if err := r.Update(ctx, network); err != nil {
		log := ctrllog.FromContext(ctx)
		log.Error(err,
			"Failed to persist sync-failure-count")
	}
	return count
}

func (r *CcfNetworkReconciler) clearSyncFailureCount(
	ctx context.Context,
	network *v1alpha1.CcfNetwork,
) {
	if _, ok := network.Annotations[syncFailureCountAnn]; !ok {
		return
	}
	delete(network.Annotations, syncFailureCountAnn)
	if err := r.Update(ctx, network); err != nil {
		log := ctrllog.FromContext(ctx)
		log.Error(err,
			"Failed to clear sync-failure-count")
	}
}

func (r *CcfNetworkReconciler) reconcileDelete(
	ctx context.Context,
	network *v1alpha1.CcfNetwork,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "CcfNetwork", "Delete", network.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)

	if !controllerutil.ContainsFinalizer(
		network, ccfFinalizerName,
	) {
		return ctrl.Result{}, nil
	}

	log.Info("Deleting CCF network", "name", network.Name)
	r.event(
		network, corev1.EventTypeNormal,
		"Deleting", "CCF network deletion initiated",
	)

	network.Status.Phase = v1alpha1.CcfNetworkPhaseDeleting
	_ = r.Status().Update(ctx, network)

	if network.Spec.DeletionPolicy !=
		v1alpha1.DeletionPolicyDelete {
		log.Info(
			"Retaining external CCF network "+
				"(deletionPolicy is not \"delete\")",
			"name", network.Name,
			"deletionPolicy",
			network.Spec.DeletionPolicy,
		)
		r.event(
			network, corev1.EventTypeNormal,
			"Retained",
			"External CCF network retained per "+
				"deletionPolicy",
		)
	} else {
		deleteInput := &client.DeleteNetworkInput{
			InfraType: network.Spec.InfraType,
		}
		if network.Spec.ProviderConfig != nil {
			deleteInput.ProviderConfig =
				network.Spec.ProviderConfig.Raw
		}

		if err := r.CcfNetworkClient.DeleteNetwork(
			ctx, network.Name, deleteInput,
		); err != nil {
			log.Error(
				err,
				"Failed to delete CCF network via "+
					"provider",
			)
			recordError(span, err)
			r.eventf(
				network, corev1.EventTypeWarning,
				"DeleteFailed",
				"Failed to delete CCF network: %s",
				err.Error(),
			)
			return ctrl.Result{
				RequeueAfter: networkRequeueDelay,
			}, nil
		}
	}

	controllerutil.RemoveFinalizer(network, ccfFinalizerName)
	if err := r.Update(ctx, network); err != nil {
		if apierrors.IsNotFound(err) {
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"removing finalizer: %w", err,
		)
	}

	log.Info("CCF network deleted", "name", network.Name)
	r.event(
		network, corev1.EventTypeNormal,
		"Deleted", "CCF network deleted",
	)
	return ctrl.Result{}, nil
}

func (r *CcfNetworkReconciler) reconcileStatusSync(
	ctx context.Context,
	network *v1alpha1.CcfNetwork,
) (ctrl.Result, error) {
	return r.syncStatusFromProvider(ctx, network)
}

func (r *CcfNetworkReconciler) syncStatusFromProvider(
	ctx context.Context,
	network *v1alpha1.CcfNetwork,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	getInput := &client.GetNetworkInput{
		InfraType: network.Spec.InfraType,
	}
	if network.Spec.ProviderConfig != nil {
		getInput.ProviderConfig = network.Spec.ProviderConfig.Raw
	}

	resp, err := r.CcfNetworkClient.GetNetwork(
		ctx, network.Name, getInput,
	)
	if err != nil {
		log.Error(
			err, "Failed to get CCF network from provider",
		)
		failMsg := fmt.Sprintf(
			"Failed to get network from provider: %s",
			err.Error())
		meta.SetStatusCondition(
			&network.Status.Conditions,
			metav1.Condition{
				Type:    v1alpha1.ConditionTypeReady,
				Status:  metav1.ConditionFalse,
				Reason:  "SyncStatusFailed",
				Message: failMsg,
			},
		)
		_ = r.Status().Update(ctx, network)

		// Track consecutive sync failures. After
		// maxSyncFailures the resource transitions
		// to Failed so the parent environment
		// reflects the actual state.
		count := r.incrementSyncFailureCount(
			ctx, network,
		)
		if count >= maxSyncFailures {
			log.Info(
				"Max sync failures reached, "+
					"transitioning to Failed",
				"count", count,
			)
			network.Status.Phase =
				v1alpha1.CcfNetworkPhaseFailed
			r.event(
				network,
				corev1.EventTypeWarning,
				"SyncFailed",
				fmt.Sprintf(
					"CCF network sync failed %d "+
						"consecutive times: %s",
					count, err.Error()),
			)
			_ = r.Status().Update(ctx, network)
		}

		return ctrl.Result{RequeueAfter: networkRequeueDelay}, nil
	}

	if resp == nil {
		if network.Status.Phase == v1alpha1.CcfNetworkPhaseCreating {
			log.Info(
				"CCF network not found in provider, " +
					"retrying create",
			)
			network.Status.Phase = v1alpha1.CcfNetworkPhasePending
			network.Status.OperationId = ""
			if err := r.Status().Update(ctx, network); err != nil {
				return ctrl.Result{}, fmt.Errorf(
					"resetting phase: %w", err,
				)
			}
			return ctrl.Result{Requeue: true}, nil
		}

		// The network was previously running/open but the
		// provider now returns nil (backing resources may
		// have been deleted out of band). Track as a sync
		// failure so we eventually transition to Failed.
		if network.Status.Phase ==
			v1alpha1.CcfNetworkPhaseOpen ||
			network.Status.Phase ==
				v1alpha1.CcfNetworkPhaseRunning {
			log.Info(
				"CCF network not found in provider " +
					"but was previously running",
			)
			failMsg := "Network not found in provider" +
				" (backing resources may have been" +
				" deleted)"
			meta.SetStatusCondition(
				&network.Status.Conditions,
				metav1.Condition{
					Type:    v1alpha1.ConditionTypeReady,
					Status:  metav1.ConditionFalse,
					Reason:  "SyncStatusFailed",
					Message: failMsg,
				},
			)
			_ = r.Status().Update(ctx, network)

			count := r.incrementSyncFailureCount(
				ctx, network,
			)
			if count >= maxSyncFailures {
				log.Info(
					"Max sync failures reached, "+
						"transitioning to Failed",
					"count", count,
				)
				network.Status.Phase =
					v1alpha1.CcfNetworkPhaseFailed
				r.event(
					network,
					corev1.EventTypeWarning,
					"SyncFailed",
					fmt.Sprintf(
						"CCF network not found in"+
							" provider after %d"+
							" consecutive checks",
						count),
				)
				_ = r.Status().Update(ctx, network)
			}
		}

		return ctrl.Result{RequeueAfter: networkStatusSyncInterval}, nil
	}

	// Map provider response to CR status.
	network.Status.Endpoint = resp.Endpoint
	network.Status.Nodes = resp.Nodes
	network.Status.OperationId = ""
	r.clearSyncFailureCount(ctx, network)

	// Preserve Open phase if already transitioned.
	if network.Status.Phase != v1alpha1.CcfNetworkPhaseOpen {
		network.Status.Phase = v1alpha1.CcfNetworkPhaseRunning
	}

	meta.SetStatusCondition(
		&network.Status.Conditions,
		metav1.Condition{
			Type:    v1alpha1.ConditionTypeCcfNetworkCreated,
			Status:  metav1.ConditionTrue,
			Reason:  "NetworkReady",
			Message: "CCF network is running",
		},
	)
	meta.SetStatusCondition(
		&network.Status.Conditions,
		metav1.Condition{
			Type:    v1alpha1.ConditionTypeReady,
			Status:  metav1.ConditionTrue,
			Reason:  "Ready",
			Message: "CCF network is running and healthy",
		},
	)

	if err := r.Status().Update(ctx, network); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status from provider: %w", err,
		)
	}

	return ctrl.Result{RequeueAfter: networkStatusSyncInterval}, nil
}

// reconcileTransitionToOpen fetches the service certificate from
// the running CCF network and calls transitionToOpen to move the
// network from Running to Open.
func (r *CcfNetworkReconciler) reconcileTransitionToOpen(
	ctx context.Context,
	network *v1alpha1.CcfNetwork,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	if network.Status.Endpoint == "" {
		log.Info("No endpoint yet, syncing status first")
		return r.syncStatusFromProvider(ctx, network)
	}

	// Fetch service certificate from the network node.
	if network.Status.ServiceCert == "" {
		serviceCert, err := r.fetchServiceCert(
			ctx, network.Status.Endpoint,
		)
		if err != nil {
			log.Error(err,
				"Failed to fetch service certificate")
			return ctrl.Result{
				RequeueAfter: networkRequeueDelay,
			}, nil
		}
		network.Status.ServiceCert = serviceCert
		if err := r.Status().Update(ctx, network); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"updating service cert: %w", err,
			)
		}
		log.Info("Service certificate fetched")
	}

	// Wait for the operator member to be Active before
	// attempting transitionToOpen. The Environment controller
	// activates the member once the network is Running.
	memberName := network.Annotations[memberRefAnnotation]
	if memberName != "" {
		var member v1alpha1.CcfMember
		if err := r.Get(ctx, ctrlclient.ObjectKey{
			Name:      memberName,
			Namespace: network.Namespace,
		}, &member); err != nil {
			log.Info("CcfMember not found, waiting",
				"member", memberName)
			return ctrl.Result{
				RequeueAfter: networkRequeueDelay,
			}, nil
		}
		if isStaleResource(
			ctx, controllerOwnerUID(network),
			&member, "CcfMember", memberName,
		) || !member.DeletionTimestamp.IsZero() {
			return ctrl.Result{
				RequeueAfter: networkRequeueDelay,
			}, nil
		}
		if member.Status.Phase !=
			v1alpha1.CcfMemberPhaseActive {
			log.Info(
				"Waiting for member to be Active "+
					"before transitionToOpen",
				"member", memberName,
				"phase", member.Status.Phase)
			r.eventf(
				network, corev1.EventTypeNormal,
				"WaitingForMemberActivation",
				"Waiting for operator member %s to be activated (current phase: %s)",
				memberName, member.Status.Phase,
			)
			return ctrl.Result{
				RequeueAfter: networkRequeueDelay,
			}, nil
		}
	}

	// Configure the CCF provider with signing cert/key before
	// calling transitionToOpen. The provider needs these to
	// submit governance proposals.
	secretName := network.Annotations[memberSecretAnnotation]
	if secretName == "" {
		log.Info("No member-secret annotation, " +
			"cannot configure provider")
		r.event(
			network, corev1.EventTypeWarning,
			"MissingMemberSecret",
			"CcfNetwork missing member-secret annotation",
		)
		return ctrl.Result{RequeueAfter: networkRequeueDelay}, nil
	}
	var memberSecret corev1.Secret
	if err := r.Get(ctx, ctrlclient.ObjectKey{
		Name:      secretName,
		Namespace: network.Namespace,
	}, &memberSecret); err != nil {
		log.Error(err, "Failed to read member secret")
		return ctrl.Result{RequeueAfter: networkRequeueDelay}, nil
	}
	signingCert := string(
		memberSecret.Data[v1alpha1.SecretKeyCert],
	)
	signingKey := string(
		memberSecret.Data[v1alpha1.SecretKeyPrivateKey],
	)
	if signingCert == "" || signingKey == "" {
		log.Info("Member secret missing cert or key")
		return ctrl.Result{RequeueAfter: networkRequeueDelay}, nil
	}
	if err := r.CcfNetworkClient.ConfigureProvider(
		ctx, signingCert, signingKey,
	); err != nil {
		log.Error(err,
			"Failed to configure CCF provider")
		r.eventf(
			network, corev1.EventTypeWarning,
			"ConfigureProviderFailed",
			"Failed to configure provider: %s",
			err.Error(),
		)
		network.Status.Phase = v1alpha1.CcfNetworkPhaseFailed
		meta.SetStatusCondition(
			&network.Status.Conditions,
			metav1.Condition{
				Type:   v1alpha1.ConditionTypeTransitionToOpen,
				Status: metav1.ConditionFalse,
				Reason: "ConfigureProviderFailed",
				Message: fmt.Sprintf(
					"Failed to configure provider: %s",
					err.Error()),
			},
		)
		_ = r.Status().Update(ctx, network)
		return ctrl.Result{}, nil
	}
	log.Info("CCF provider configured with signing certs")
	r.event(
		network, corev1.EventTypeNormal,
		"ProviderConfigured",
		"CCF provider configured with signing credentials",
	)

	// Call transitionToOpen.
	log.Info("Transitioning CCF network to open",
		"name", network.Name)
	r.event(
		network, corev1.EventTypeNormal,
		"TransitioningToOpen",
		"Transitioning CCF network to open",
	)

	input := &client.TransitionToOpenInput{
		InfraType: network.Spec.InfraType,
	}
	if network.Spec.ProviderConfig != nil {
		input.ProviderConfig = network.Spec.ProviderConfig.Raw
	}

	if err := r.CcfNetworkClient.TransitionToOpen(
		ctx, network.Name, input,
	); err != nil {
		log.Error(err, "Failed to transition to open")
		r.eventf(
			network, corev1.EventTypeWarning,
			"TransitionToOpenFailed",
			"Failed to transition to open: %s", err.Error(),
		)
		network.Status.Phase = v1alpha1.CcfNetworkPhaseFailed
		meta.SetStatusCondition(
			&network.Status.Conditions,
			metav1.Condition{
				Type:   v1alpha1.ConditionTypeTransitionToOpen,
				Status: metav1.ConditionFalse,
				Reason: "TransitionToOpenFailed",
				Message: fmt.Sprintf(
					"Failed to transition to open: %s",
					err.Error()),
			},
		)
		_ = r.Status().Update(ctx, network)
		return ctrl.Result{}, nil
	}

	network.Status.Phase = v1alpha1.CcfNetworkPhaseOpen
	meta.SetStatusCondition(
		&network.Status.Conditions,
		metav1.Condition{
			Type:    v1alpha1.ConditionTypeCcfNetworkOpen,
			Status:  metav1.ConditionTrue,
			Reason:  "Open",
			Message: "CCF network transitioned to open",
		},
	)
	meta.SetStatusCondition(
		&network.Status.Conditions,
		metav1.Condition{
			Type:    v1alpha1.ConditionTypeReady,
			Status:  metav1.ConditionTrue,
			Reason:  "Open",
			Message: "CCF network is open and ready",
		},
	)
	if err := r.Status().Update(ctx, network); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after transition to open: %w",
			err,
		)
	}

	log.Info("CCF network is open", "name", network.Name)
	r.event(
		network, corev1.EventTypeNormal,
		"Open", "CCF network is open",
	)

	return ctrl.Result{RequeueAfter: networkStatusSyncInterval}, nil
}

// ccfNetworkResponse is the JSON shape returned by the CCF
// node /node/network endpoint.
type ccfNetworkResponse struct {
	ServiceCertificate string `json:"service_certificate"`
}

// fetchServiceCert retrieves the service certificate from the
// CCF network's /node/network endpoint.
func (r *CcfNetworkReconciler) fetchServiceCert(
	ctx context.Context,
	endpoint string,
) (string, error) {
	url := fmt.Sprintf("%s/node/network", endpoint)

	// The CCF node uses a self-signed TLS cert, so we skip
	// verification (same as curl -k in the script).
	httpClient := &http.Client{
		Transport: &http.Transport{
			TLSClientConfig: &tls.Config{
				InsecureSkipVerify: true, //nolint:gosec
			},
		},
	}

	req, err := http.NewRequestWithContext(
		ctx, http.MethodGet, url, nil,
	)
	if err != nil {
		return "", fmt.Errorf("creating request: %w", err)
	}

	resp, err := httpClient.Do(req)
	if err != nil {
		return "", fmt.Errorf(
			"calling /node/network: %w", err,
		)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		body, _ := io.ReadAll(resp.Body)
		return "", fmt.Errorf(
			"/node/network returned %d: %s",
			resp.StatusCode, string(body),
		)
	}

	var result ccfNetworkResponse
	if err := json.NewDecoder(resp.Body).Decode(
		&result,
	); err != nil {
		return "", fmt.Errorf(
			"decoding /node/network response: %w", err,
		)
	}

	return strings.TrimRight(
		result.ServiceCertificate, "\n",
	), nil
}

func (r *CcfNetworkReconciler) buildCreateInput(
	network *v1alpha1.CcfNetwork,
) *client.PutNetworkInput {
	input := &client.PutNetworkInput{
		NodeCount:    network.Spec.NodeCount,
		InfraType:    network.Spec.InfraType,
		NodeLogLevel: network.Spec.NodeLogLevel,
	}

	for _, m := range network.Spec.Members {
		member := client.MemberInput{
			Certificate:         m.Certificate,
			EncryptionPublicKey: m.EncryptionPublicKey,
		}
		if m.MemberData != nil {
			member.MemberData = m.MemberData.Raw
		}
		input.Members = append(input.Members, member)
	}

	if network.Spec.ProviderConfig != nil {
		input.ProviderConfig = network.Spec.ProviderConfig.Raw
	}

	if network.Spec.SecurityPolicyCreationOption != "" {
		input.SecurityPolicy = &client.SecurityPolicyInput{
			PolicyCreationOption: network.Spec.
				SecurityPolicyCreationOption,
		}
	}

	return input
}

// event emits a Kubernetes event on the CCF network resource.
func (r *CcfNetworkReconciler) event(
	network *v1alpha1.CcfNetwork,
	eventType string,
	reason string,
	message string,
) {
	if r.Recorder != nil {
		r.Recorder.Event(
			network, eventType, reason, message,
		)
	}
}

// eventf emits a formatted Kubernetes event.
func (r *CcfNetworkReconciler) eventf(
	network *v1alpha1.CcfNetwork,
	eventType string,
	reason string,
	msgFmt string,
	args ...interface{},
) {
	if r.Recorder != nil {
		r.Recorder.Eventf(
			network, eventType, reason, msgFmt, args...,
		)
	}
}

// SetupWithManager sets up the controller with the Manager.
func (r *CcfNetworkReconciler) SetupWithManager(
	mgr ctrl.Manager,
) error {
	return ctrl.NewControllerManagedBy(mgr).
		For(&v1alpha1.CcfNetwork{}).
		Complete(r)
}
