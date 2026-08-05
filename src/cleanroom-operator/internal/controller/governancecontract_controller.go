package controller

import (
	"context"
	"encoding/base64"
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
	"github.com/Azure/azure-cleanroom/cleanroom-operator/client"
)

const (
	gcFinalizerName = "cleanroom.azure.com/gc-finalizer"
	gcRequeueDelay  = 15 * time.Second

	// virtualAllowAllHostData is the well-known hostData
	// value for the allow-all CCE policy used in virtual
	// (non-SNP) mode.
	virtualAllowAllHostData = "73973b78d70cc68353426de188db5dfc57e5b766e399935fb73a61127ea26d20"
)

// GovernanceContractReconciler reconciles GovernanceContract
// objects.
type GovernanceContractReconciler struct {
	ctrlclient.Client
	Scheme           *runtime.Scheme
	CgsClient        *client.CgsClient
	CcfNetworkClient *client.CcfNetworkClient
	Recorder         record.EventRecorder
}

// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=governancecontracts,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=governancecontracts/status,verbs=get;update;patch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=governancecontracts/finalizers,verbs=update
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=ccfnetworks,verbs=get;list;watch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=ccfmembers,verbs=get;list;watch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=governanceservices,verbs=get;list;watch
// +kubebuilder:rbac:groups="",resources=configmaps,verbs=get;list;watch;create;update;patch
// +kubebuilder:rbac:groups="",resources=secrets,verbs=get;list;watch
// +kubebuilder:rbac:groups="",resources=events,verbs=create;patch

// Reconcile handles the reconciliation loop for
// GovernanceContract.
func (r *GovernanceContractReconciler) Reconcile(
	ctx context.Context,
	req ctrl.Request,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	var gc v1alpha1.GovernanceContract
	if err := r.Get(
		ctx, req.NamespacedName, &gc,
	); err != nil {
		if apierrors.IsNotFound(err) {
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"fetching GovernanceContract: %w", err,
		)
	}

	ctx = resolveTraceContext(
		ctx, &gc, gc.Status.TraceParent,
	)
	ctx, span := startSpan(
		ctx, "GovernanceContract", "Reconcile",
		gc.Name,
	)
	defer span.End()

	// Handle deletion.
	if !gc.DeletionTimestamp.IsZero() {
		return r.reconcileDelete(ctx, &gc)
	}

	// Ensure finalizer.
	if !controllerutil.ContainsFinalizer(
		&gc, gcFinalizerName,
	) {
		controllerutil.AddFinalizer(&gc, gcFinalizerName)
		if err := r.Update(ctx, &gc); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"adding finalizer: %w", err,
			)
		}
		return ctrl.Result{Requeue: true}, nil
	}

	// Route based on phase.
	switch gc.Status.Phase {
	case "",
		v1alpha1.GovernanceContractPhasePending,
		v1alpha1.GovernanceContractPhaseWaiting,
		v1alpha1.GovernanceContractPhaseWaitingForGS:
		return r.reconcileWaitForNetwork(ctx, &gc)

	case v1alpha1.GovernanceContractPhaseConfiguring:
		return r.reconcileConfiguring(ctx, &gc)

	case v1alpha1.GovernanceContractPhaseReady:
		return ctrl.Result{}, nil

	case v1alpha1.GovernanceContractPhaseFailed:
		if gc.Generation !=
			gc.Status.ObservedGeneration ||
			gc.Annotations[retryAnnotation] != "" {
			if gc.Annotations[retryAnnotation] != "" {
				delete(gc.Annotations, retryAnnotation)
			}
			if err := r.Update(
				ctx, &gc,
			); err != nil {
				return ctrl.Result{}, fmt.Errorf(
					"clearing retry annotation: %w",
					err,
				)
			}
			r.gcEvent(&gc, corev1.EventTypeNormal,
				"Retrying",
				"Retry requested via annotation")
			// Set transitional phase immediately so
			// the parent controller does not see a
			// stale Failed phase after the retry
			// annotation has been cleared.
			gc.Status.Phase =
				v1alpha1.GovernanceContractPhaseWaiting
			gc.Status.Conditions = nil
			if err := r.Status().Update(
				ctx, &gc,
			); err != nil {
				return ctrl.Result{}, fmt.Errorf(
					"setting transitional phase: %w",
					err,
				)
			}
			return r.reconcileWaitForNetwork(ctx, &gc)
		}
		return ctrl.Result{}, nil

	default:
		log.Info("Unknown phase, requeueing",
			"phase", gc.Status.Phase)
		return ctrl.Result{
			RequeueAfter: gcRequeueDelay,
		}, nil
	}
}

// reconcileWaitForNetwork waits for the CcfNetwork to reach
// Open phase and the operator CcfMember to be Active.
func (r *GovernanceContractReconciler) reconcileWaitForNetwork(
	ctx context.Context,
	gc *v1alpha1.GovernanceContract,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	// Resolve the Environment UID by walking the
	// ownership chain: GC → WorkloadGovernance → Env.
	envUID, err := r.resolveEnvUID(ctx, gc)
	if err != nil {
		return ctrl.Result{}, err
	}
	if envUID == "" {
		// Owner not found yet, requeue.
		return ctrl.Result{
			RequeueAfter: gcRequeueDelay,
		}, nil
	}

	// Set WaitingForNetwork phase if not already in a
	// waiting sub-phase.
	if gc.Status.Phase !=
		v1alpha1.GovernanceContractPhaseWaiting &&
		gc.Status.Phase !=
			v1alpha1.GovernanceContractPhaseWaitingForGS {
		gc.Status.Phase =
			v1alpha1.GovernanceContractPhaseWaiting
		gc.Status.ObservedGeneration = gc.Generation
		if err := r.Status().Update(ctx, gc); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"setting WaitingForNetwork phase: %w", err,
			)
		}
		return ctrl.Result{Requeue: true}, nil
	}

	// Check CcfNetwork.
	var network v1alpha1.CcfNetwork
	if err := r.Get(ctx, types.NamespacedName{
		Name:      gc.Spec.NetworkRef,
		Namespace: gc.Namespace,
	}, &network); err != nil {
		if apierrors.IsNotFound(err) {
			log.Info("CcfNetwork not found, waiting",
				"network", gc.Spec.NetworkRef)
			return ctrl.Result{
				RequeueAfter: gcRequeueDelay,
			}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"fetching CcfNetwork: %w", err,
		)
	}

	if isStaleResource(
		ctx, envUID, &network,
		"CcfNetwork", gc.Spec.NetworkRef,
	) || !network.DeletionTimestamp.IsZero() {
		return ctrl.Result{
			RequeueAfter: gcRequeueDelay,
		}, nil
	}

	if network.Status.Phase !=
		v1alpha1.CcfNetworkPhaseOpen {
		log.Info("CcfNetwork not Open yet, waiting",
			"network", gc.Spec.NetworkRef,
			"phase", network.Status.Phase)
		if gc.Status.Phase !=
			v1alpha1.GovernanceContractPhaseWaiting {
			gc.Status.Phase =
				v1alpha1.GovernanceContractPhaseWaiting
			_ = r.Status().Update(ctx, gc)
		}
		return ctrl.Result{
			RequeueAfter: gcRequeueDelay,
		}, nil
	}

	// Check CcfMember.
	var member v1alpha1.CcfMember
	if err := r.Get(ctx, types.NamespacedName{
		Name:      gc.Spec.MemberRef,
		Namespace: gc.Namespace,
	}, &member); err != nil {
		if apierrors.IsNotFound(err) {
			log.Info("CcfMember not found, waiting",
				"member", gc.Spec.MemberRef)
			return ctrl.Result{
				RequeueAfter: gcRequeueDelay,
			}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"fetching CcfMember: %w", err,
		)
	}

	if isStaleResource(
		ctx, envUID, &member,
		"CcfMember", gc.Spec.MemberRef,
	) || !member.DeletionTimestamp.IsZero() {
		return ctrl.Result{
			RequeueAfter: gcRequeueDelay,
		}, nil
	}

	if member.Status.Phase !=
		v1alpha1.CcfMemberPhaseActive {
		log.Info("CcfMember not Active yet, waiting",
			"member", gc.Spec.MemberRef,
			"phase", member.Status.Phase)
		return ctrl.Result{
			RequeueAfter: gcRequeueDelay,
		}, nil
	}

	// Check GovernanceService is Ready.
	var gs v1alpha1.GovernanceService
	if err := r.Get(ctx, types.NamespacedName{
		Name:      gc.Spec.GovernanceServiceRef,
		Namespace: gc.Namespace,
	}, &gs); err != nil {
		if apierrors.IsNotFound(err) {
			log.Info(
				"GovernanceService not found, "+
					"waiting",
				"gs",
				gc.Spec.GovernanceServiceRef,
			)
			return ctrl.Result{
				RequeueAfter: gcRequeueDelay,
			}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"fetching GovernanceService: %w", err,
		)
	}

	if isStaleResource(
		ctx, envUID, &gs,
		"GovernanceService",
		gc.Spec.GovernanceServiceRef,
	) || !gs.DeletionTimestamp.IsZero() {
		return ctrl.Result{
			RequeueAfter: gcRequeueDelay,
		}, nil
	}

	if gs.Status.Phase !=
		v1alpha1.GovernanceServicePhaseReady {
		log.Info(
			"GovernanceService not Ready yet, "+
				"waiting",
			"gs", gc.Spec.GovernanceServiceRef,
			"phase", gs.Status.Phase,
		)
		if gc.Status.Phase !=
			v1alpha1.GovernanceContractPhaseWaitingForGS {
			gc.Status.Phase =
				v1alpha1.GovernanceContractPhaseWaitingForGS
		}

		// Set a descriptive condition so upstream
		// resources can see why we're blocked.
		msg := fmt.Sprintf(
			"waiting for GovernanceService %q "+
				"(phase: %s)",
			gc.Spec.GovernanceServiceRef,
			gs.Status.Phase,
		)
		if gs.Status.Phase ==
			v1alpha1.GovernanceServicePhaseFailed {
			// Extract the failure message from the
			// GovernanceService conditions.
			if failedCond := meta.FindStatusCondition(
				gs.Status.Conditions, "Failed",
			); failedCond != nil {
				msg = fmt.Sprintf(
					"blocked: GovernanceService %q "+
						"failed: %s",
					gc.Spec.GovernanceServiceRef,
					failedCond.Message,
				)
			} else {
				msg = fmt.Sprintf(
					"blocked: GovernanceService %q "+
						"is in Failed state",
					gc.Spec.GovernanceServiceRef,
				)
			}
		}
		meta.SetStatusCondition(
			&gc.Status.Conditions,
			metav1.Condition{
				Type:    v1alpha1.ConditionTypeReady,
				Status:  metav1.ConditionFalse,
				Reason:  "WaitingForGovernanceService",
				Message: msg,
			},
		)
		_ = r.Status().Update(ctx, gc)

		return ctrl.Result{
			RequeueAfter: gcRequeueDelay,
		}, nil
	}

	// Transition to Configuring.
	_, span := startSpan(
		ctx, "GovernanceContract",
		"DepsReady", gc.Name,
	)
	defer span.End()

	gc.Status.Phase =
		v1alpha1.GovernanceContractPhaseConfiguring
	gc.Status.TraceParent,
		gc.Status.LastOperationTraceId =
		saveTrace(ctx)
	if err := r.Status().Update(ctx, gc); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"transitioning to Configuring: %w", err,
		)
	}

	r.gcEvent(gc, corev1.EventTypeNormal,
		"NetworkReady",
		"CcfNetwork is Open and member is Active")

	return ctrl.Result{Requeue: true}, nil
}

// reconcileConfiguring drives the governance setup workflow
// step by step. Each step is tracked via a status condition
// so progress survives restarts.
func (r *GovernanceContractReconciler) reconcileConfiguring(
	ctx context.Context,
	gc *v1alpha1.GovernanceContract,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	// Resolve the operator member's CGS endpoint and certs.
	memberCtx, err := r.resolveMemberContext(ctx, gc)
	if err != nil {
		log.Error(err, "Failed to resolve member context")
		return ctrl.Result{
			RequeueAfter: gcRequeueDelay,
		}, nil
	}

	// Resolve network context.
	networkCtx, err := r.resolveNetworkContext(ctx, gc)
	if err != nil {
		log.Error(err, "Failed to resolve network context")
		return ctrl.Result{
			RequeueAfter: gcRequeueDelay,
		}, nil
	}

	ep := memberCtx.endpoint

	// Steps 1+2: Create and accept the contract.
	// Always queries CGS for actual contract state
	// rather than relying on condition flags.
	contract, err := r.CgsClient.GetContract(
		ctx, ep, gc.Spec.ContractId,
	)
	if err != nil {
		log.Error(err, "Failed to get contract",
			"contractId", gc.Spec.ContractId)
		return ctrl.Result{
			RequeueAfter: gcRequeueDelay,
		}, nil
	}
	if contract == nil || contract.State != "Accepted" {
		return r.stepEnsureContractAccepted(
			ctx, gc, ep, networkCtx, memberCtx,
			contract,
		)
	}
	// Ensure conditions reflect reality.
	r.setConditionTrue(gc,
		v1alpha1.ConditionTypeContractCreated,
		"Created", "Contract exists")
	r.setConditionTrue(gc,
		v1alpha1.ConditionTypeContractAccepted,
		"Accepted", "Contract accepted")

	// Step 4: Deployment spec (if provided).
	if gc.Spec.DeploymentSpec != nil &&
		!r.conditionIsTrue(gc,
			v1alpha1.ConditionTypeDeploymentSpecAccepted) {
		return r.stepProposeAndAccept(
			ctx, gc, ep,
			v1alpha1.ConditionTypeDeploymentSpecAccepted,
			"DeploymentSpec",
			func() (*client.ProposalResponse, error) {
				return r.CgsClient.ProposeDeploymentSpec(
					ctx, ep, gc.Spec.ContractId,
					gc.Spec.DeploymentSpec.Raw,
				)
			},
		)
	}

	// Step 5: Clean room policy (if provided).
	if gc.Spec.CleanRoomPolicy != nil &&
		!r.conditionIsTrue(gc,
			v1alpha1.ConditionTypeCleanRoomPolicyAccepted) {
		return r.stepProposeAndAccept(
			ctx, gc, ep,
			v1alpha1.ConditionTypeCleanRoomPolicyAccepted,
			"CleanRoomPolicy",
			func() (*client.ProposalResponse, error) {
				return r.CgsClient.ProposeCleanRoomPolicy(
					ctx, ep, gc.Spec.ContractId,
					gc.Spec.CleanRoomPolicy.Raw,
				)
			},
		)
	}

	// Step 6: Enable logging (if requested).
	if gc.Spec.RuntimeOptions.EnableLogging &&
		!r.conditionIsTrue(gc,
			v1alpha1.ConditionTypeLoggingEnabled) {
		return r.stepProposeAndAccept(
			ctx, gc, ep,
			v1alpha1.ConditionTypeLoggingEnabled,
			"Logging",
			func() (*client.ProposalResponse, error) {
				return r.CgsClient.ProposeEnableLogging(
					ctx, ep, gc.Spec.ContractId,
				)
			},
		)
	}

	// Step 7: Enable telemetry (if requested).
	if gc.Spec.RuntimeOptions.EnableTelemetry &&
		!r.conditionIsTrue(gc,
			v1alpha1.ConditionTypeTelemetryEnabled) {
		return r.stepProposeAndAccept(
			ctx, gc, ep,
			v1alpha1.ConditionTypeTelemetryEnabled,
			"Telemetry",
			func() (*client.ProposalResponse, error) {
				return r.CgsClient.ProposeEnableTelemetry(
					ctx, ep, gc.Spec.ContractId,
				)
			},
		)
	}

	// Step 8: Enable CA (if requested).
	if gc.Spec.EnableCA &&
		!r.conditionIsTrue(gc,
			v1alpha1.ConditionTypeCAEnabled) {
		return r.stepProposeAndAccept(
			ctx, gc, ep,
			v1alpha1.ConditionTypeCAEnabled,
			"CAEnable",
			func() (*client.ProposalResponse, error) {
				return r.CgsClient.ProposeEnableCA(
					ctx, ep, gc.Spec.ContractId,
				)
			},
		)
	}

	// Step 9: Generate CA signing key (if CA enabled).
	if gc.Spec.EnableCA &&
		!r.conditionIsTrue(gc,
			v1alpha1.ConditionTypeCAKeyGenerated) {
		return r.stepGenerateCAKey(ctx, gc, ep)
	}

	// Step 10: Write output ConfigMap.
	if !r.conditionIsTrue(gc,
		v1alpha1.ConditionTypeConfigMapReady) {
		return r.stepWriteConfigMap(
			ctx, gc, networkCtx,
		)
	}

	// All steps complete — transition to Ready.
	gc.Status.Phase = v1alpha1.GovernanceContractPhaseReady
	gc.Status.TraceParent = ""
	meta.SetStatusCondition(
		&gc.Status.Conditions,
		metav1.Condition{
			Type:   v1alpha1.ConditionTypeReady,
			Status: metav1.ConditionTrue,
			Reason: "AllStepsComplete",
			Message: "Governance contract is fully " +
				"configured",
		},
	)
	if err := r.Status().Update(ctx, gc); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"transitioning to Ready: %w", err,
		)
	}

	r.gcEvent(gc, corev1.EventTypeNormal,
		"Ready", "Governance contract is ready")

	return ctrl.Result{}, nil
}

// memberContext holds resolved member information.
type memberContext struct {
	endpoint    string
	signingCert string
	signingKey  string
}

// networkContext holds resolved network information.
type networkContext struct {
	networkName    string
	infraType      string
	endpoint       string
	serviceCert    string
	providerConfig json.RawMessage
}

// resolveMemberContext looks up the CcfMember and reads its
// certs from the referenced Secret.
func (r *GovernanceContractReconciler) resolveMemberContext(
	ctx context.Context,
	gc *v1alpha1.GovernanceContract,
) (*memberContext, error) {
	var member v1alpha1.CcfMember
	if err := r.Get(ctx, types.NamespacedName{
		Name:      gc.Spec.MemberRef,
		Namespace: gc.Namespace,
	}, &member); err != nil {
		return nil, fmt.Errorf(
			"fetching CcfMember %s: %w",
			gc.Spec.MemberRef, err,
		)
	}

	if member.Status.GovernanceClientEndpoint == "" {
		return nil, fmt.Errorf(
			"CcfMember %s has no governance endpoint",
			gc.Spec.MemberRef,
		)
	}

	// Read the member's cert Secret.
	var secret corev1.Secret
	if err := r.Get(ctx, types.NamespacedName{
		Name:      member.Status.SecretRef,
		Namespace: gc.Namespace,
	}, &secret); err != nil {
		return nil, fmt.Errorf(
			"fetching member secret %s: %w",
			member.Status.SecretRef, err,
		)
	}

	return &memberContext{
		endpoint: member.Status.GovernanceClientEndpoint,
		signingCert: string(
			secret.Data[v1alpha1.SecretKeyCert],
		),
		signingKey: string(
			secret.Data[v1alpha1.SecretKeyPrivateKey],
		),
	}, nil
}

// resolveNetworkContext looks up the CcfNetwork status.
func (r *GovernanceContractReconciler) resolveNetworkContext(
	ctx context.Context,
	gc *v1alpha1.GovernanceContract,
) (*networkContext, error) {
	var network v1alpha1.CcfNetwork
	if err := r.Get(ctx, types.NamespacedName{
		Name:      gc.Spec.NetworkRef,
		Namespace: gc.Namespace,
	}, &network); err != nil {
		return nil, fmt.Errorf(
			"fetching CcfNetwork %s: %w",
			gc.Spec.NetworkRef, err,
		)
	}

	if network.Status.Endpoint == "" {
		return nil, fmt.Errorf(
			"CcfNetwork %s has no endpoint",
			gc.Spec.NetworkRef,
		)
	}

	var pc json.RawMessage
	if network.Spec.ProviderConfig != nil {
		pc = network.Spec.ProviderConfig.Raw
	}

	return &networkContext{
		networkName:    network.Name,
		infraType:      network.Spec.InfraType,
		endpoint:       network.Status.Endpoint,
		serviceCert:    network.Status.ServiceCert,
		providerConfig: pc,
	}, nil
}

// stepCreateContract creates the governance contract via
// PUT /contracts/{contractId}. When spec.data is empty, the
// contract payload is synthesized from CcfNetwork status,
// recovery agent report, and CGS member list.
// stepEnsureContractAccepted drives the contract through
// its lifecycle (create → propose → accept) using the
// actual CGS contract state for idempotency rather than
// condition flags. The contract parameter is the result of
// a prior GetContract call (nil means not found).
func (r *GovernanceContractReconciler) stepEnsureContractAccepted(
	ctx context.Context,
	gc *v1alpha1.GovernanceContract,
	endpoint string,
	nc *networkContext,
	mc *memberContext,
	contract *client.ContractResponse,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	// Contract does not exist yet — create it.
	if contract == nil {
		ctx, span := startSpan(
			ctx, "GovernanceContract",
			"CreateContract", gc.Name,
		)
		defer span.End()
		return r.createContract(
			ctx, gc, endpoint, nc, mc,
		)
	}

	switch contract.State {
	case "Accepted":
		// No span — just sync conditions, no real work.
		log.Info("Contract already accepted",
			"contractId", gc.Spec.ContractId)
		r.setConditionTrue(gc,
			v1alpha1.ConditionTypeContractCreated,
			"Created",
			"Contract exists")
		r.setConditionTrue(gc,
			v1alpha1.ConditionTypeContractAccepted,
			"Accepted",
			"Contract accepted")
		if err := r.Status().Update(
			ctx, gc,
		); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"updating status: %w", err,
			)
		}
		r.gcEvent(gc, corev1.EventTypeNormal,
			"ContractAccepted",
			fmt.Sprintf("Contract %s accepted",
				gc.Spec.ContractId))
		return ctrl.Result{Requeue: true}, nil

	case "Proposed":
		log.Info(
			"Contract is Proposed, voting",
			"contractId", gc.Spec.ContractId,
			"proposalId", contract.ProposalID,
		)
		if gc.Spec.AutoApprove {
			ctx, span := startSpan(
				ctx, "GovernanceContract",
				"VoteContract", gc.Name,
			)
			defer span.End()
			if err := r.CgsClient.VoteAcceptContract(
				ctx, endpoint,
				gc.Spec.ContractId,
				contract.ProposalID,
			); err != nil {
				if isProposalAlreadyAccepted(err) {
					log.Info(
						"Proposal already accepted",
						"proposalId",
						contract.ProposalID,
					)
				} else {
					if sErr := r.setGCFailed(
						ctx, gc,
						"ContractVoteFailed",
						err); sErr != nil {
						return ctrl.Result{}, sErr
					}
					return ctrl.Result{
						RequeueAfter: gcRequeueDelay,
					}, nil
				}
			}
		}
		// Re-check state on next reconcile to
		// confirm acceptance (may need more votes).
		return ctrl.Result{
			RequeueAfter: gcRequeueDelay,
		}, nil

	default:
		// Draft — propose and vote.
		ctx, span := startSpan(
			ctx, "GovernanceContract",
			"ProposeContract", gc.Name,
		)
		defer span.End()

		log = ctrllog.FromContext(ctx)
		log.Info("Proposing contract",
			"contractId", gc.Spec.ContractId,
			"version", contract.Version)

		propResp, err := r.CgsClient.ProposeContract(
			ctx, endpoint, gc.Spec.ContractId,
			contract.Version,
		)
		if err != nil {
			if sErr := r.setGCFailed(ctx, gc,
				"ContractProposeFailed",
				err); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{
				RequeueAfter: gcRequeueDelay,
			}, nil
		}

		if gc.Spec.AutoApprove &&
			propResp.ProposalState != "Accepted" {
			log.Info("Voting accept on contract",
				"proposalId", propResp.ProposalID)
			if err := r.CgsClient.VoteAcceptContract(
				ctx, endpoint,
				gc.Spec.ContractId,
				propResp.ProposalID,
			); err != nil {
				if isProposalAlreadyAccepted(err) {
					log.Info(
						"Proposal already accepted",
						"proposalId",
						propResp.ProposalID,
					)
				} else {
					if sErr := r.setGCFailed(
						ctx, gc,
						"ContractVoteFailed",
						err); sErr != nil {
						return ctrl.Result{}, sErr
					}
					return ctrl.Result{
						RequeueAfter: gcRequeueDelay,
					}, nil
				}
			}
		}
		// Re-check state on next reconcile.
		return ctrl.Result{
			RequeueAfter: gcRequeueDelay,
		}, nil
	}
}

// createContract creates the contract in CGS in Draft
// state, synthesizing data if not provided.
func (r *GovernanceContractReconciler) createContract(
	ctx context.Context,
	gc *v1alpha1.GovernanceContract,
	endpoint string,
	nc *networkContext,
	mc *memberContext,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)
	log.Info("Creating governance contract",
		"contractId", gc.Spec.ContractId)

	data := gc.Spec.Data
	if data == "" {
		synthesized, err := r.synthesizeContractData(
			ctx, gc, nc, mc,
		)
		if err != nil {
			if sErr := r.setGCFailed(ctx, gc,
				"ContractDataSynthesisFailed",
				err); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{
				RequeueAfter: gcRequeueDelay,
			}, nil
		}
		data = synthesized
		log.Info("Synthesized contract data from "+
			"CcfNetwork status",
			"contractId", gc.Spec.ContractId)
	}

	err := r.CgsClient.CreateContract(
		ctx, endpoint,
		gc.Spec.ContractId, data,
	)
	if err != nil {
		if sErr := r.setGCFailed(ctx, gc,
			"ContractCreateFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: gcRequeueDelay,
		}, nil
	}

	r.gcEvent(gc, corev1.EventTypeNormal,
		"ContractCreated",
		fmt.Sprintf("Contract %s created",
			gc.Spec.ContractId))

	// Requeue to re-enter stepEnsureContractAccepted
	// which will now find the contract in Draft state.
	return ctrl.Result{Requeue: true}, nil
}

// contractData is the JSON structure sent to
// PUT /contracts/{contractId}.
type contractData struct {
	CcrgovEndpoint             string                `json:"ccrgovEndpoint"`
	CcrgovAPIPathPrefix        string                `json:"ccrgovApiPathPrefix"`
	CcrgovServiceCertDiscovery *serviceCertDiscovery `json:"ccrgovServiceCertDiscovery,omitempty"`
	CcfNetworkRecoveryMembers  []string              `json:"ccfNetworkRecoveryMembers,omitempty"`
}

// serviceCertDiscovery holds the CCF service certificate
// discovery configuration.
type serviceCertDiscovery struct {
	Endpoint           string `json:"endpoint"`
	SnpHostData        string `json:"snpHostData"`
	ConstitutionDigest string `json:"constitutionDigest"`
	JsappBundleDigest  string `json:"jsappBundleDigest"`
}

// synthesizeContractData builds the contract data JSON
// payload from CcfNetwork status, recovery agent report,
// and CGS member list.
func (r *GovernanceContractReconciler) synthesizeContractData(
	ctx context.Context,
	gc *v1alpha1.GovernanceContract,
	nc *networkContext,
	mc *memberContext,
) (string, error) {
	cd := contractData{
		CcrgovEndpoint: nc.endpoint,
		CcrgovAPIPathPrefix: fmt.Sprintf(
			"/app/contracts/%s", gc.Spec.ContractId,
		),
	}

	// Fetch recovery agent info and report for service
	// cert discovery.
	if r.CcfNetworkClient != nil {
		discovery, err := r.buildServiceCertDiscovery(
			ctx, nc,
		)
		if err != nil {
			return "", fmt.Errorf(
				"building service cert discovery: %w",
				err,
			)
		}
		cd.CcrgovServiceCertDiscovery = discovery
	}

	// Fetch recovery members from CGS.
	recoveryMembers, err := r.getRecoveryMemberIDs(
		ctx, mc.endpoint,
	)
	if err != nil {
		return "", fmt.Errorf(
			"fetching recovery members: %w", err,
		)
	}
	cd.CcfNetworkRecoveryMembers = recoveryMembers

	b, err := json.Marshal(cd)
	if err != nil {
		return "", fmt.Errorf(
			"marshaling contract data: %w", err,
		)
	}

	return string(b), nil
}

// buildServiceCertDiscovery fetches the recovery agent
// endpoint and report to build the service cert discovery
// section of the contract data.
func (r *GovernanceContractReconciler) buildServiceCertDiscovery(
	ctx context.Context,
	nc *networkContext,
) (*serviceCertDiscovery, error) {
	agent, err := r.CcfNetworkClient.GetRecoveryAgent(
		ctx, nc.networkName, nc.infraType,
		nc.providerConfig,
	)
	if err != nil {
		return nil, fmt.Errorf(
			"fetching recovery agent: %w", err,
		)
	}
	if agent == nil {
		// No recovery agent deployed; skip discovery.
		return nil, nil
	}

	// Fetch the recovery agent's network report to get
	// constitutionDigest and jsappBundleDigest from the
	// reportDataPayload.
	agentReport, err :=
		r.CcfNetworkClient.GetRecoveryAgentNetworkReport(
			ctx, nc.networkName, nc.infraType,
			nc.providerConfig,
		)
	if err != nil {
		return nil, fmt.Errorf(
			"fetching recovery agent network report: %w",
			err,
		)
	}

	// Parse the report data payload to extract digests.
	var constitutionDigest, jsappBundleDigest string
	if len(agentReport.Reports) > 0 &&
		agentReport.Reports[0].Report != nil {
		reportData, err := base64Decode(
			agentReport.Reports[0].Report.ReportDataPayload,
		)
		if err == nil {
			var payload struct {
				ConstitutionDigest string `json:"constitutionDigest"`
				JsappBundleDigest  string `json:"jsappBundleDigest"`
			}
			if err := json.Unmarshal(
				reportData, &payload,
			); err == nil {
				constitutionDigest = payload.ConstitutionDigest
				jsappBundleDigest = payload.JsappBundleDigest
			}
		}
	}

	// snpHostData: for virtual infra use the well-known
	// allow-all value; for SNP (caci) fetch from the CCF
	// network report.
	var snpHostData string
	if nc.infraType == "virtual" {
		snpHostData = virtualAllowAllHostData
	} else {
		networkReport, err := r.CcfNetworkClient.GetReport(
			ctx, nc.networkName, nc.infraType,
			nc.providerConfig,
		)
		if err != nil {
			return nil, fmt.Errorf(
				"fetching network report: %w", err,
			)
		}
		if len(networkReport.Reports) > 0 {
			snpHostData = networkReport.Reports[0].HostData
		}
	}

	return &serviceCertDiscovery{
		Endpoint: fmt.Sprintf(
			"%s/network/report", agent.Endpoint,
		),
		SnpHostData:        snpHostData,
		ConstitutionDigest: constitutionDigest,
		JsappBundleDigest:  jsappBundleDigest,
	}, nil
}

// getRecoveryMemberIDs returns the member IDs of all
// consortium members that have a public encryption key
// (i.e. recovery members).
func (r *GovernanceContractReconciler) getRecoveryMemberIDs(
	ctx context.Context,
	cgsEndpoint string,
) ([]string, error) {
	members, err := r.CgsClient.GetMembers(
		ctx, cgsEndpoint,
	)
	if err != nil {
		return nil, err
	}

	var ids []string
	for _, m := range members.Value {
		if m.PublicEncryptionKey != "" {
			ids = append(ids, m.MemberID)
		}
	}
	return ids, nil
}

// base64Decode decodes a base64 string.
func base64Decode(s string) ([]byte, error) {
	return base64.StdEncoding.DecodeString(s)
}

// stepProposeAndAccept proposes an action and votes accept
// on the resulting proposal (if autoApprove is enabled).
func (r *GovernanceContractReconciler) stepProposeAndAccept(
	ctx context.Context,
	gc *v1alpha1.GovernanceContract,
	endpoint string,
	conditionType string,
	stepName string,
	proposeFn func() (*client.ProposalResponse, error),
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "GovernanceContract", stepName, gc.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	log.Info("Proposing governance action",
		"step", stepName)

	propResp, err := proposeFn()
	if err != nil {
		if sErr := r.setGCFailed(ctx, gc,
			stepName+"ProposeFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: gcRequeueDelay,
		}, nil
	}

	if gc.Spec.AutoApprove {
		log.Info("Voting accept on proposal",
			"step", stepName,
			"proposalId", propResp.ProposalID)

		if err := r.CgsClient.VoteAcceptProposal(
			ctx, endpoint, propResp.ProposalID,
		); err != nil {
			if sErr := r.setGCFailed(ctx, gc,
				stepName+"VoteFailed", err); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{
				RequeueAfter: gcRequeueDelay,
			}, nil
		}
	}

	r.setConditionTrue(gc, conditionType,
		stepName+"Accepted",
		stepName+" proposal accepted")
	if err := r.Status().Update(ctx, gc); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after %s: %w",
			stepName, err,
		)
	}

	r.gcEvent(gc, corev1.EventTypeNormal,
		stepName+"Accepted",
		fmt.Sprintf("%s proposal accepted", stepName))

	return ctrl.Result{Requeue: true}, nil
}

// stepGenerateCAKey generates the CA signing key for the
// contract.
func (r *GovernanceContractReconciler) stepGenerateCAKey(
	ctx context.Context,
	gc *v1alpha1.GovernanceContract,
	endpoint string,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "GovernanceContract",
		"GenerateCAKey", gc.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	log.Info("Generating CA signing key",
		"contractId", gc.Spec.ContractId)

	if err := r.CgsClient.GenerateCASigningKey(
		ctx, endpoint, gc.Spec.ContractId,
	); err != nil {
		if sErr := r.setGCFailed(ctx, gc,
			"CAKeyGenFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: gcRequeueDelay,
		}, nil
	}

	// Fetch the CA cert and store it in status.
	caInfo, err := r.CgsClient.GetCAInfo(
		ctx, endpoint, gc.Spec.ContractId,
	)
	if err != nil {
		if sErr := r.setGCFailed(ctx, gc,
			"CAInfoFetchFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: gcRequeueDelay,
		}, nil
	}
	gc.Status.CaCert = caInfo.CaCert

	r.setConditionTrue(gc,
		v1alpha1.ConditionTypeCAKeyGenerated,
		"Generated",
		"CA signing key generated")
	if err := r.Status().Update(ctx, gc); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after CA key gen: %w", err,
		)
	}

	r.gcEvent(gc, corev1.EventTypeNormal,
		"CAKeyGenerated", "CA signing key generated")

	return ctrl.Result{Requeue: true}, nil
}

// stepWriteConfigMap writes governance outputs to a ConfigMap.
func (r *GovernanceContractReconciler) stepWriteConfigMap(
	ctx context.Context,
	gc *v1alpha1.GovernanceContract,
	nc *networkContext,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "GovernanceContract",
		"WriteConfigMap", gc.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	cmName := gc.Spec.OutputConfigMapRef
	log.Info("Writing governance output ConfigMap",
		"configmap", cmName)

	data := map[string]string{
		"contractId":    gc.Spec.ContractId,
		"contractState": "Accepted",
		"ccfEndpoint":   nc.endpoint,
		"serviceCert":   nc.serviceCert,
	}

	cm := &corev1.ConfigMap{
		ObjectMeta: metav1.ObjectMeta{
			Name:      cmName,
			Namespace: gc.Namespace,
		},
		Data: data,
	}

	if err := controllerutil.SetControllerReference(
		gc, cm, r.Scheme,
	); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"setting owner reference on ConfigMap: %w",
			err,
		)
	}

	// Create or update the ConfigMap.
	var existing corev1.ConfigMap
	err := r.Get(ctx, types.NamespacedName{
		Name:      cmName,
		Namespace: gc.Namespace,
	}, &existing)

	if apierrors.IsNotFound(err) {
		if err := r.Create(ctx, cm); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"creating ConfigMap %s: %w", cmName, err,
			)
		}
	} else if err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"checking ConfigMap %s: %w", cmName, err,
		)
	} else {
		existing.Data = data
		if err := r.Update(ctx, &existing); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"updating ConfigMap %s: %w", cmName, err,
			)
		}
	}

	r.setConditionTrue(gc,
		v1alpha1.ConditionTypeConfigMapReady,
		"Written",
		"Output ConfigMap written")
	if err := r.Status().Update(ctx, gc); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after ConfigMap: %w", err,
		)
	}

	r.gcEvent(gc, corev1.EventTypeNormal,
		"ConfigMapReady",
		fmt.Sprintf("Output ConfigMap %s written", cmName))

	return ctrl.Result{Requeue: true}, nil
}

func (r *GovernanceContractReconciler) reconcileDelete(
	ctx context.Context,
	gc *v1alpha1.GovernanceContract,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	if !controllerutil.ContainsFinalizer(
		gc, gcFinalizerName,
	) {
		return ctrl.Result{}, nil
	}

	log.Info("Deleting GovernanceContract resources",
		"name", gc.Name)

	// Owned ConfigMap is garbage-collected via
	// ownerReferences.

	controllerutil.RemoveFinalizer(gc, gcFinalizerName)
	if err := r.Update(ctx, gc); err != nil {
		if apierrors.IsNotFound(err) {
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"removing finalizer: %w", err,
		)
	}

	log.Info("GovernanceContract deleted",
		"name", gc.Name)
	return ctrl.Result{}, nil
}

// conditionIsTrue checks if a condition is set to True.
func (r *GovernanceContractReconciler) conditionIsTrue(
	gc *v1alpha1.GovernanceContract,
	conditionType string,
) bool {
	return meta.IsStatusConditionTrue(
		gc.Status.Conditions, conditionType,
	)
}

// setConditionTrue sets a condition to True.
func (r *GovernanceContractReconciler) setConditionTrue(
	gc *v1alpha1.GovernanceContract,
	conditionType string,
	reason string,
	message string,
) {
	meta.SetStatusCondition(
		&gc.Status.Conditions,
		metav1.Condition{
			Type:    conditionType,
			Status:  metav1.ConditionTrue,
			Reason:  reason,
			Message: message,
		},
	)
}

func (r *GovernanceContractReconciler) setGCFailed(
	ctx context.Context,
	gc *v1alpha1.GovernanceContract,
	reason string,
	err error,
) error {
	log := ctrllog.FromContext(ctx)
	log.Error(err, "Operation failed: "+reason)
	recordContextError(ctx, err)

	message := err.Error()
	gc.Status.Phase =
		v1alpha1.GovernanceContractPhaseFailed
	gc.Status.TraceParent = ""
	meta.SetStatusCondition(
		&gc.Status.Conditions,
		metav1.Condition{
			Type:    v1alpha1.ConditionTypeReady,
			Status:  metav1.ConditionFalse,
			Reason:  reason,
			Message: message,
		},
	)
	if err := r.Status().Update(ctx, gc); err != nil {
		return fmt.Errorf(
			"updating failed status: %w", err,
		)
	}
	return nil
}

// gcEvent emits a Kubernetes event on the
// GovernanceContract resource.
func (r *GovernanceContractReconciler) gcEvent(
	gc *v1alpha1.GovernanceContract,
	eventType string,
	reason string,
	message string,
) {
	if r.Recorder != nil {
		r.Recorder.Event(
			gc, eventType, reason, message,
		)
	}
}

// resolveEnvUID walks the ownership chain from
// GovernanceContract → WorkloadGovernance → Environment
// to return the Environment UID.
func (r *GovernanceContractReconciler) resolveEnvUID(
	ctx context.Context,
	gc *v1alpha1.GovernanceContract,
) (types.UID, error) {
	// GC's controller owner is WorkloadGovernance.
	wgUID := controllerOwnerUID(gc)
	if wgUID == "" {
		return "", nil
	}

	// Find the WorkloadGovernance name from
	// ownerReferences.
	var wgName string
	for _, ref := range gc.GetOwnerReferences() {
		if ref.Controller != nil &&
			*ref.Controller {
			wgName = ref.Name
			break
		}
	}
	if wgName == "" {
		return "", nil
	}

	var wg v1alpha1.WorkloadGovernance
	if err := r.Get(ctx, types.NamespacedName{
		Name:      wgName,
		Namespace: gc.Namespace,
	}, &wg); err != nil {
		if apierrors.IsNotFound(err) {
			return "", nil
		}
		return "", fmt.Errorf(
			"fetching WorkloadGovernance %s: %w",
			wgName, err,
		)
	}

	// WG's controller owner is Environment.
	return controllerOwnerUID(&wg), nil
}

// isProposalAlreadyAccepted returns true when a vote
// fails because the proposal has already been accepted
// (e.g. the constitution's resolve function reached
// quorum before the explicit vote).
func isProposalAlreadyAccepted(err error) bool {
	msg := err.Error()
	return strings.Contains(msg, "ProposalNotOpen") &&
		strings.Contains(msg, "state accepted")
}

// SetupWithManager sets up the controller with the Manager.
func (r *GovernanceContractReconciler) SetupWithManager(
	mgr ctrl.Manager,
) error {
	return ctrl.NewControllerManagedBy(mgr).
		For(&v1alpha1.GovernanceContract{}).
		Owns(&corev1.ConfigMap{}).
		Complete(r)
}
