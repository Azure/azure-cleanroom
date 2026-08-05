package controller

import (
	"context"
	"encoding/json"
	"fmt"
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
	"sigs.k8s.io/controller-runtime/pkg/handler"
	ctrllog "sigs.k8s.io/controller-runtime/pkg/log"

	v1alpha1 "github.com/Azure/azure-cleanroom/cleanroom-operator/api/v1alpha1"
	"github.com/Azure/azure-cleanroom/cleanroom-operator/client"
)

const (
	wgRequeueDelay = 15 * time.Second
)

// WorkloadGovernanceReconciler reconciles
// WorkloadGovernance objects.
type WorkloadGovernanceReconciler struct {
	ctrlclient.Client
	Scheme        *runtime.Scheme
	CgsClient     *client.CgsClient
	ClusterClient *client.ClusterClient
	Recorder      record.EventRecorder
}

// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=workloadgovernances,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=workloadgovernances/status,verbs=get;update;patch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=workloadgovernances/finalizers,verbs=update
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=governancecontracts,verbs=get;list;watch;create;update;patch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=ccfnetworks,verbs=get;list;watch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=ccfmembers,verbs=get;list;watch
// +kubebuilder:rbac:groups="",resources=configmaps,verbs=get;list;watch;create;update;patch
// +kubebuilder:rbac:groups="",resources=secrets,verbs=get;list;watch
// +kubebuilder:rbac:groups="",resources=events,verbs=create;patch

// Reconcile handles the reconciliation loop for
// WorkloadGovernance.
func (r *WorkloadGovernanceReconciler) Reconcile(
	ctx context.Context,
	req ctrl.Request,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	var wg v1alpha1.WorkloadGovernance
	if err := r.Get(
		ctx, req.NamespacedName, &wg,
	); err != nil {
		if apierrors.IsNotFound(err) {
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"fetching WorkloadGovernance: %w", err,
		)
	}

	log.Info("Reconciling WorkloadGovernance",
		"name", wg.Name,
		"phase", wg.Status.Phase)

	ctx = resolveTraceContext(
		ctx, &wg, wg.Status.TraceParent,
	)
	ctx, span := startSpan(
		ctx, "WorkloadGovernance", "Reconcile",
		wg.Name,
	)
	defer span.End()

	// Route based on phase.
	switch wg.Status.Phase {
	case "", v1alpha1.WorkloadGovernancePhasePending:
		return r.reconcileStart(ctx, &wg)

	case v1alpha1.WorkloadGovernancePhaseWaitingForContract:
		return r.reconcileWaitingForContract(ctx, &wg)

	case v1alpha1.WorkloadGovernancePhaseConfiguring:
		return r.reconcileConfiguring(ctx, &wg)

	case v1alpha1.WorkloadGovernancePhaseReady:
		return ctrl.Result{}, nil

	case v1alpha1.WorkloadGovernancePhaseFailed:
		// Retry if spec changed or retry annotation set.
		if wg.Generation !=
			wg.Status.ObservedGeneration ||
			wg.Annotations[retryAnnotation] != "" {
			if wg.Annotations[retryAnnotation] != "" {
				delete(wg.Annotations, retryAnnotation)
			}
			if err := r.Update(ctx, &wg); err != nil {
				return ctrl.Result{}, fmt.Errorf(
					"clearing retry annotation: %w",
					err,
				)
			}
			// Set transitional phase immediately so
			// the parent controller does not see a
			// stale Failed phase after the retry
			// annotation has been cleared.
			wg.Status.Phase =
				v1alpha1.WorkloadGovernancePhasePending
			wg.Status.Conditions = nil
			if err := r.Status().Update(
				ctx, &wg,
			); err != nil {
				return ctrl.Result{}, fmt.Errorf(
					"setting transitional phase: %w",
					err,
				)
			}
			return r.reconcileStart(ctx, &wg)
		}
		return ctrl.Result{}, nil
	}

	return ctrl.Result{}, nil
}

// reconcileStart transitions to WaitingForContract phase
// and ensures the child GovernanceContract exists.
func (r *WorkloadGovernanceReconciler) reconcileStart(
	ctx context.Context,
	wg *v1alpha1.WorkloadGovernance,
) (ctrl.Result, error) {
	wg.Status.Phase =
		v1alpha1.WorkloadGovernancePhaseWaitingForContract
	wg.Status.ObservedGeneration = wg.Generation
	wg.Status.TraceParent,
		wg.Status.LastOperationTraceId =
		saveTrace(ctx)
	if err := r.Status().Update(ctx, wg); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating phase to WaitingForContract: %w",
			err,
		)
	}
	return ctrl.Result{Requeue: true}, nil
}

// reconcileWaitingForContract ensures the child
// GovernanceContract exists and waits for it to become
// Ready before transitioning to Configuring.
func (r *WorkloadGovernanceReconciler) reconcileWaitingForContract(
	ctx context.Context,
	wg *v1alpha1.WorkloadGovernance,
) (ctrl.Result, error) {
	// Step 1: Ensure GovernanceContract child exists.
	if !r.conditionIsTrue(wg,
		v1alpha1.ConditionTypeWGContractCreated) {
		return r.stepEnsureContract(ctx, wg)
	}

	// Step 2: Wait for GovernanceContract to be Ready.
	if !r.conditionIsTrue(wg,
		v1alpha1.ConditionTypeWGContractReady) {
		return r.stepWaitForContract(ctx, wg)
	}

	// Contract is ready — transition to Configuring.
	wg.Status.Phase =
		v1alpha1.WorkloadGovernancePhaseConfiguring
	if err := r.Status().Update(ctx, wg); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating phase to Configuring: %w", err,
		)
	}
	return ctrl.Result{Requeue: true}, nil
}

// reconcileConfiguring executes the condition-tracked step
// machine for workload governance setup.
func (r *WorkloadGovernanceReconciler) reconcileConfiguring(
	ctx context.Context,
	wg *v1alpha1.WorkloadGovernance,
) (ctrl.Result, error) {
	// Resolve CGS endpoint for remaining steps.
	ep, err := r.resolveCgsEndpoint(ctx, wg)
	if err != nil {
		return ctrl.Result{
			RequeueAfter: wgRequeueDelay,
		}, nil
	}

	// Step 3: Enable signing.
	if !r.conditionIsTrue(wg,
		v1alpha1.ConditionTypeSigningEnabled) {
		return r.stepEnableSigning(ctx, wg, ep)
	}

	// Step 4: Generate signing key.
	if !r.conditionIsTrue(wg,
		v1alpha1.ConditionTypeSigningKeyGen) {
		return r.stepGenerateSigningKey(ctx, wg, ep)
	}

	// Step 5: Generate deployment via cluster provider.
	if !r.conditionIsTrue(wg,
		v1alpha1.ConditionTypeDeploymentGen) {
		return r.stepGenerateDeployment(ctx, wg, ep)
	}

	// Step 6: Propose deployment spec.
	if !r.conditionIsTrue(wg,
		v1alpha1.ConditionTypeWGDeploySpecOK) {
		return r.stepProposeDeploymentSpec(ctx, wg, ep)
	}

	// Step 7: Propose clean room policy.
	if !r.conditionIsTrue(wg,
		v1alpha1.ConditionTypeWGPolicyOK) {
		return r.stepProposeCleanRoomPolicy(ctx, wg, ep)
	}

	// Step 8: Update ConfigMap with configurationUrl.
	if !r.conditionIsTrue(wg,
		v1alpha1.ConditionTypeWGConfigMapReady) {
		return r.stepUpdateConfigMap(ctx, wg, ep)
	}

	// All steps complete — mark Ready.
	wg.Status.Phase = v1alpha1.WorkloadGovernancePhaseReady
	wg.Status.TraceParent = ""
	if err := r.Status().Update(ctx, wg); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating phase to Ready: %w", err,
		)
	}

	r.wgEvent(wg, corev1.EventTypeNormal,
		"Ready", "WorkloadGovernance is ready")

	return ctrl.Result{}, nil
}

// stepEnsureContract creates the child GovernanceContract
// if it doesn't exist.
func (r *WorkloadGovernanceReconciler) stepEnsureContract(
	ctx context.Context,
	wg *v1alpha1.WorkloadGovernance,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "WorkloadGovernance",
		"EnsureContract", wg.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	gcName := wg.Name + "-gc"

	var existing v1alpha1.GovernanceContract
	err := r.Get(ctx, types.NamespacedName{
		Name:      gcName,
		Namespace: wg.Namespace,
	}, &existing)
	if err == nil {
		// Already exists.
		r.setConditionTrue(wg,
			v1alpha1.ConditionTypeWGContractCreated,
			"Created",
			"GovernanceContract exists")
		if err := r.Status().Update(ctx, wg); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"updating status: %w", err,
			)
		}
		return ctrl.Result{Requeue: true}, nil
	}
	if !apierrors.IsNotFound(err) {
		return ctrl.Result{}, fmt.Errorf(
			"checking GovernanceContract: %w", err,
		)
	}

	log.Info("Creating GovernanceContract",
		"name", gcName)

	gc := &v1alpha1.GovernanceContract{
		ObjectMeta: metav1.ObjectMeta{
			Name:      gcName,
			Namespace: wg.Namespace,
		},
		Spec: v1alpha1.GovernanceContractSpec{
			ContractId:           wg.Spec.ContractId,
			NetworkRef:           wg.Spec.NetworkRef,
			MemberRef:            wg.Spec.MemberRef,
			AutoApprove:          wg.Spec.AutoApprove,
			EnableCA:             wg.Spec.EnableCA,
			RuntimeOptions:       wg.Spec.RuntimeOptions,
			OutputConfigMapRef:   wg.Spec.OutputConfigMapRef,
			GovernanceServiceRef: wg.Spec.GovernanceServiceRef,
		},
	}

	if err := controllerutil.SetControllerReference(
		wg, gc, r.Scheme,
	); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"setting owner reference: %w", err,
		)
	}

	injectTraceAnnotation(ctx, gc)
	if err := r.Create(ctx, gc); err != nil {
		if apierrors.IsAlreadyExists(err) {
			return ctrl.Result{Requeue: true}, nil
		}
		if apierrors.IsInvalid(err) ||
			apierrors.IsForbidden(err) {
			if sErr := r.setFailed(ctx, wg,
				"ContractCreateRejected",
				fmt.Errorf(
					"creating GovernanceContract: %w",
					err,
				),
			); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"creating GovernanceContract: %w", err,
		)
	}

	r.setConditionTrue(wg,
		v1alpha1.ConditionTypeWGContractCreated,
		"Created",
		"GovernanceContract created")
	if err := r.Status().Update(ctx, wg); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status: %w", err,
		)
	}

	r.wgEvent(wg, corev1.EventTypeNormal,
		"GovernanceContractCreated",
		fmt.Sprintf("GovernanceContract %s created", gcName))

	return ctrl.Result{Requeue: true}, nil
}

// stepWaitForContract waits for the child
// GovernanceContract to reach Ready phase.
func (r *WorkloadGovernanceReconciler) stepWaitForContract(
	ctx context.Context,
	wg *v1alpha1.WorkloadGovernance,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)
	gcName := wg.Name + "-gc"

	var gc v1alpha1.GovernanceContract
	if err := r.Get(ctx, types.NamespacedName{
		Name:      gcName,
		Namespace: wg.Namespace,
	}, &gc); err != nil {
		log.Error(err,
			"Failed to get GovernanceContract",
			"name", gcName)
		return ctrl.Result{
			RequeueAfter: wgRequeueDelay,
		}, nil
	}

	if gc.Status.Phase ==
		v1alpha1.GovernanceContractPhaseFailed {
		// Extract failure detail from the contract's
		// Ready condition.
		msg := "GovernanceContract failed"
		if readyCond := meta.FindStatusCondition(
			gc.Status.Conditions,
			v1alpha1.ConditionTypeReady,
		); readyCond != nil && readyCond.Message != "" {
			msg = fmt.Sprintf(
				"GovernanceContract failed: %s",
				readyCond.Message,
			)
		}
		if sErr := r.setFailed(ctx, wg,
			"GovernanceContractFailed",
			fmt.Errorf("%s", msg)); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{}, nil
	}

	if gc.Status.Phase !=
		v1alpha1.GovernanceContractPhaseReady {
		log.Info("Waiting for GovernanceContract Ready",
			"name", gcName,
			"phase", gc.Status.Phase)

		// Set a descriptive condition so users can see
		// what this resource is blocked on.
		reason := "WaitingForContract"
		msg := fmt.Sprintf(
			"GovernanceContract %q phase: %s",
			gcName, gc.Status.Phase,
		)
		// If the contract has a Ready condition with
		// a message, propagate it.
		if readyCond := meta.FindStatusCondition(
			gc.Status.Conditions,
			v1alpha1.ConditionTypeReady,
		); readyCond != nil &&
			readyCond.Status == metav1.ConditionFalse &&
			readyCond.Message != "" {
			msg = fmt.Sprintf(
				"GovernanceContract %q: %s",
				gcName, readyCond.Message,
			)
		}
		meta.SetStatusCondition(
			&wg.Status.Conditions,
			metav1.Condition{
				Type:    v1alpha1.ConditionTypeWGContractReady,
				Status:  metav1.ConditionFalse,
				Reason:  reason,
				Message: msg,
			},
		)
		_ = r.Status().Update(ctx, wg)

		return ctrl.Result{
			RequeueAfter: wgRequeueDelay,
		}, nil
	}

	r.setConditionTrue(wg,
		v1alpha1.ConditionTypeWGContractReady,
		"Ready",
		"GovernanceContract is ready")
	if err := r.Status().Update(ctx, wg); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status: %w", err,
		)
	}

	r.wgEvent(wg, corev1.EventTypeNormal,
		"GovernanceContractReady",
		"GovernanceContract is ready")

	return ctrl.Result{Requeue: true}, nil
}

// stepEnableSigning proposes enable_signing and votes
// accept.
func (r *WorkloadGovernanceReconciler) stepEnableSigning(
	ctx context.Context,
	wg *v1alpha1.WorkloadGovernance,
	endpoint string,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "WorkloadGovernance",
		"EnableSigning", wg.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	log.Info("Proposing enable signing",
		"contractId", wg.Spec.ContractId)

	propResp, err := r.CgsClient.ProposeEnableSigning(
		ctx, endpoint, wg.Spec.ContractId,
	)
	if err != nil {
		if sErr := r.setFailed(ctx, wg,
			"SigningProposeFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: wgRequeueDelay,
		}, nil
	}

	if wg.Spec.AutoApprove {
		if err := r.CgsClient.VoteAcceptProposal(
			ctx, endpoint, propResp.ProposalID,
		); err != nil {
			if sErr := r.setFailed(ctx, wg,
				"SigningVoteFailed", err); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{
				RequeueAfter: wgRequeueDelay,
			}, nil
		}
	}

	r.setConditionTrue(wg,
		v1alpha1.ConditionTypeSigningEnabled,
		"Enabled",
		"Signing enabled")
	if err := r.Status().Update(ctx, wg); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after signing: %w", err,
		)
	}

	r.wgEvent(wg, corev1.EventTypeNormal,
		"SigningEnabled", "Signing enabled")

	return ctrl.Result{Requeue: true}, nil
}

// stepGenerateSigningKey generates the signing key.
func (r *WorkloadGovernanceReconciler) stepGenerateSigningKey(
	ctx context.Context,
	wg *v1alpha1.WorkloadGovernance,
	endpoint string,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "WorkloadGovernance",
		"GenerateSigningKey", wg.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	log.Info("Generating signing key")

	if err := r.CgsClient.GenerateSigningKey(
		ctx, endpoint,
	); err != nil {
		if sErr := r.setFailed(ctx, wg,
			"SigningKeyGenFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: wgRequeueDelay,
		}, nil
	}

	r.setConditionTrue(wg,
		v1alpha1.ConditionTypeSigningKeyGen,
		"Generated",
		"Signing key generated")
	if err := r.Status().Update(ctx, wg); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after signing key gen: %w",
			err,
		)
	}

	r.wgEvent(wg, corev1.EventTypeNormal,
		"SigningKeyGenerated", "Signing key generated")

	return ctrl.Result{Requeue: true}, nil
}

// stepGenerateDeployment calls the cluster provider to
// generate the deployment template and governance policy.
// Results are stored in annotations for subsequent steps.
func (r *WorkloadGovernanceReconciler) stepGenerateDeployment(
	ctx context.Context,
	wg *v1alpha1.WorkloadGovernance,
	cgsEndpoint string,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "WorkloadGovernance",
		"GenerateDeployment", wg.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	log.Info("Generating deployment")

	// Read the ConfigMap for contract URL context.
	cmName := wg.Spec.OutputConfigMapRef
	var cm corev1.ConfigMap
	if err := r.Get(ctx, types.NamespacedName{
		Name:      cmName,
		Namespace: wg.Namespace,
	}, &cm); err != nil {
		log.Error(err,
			"Failed to get output ConfigMap",
			"name", cmName)
		return ctrl.Result{
			RequeueAfter: wgRequeueDelay,
		}, nil
	}

	contractUrl := fmt.Sprintf(
		"%s/contracts/%s",
		cgsEndpoint, wg.Spec.ContractId,
	)

	input := &client.GenerateDeploymentInput{
		InfraType:   wg.Spec.InfraType,
		ContractUrl: contractUrl,
	}
	if wg.Spec.RuntimeOptions.EnableTelemetry {
		input.TelemetryProfile = true
	}
	if wg.Spec.SecurityPolicyCreationOption != "" {
		input.SecurityPolicy = &client.SecurityPolicyInput{
			PolicyCreationOption: wg.Spec.SecurityPolicyCreationOption,
		}
	}
	if wg.Spec.ProviderConfig != nil {
		input.ProviderConfig = wg.Spec.ProviderConfig.Raw
	}

	resp, err :=
		r.ClusterClient.GenerateKServeInferencingDeployment(
			ctx, input,
		)
	if err != nil {
		if sErr := r.setFailed(ctx, wg,
			"DeploymentGenFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: wgRequeueDelay,
		}, nil
	}

	// Store the generated deployment in annotations for
	// use by subsequent propose steps.
	if wg.Annotations == nil {
		wg.Annotations = make(map[string]string)
	}
	wg.Annotations["cleanroom.azure.com/deployment-template"] =
		string(resp.DeploymentTemplate)
	wg.Annotations["cleanroom.azure.com/governance-policy"] =
		string(resp.GovernancePolicy)
	if err := r.Update(ctx, wg); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"storing deployment annotations: %w", err,
		)
	}

	r.setConditionTrue(wg,
		v1alpha1.ConditionTypeDeploymentGen,
		"Generated",
		"Deployment generated")
	if err := r.Status().Update(ctx, wg); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after deployment gen: %w",
			err,
		)
	}

	r.wgEvent(wg, corev1.EventTypeNormal,
		"DeploymentGenerated", "Deployment generated")

	return ctrl.Result{Requeue: true}, nil
}

// stepProposeDeploymentSpec proposes the generated
// deployment template.
func (r *WorkloadGovernanceReconciler) stepProposeDeploymentSpec(
	ctx context.Context,
	wg *v1alpha1.WorkloadGovernance,
	endpoint string,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "WorkloadGovernance",
		"ProposeDeploymentSpec", wg.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	log.Info("Proposing deployment spec")

	template := wg.Annotations["cleanroom.azure.com/deployment-template"]
	if template == "" {
		if sErr := r.setFailed(ctx, wg,
			"MissingDeploymentTemplate",
			fmt.Errorf(
				"deployment template annotation "+
					"missing")); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{}, nil
	}

	propResp, err := r.CgsClient.ProposeDeploymentSpec(
		ctx, endpoint, wg.Spec.ContractId,
		json.RawMessage(template),
	)
	if err != nil {
		if sErr := r.setFailed(ctx, wg,
			"DeploySpecProposeFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: wgRequeueDelay,
		}, nil
	}

	if wg.Spec.AutoApprove {
		if err := r.CgsClient.VoteAcceptProposal(
			ctx, endpoint, propResp.ProposalID,
		); err != nil {
			if sErr := r.setFailed(ctx, wg,
				"DeploySpecVoteFailed", err); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{
				RequeueAfter: wgRequeueDelay,
			}, nil
		}
	}

	r.setConditionTrue(wg,
		v1alpha1.ConditionTypeWGDeploySpecOK,
		"Accepted",
		"Deployment spec accepted")
	if err := r.Status().Update(ctx, wg); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after deploy spec: %w", err,
		)
	}

	r.wgEvent(wg, corev1.EventTypeNormal,
		"DeploymentSpecAccepted",
		"Deployment spec proposed and accepted")

	return ctrl.Result{Requeue: true}, nil
}

// stepProposeCleanRoomPolicy proposes the generated
// governance policy.
func (r *WorkloadGovernanceReconciler) stepProposeCleanRoomPolicy(
	ctx context.Context,
	wg *v1alpha1.WorkloadGovernance,
	endpoint string,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "WorkloadGovernance",
		"ProposeCleanRoomPolicy", wg.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	log.Info("Proposing clean room policy")

	policy := wg.Annotations["cleanroom.azure.com/governance-policy"]
	if policy == "" {
		if sErr := r.setFailed(ctx, wg,
			"MissingGovernancePolicy",
			fmt.Errorf(
				"governance policy annotation "+
					"missing")); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{}, nil
	}

	propResp, err := r.CgsClient.ProposeCleanRoomPolicy(
		ctx, endpoint, wg.Spec.ContractId,
		json.RawMessage(policy),
	)
	if err != nil {
		if sErr := r.setFailed(ctx, wg,
			"PolicyProposeFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: wgRequeueDelay,
		}, nil
	}

	if wg.Spec.AutoApprove {
		if err := r.CgsClient.VoteAcceptProposal(
			ctx, endpoint, propResp.ProposalID,
		); err != nil {
			if sErr := r.setFailed(ctx, wg,
				"PolicyVoteFailed", err); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{
				RequeueAfter: wgRequeueDelay,
			}, nil
		}
	}

	r.setConditionTrue(wg,
		v1alpha1.ConditionTypeWGPolicyOK,
		"Accepted",
		"Clean room policy accepted")
	if err := r.Status().Update(ctx, wg); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after policy: %w", err,
		)
	}

	r.wgEvent(wg, corev1.EventTypeNormal,
		"CleanRoomPolicyAccepted",
		"Clean room policy proposed and accepted")

	return ctrl.Result{Requeue: true}, nil
}

// stepUpdateConfigMap adds configurationUrl to the output
// ConfigMap so the Cluster controller can use it.
func (r *WorkloadGovernanceReconciler) stepUpdateConfigMap(
	ctx context.Context,
	wg *v1alpha1.WorkloadGovernance,
	cgsEndpoint string,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "WorkloadGovernance",
		"UpdateConfigMap", wg.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	cmName := wg.Spec.OutputConfigMapRef
	log.Info("Updating ConfigMap with configurationUrl",
		"configmap", cmName)

	var cm corev1.ConfigMap
	if err := r.Get(ctx, types.NamespacedName{
		Name:      cmName,
		Namespace: wg.Namespace,
	}, &cm); err != nil {
		log.Error(err,
			"Failed to get output ConfigMap",
			"name", cmName)
		return ctrl.Result{
			RequeueAfter: wgRequeueDelay,
		}, nil
	}

	if cm.Data == nil {
		cm.Data = make(map[string]string)
	}
	cm.Data["configurationUrl"] = fmt.Sprintf(
		"%s/contracts/%s/deploymentspec",
		cgsEndpoint, wg.Spec.ContractId,
	)

	// Also write the signing public key so the Cluster
	// controller can use it for flex node policy signing.
	signingInfo, err := r.CgsClient.GetSigningInfo(
		ctx, cgsEndpoint,
	)
	if err != nil {
		log.Error(err,
			"Failed to get signing info")
		return ctrl.Result{
			RequeueAfter: wgRequeueDelay,
		}, nil
	}
	if signingInfo.PublicKeyPem != "" {
		cm.Data["policySigningCertPem"] =
			signingInfo.PublicKeyPem
	}

	if err := r.Update(ctx, &cm); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating ConfigMap %s: %w", cmName, err,
		)
	}

	r.setConditionTrue(wg,
		v1alpha1.ConditionTypeWGConfigMapReady,
		"Updated",
		"ConfigMap updated with configurationUrl")
	if err := r.Status().Update(ctx, wg); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after ConfigMap: %w", err,
		)
	}

	r.wgEvent(wg, corev1.EventTypeNormal,
		"ConfigMapUpdated",
		"ConfigMap updated with configurationUrl")

	return ctrl.Result{Requeue: true}, nil
}

// resolveCgsEndpoint looks up the operator CcfMember and
// returns its CGS client endpoint.
func (r *WorkloadGovernanceReconciler) resolveCgsEndpoint(
	ctx context.Context,
	wg *v1alpha1.WorkloadGovernance,
) (string, error) {
	var member v1alpha1.CcfMember
	if err := r.Get(ctx, types.NamespacedName{
		Name:      wg.Spec.MemberRef,
		Namespace: wg.Namespace,
	}, &member); err != nil {
		return "", fmt.Errorf(
			"fetching CcfMember %s: %w",
			wg.Spec.MemberRef, err,
		)
	}

	if member.Status.GovernanceClientEndpoint == "" {
		return "", fmt.Errorf(
			"CcfMember %s has no governance client endpoint",
			wg.Spec.MemberRef,
		)
	}

	return member.Status.GovernanceClientEndpoint, nil
}

// conditionIsTrue checks if a condition is True.
func (r *WorkloadGovernanceReconciler) conditionIsTrue(
	wg *v1alpha1.WorkloadGovernance,
	conditionType string,
) bool {
	return meta.IsStatusConditionTrue(
		wg.Status.Conditions, conditionType,
	)
}

// setConditionTrue sets a condition to True.
func (r *WorkloadGovernanceReconciler) setConditionTrue(
	wg *v1alpha1.WorkloadGovernance,
	conditionType string,
	reason string,
	message string,
) {
	meta.SetStatusCondition(
		&wg.Status.Conditions,
		metav1.Condition{
			Type:    conditionType,
			Status:  metav1.ConditionTrue,
			Reason:  reason,
			Message: message,
		},
	)
}

// setFailed sets the phase to Failed with a condition.
func (r *WorkloadGovernanceReconciler) setFailed(
	ctx context.Context,
	wg *v1alpha1.WorkloadGovernance,
	reason string,
	err error,
) error {
	log := ctrllog.FromContext(ctx)
	log.Error(err, "Operation failed: "+reason)
	recordContextError(ctx, err)

	message := truncateMessage(
		err.Error(), maxConditionMessageLen,
	)
	wg.Status.Phase = v1alpha1.WorkloadGovernancePhaseFailed
	wg.Status.TraceParent = ""
	meta.SetStatusCondition(
		&wg.Status.Conditions,
		metav1.Condition{
			Type:    "Ready",
			Status:  metav1.ConditionFalse,
			Reason:  reason,
			Message: message,
		},
	)
	if err := r.Status().Update(ctx, wg); err != nil {
		return fmt.Errorf(
			"updating failed status: %w", err,
		)
	}
	return nil
}

// wgEvent emits a Kubernetes event for the
// WorkloadGovernance resource.
func (r *WorkloadGovernanceReconciler) wgEvent(
	wg *v1alpha1.WorkloadGovernance,
	eventType string,
	reason string,
	message string,
) {
	r.Recorder.Event(wg, eventType, reason, message)
}

// SetupWithManager registers the controller with the
// manager.
func (r *WorkloadGovernanceReconciler) SetupWithManager(
	mgr ctrl.Manager,
) error {
	return ctrl.NewControllerManagedBy(mgr).
		For(&v1alpha1.WorkloadGovernance{}).
		Owns(&v1alpha1.GovernanceContract{}).
		Watches(
			&v1alpha1.GovernanceContract{},
			handler.EnqueueRequestForOwner(
				mgr.GetScheme(),
				mgr.GetRESTMapper(),
				&v1alpha1.WorkloadGovernance{},
			),
		).
		Complete(r)
}
