package controller

import (
	"context"
	"crypto/sha256"
	"crypto/tls"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
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
	"github.com/Azure/azure-cleanroom/cleanroom-operator/internal/azure"
)

const (
	mrRequeueDelay = 15 * time.Second
)

// ModelRegistrationReconciler reconciles ModelRegistration
// objects.
type ModelRegistrationReconciler struct {
	ctrlclient.Client
	Scheme      *runtime.Scheme
	CgsClient   *client.CgsClient
	AzureClient *azure.Client
	Recorder    record.EventRecorder
}

// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=modelregistrations,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=modelregistrations/status,verbs=get;update;patch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=modelregistrations/finalizers,verbs=update
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=modelgovernances,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=modelgovernances/status,verbs=get;update;patch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=modelgovernances/finalizers,verbs=update
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=environments,verbs=get;list;watch;update;patch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=governanceservices,verbs=get;list;watch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=ccfmembers,verbs=get;list;watch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=ccfnetworks,verbs=get;list;watch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=ccfusers,verbs=get;list;watch;create;update;patch
// +kubebuilder:rbac:groups="",resources=events,verbs=create;patch

// Reconcile handles the reconciliation loop for
// ModelRegistration.
func (r *ModelRegistrationReconciler) Reconcile(
	ctx context.Context,
	req ctrl.Request,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	var mr v1alpha1.ModelRegistration
	if err := r.Get(
		ctx, req.NamespacedName, &mr,
	); err != nil {
		if apierrors.IsNotFound(err) {
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"fetching ModelRegistration: %w", err,
		)
	}

	log.Info("Reconciling ModelRegistration",
		"name", mr.Name,
		"phase", mr.Status.Phase)

	ctx = resolveTraceContext(
		ctx, &mr, mr.Status.TraceParent,
	)
	ctx, span := startSpan(
		ctx, "ModelRegistration", "Reconcile",
		mr.Name,
	)
	defer span.End()

	// Ensure the owning Environment is set as an owner so
	// that deleting the Environment cascades to this
	// ModelRegistration.
	if mr.Spec.EnvironmentRef != "" {
		var env v1alpha1.Environment
		if err := r.Get(ctx, types.NamespacedName{
			Name:      mr.Spec.EnvironmentRef,
			Namespace: mr.Namespace,
		}, &env); err == nil {
			if !hasOwnerReference(&mr, &env) {
				if err := controllerutil.SetOwnerReference(
					&env, &mr, r.Scheme,
				); err != nil {
					return ctrl.Result{}, fmt.Errorf(
						"setting Environment owner on "+
							"ModelRegistration: %w", err,
					)
				}
				if err := r.Update(ctx, &mr); err != nil {
					return ctrl.Result{}, fmt.Errorf(
						"persisting Environment owner: "+
							"%w", err,
					)
				}
				log.Info(
					"Set Environment as owner",
					"environment", env.Name,
				)
			}
		}
	}

	// Route based on phase.
	switch mr.Status.Phase {
	case "", v1alpha1.ModelRegistrationPhasePending:
		return r.reconcileStart(ctx, &mr)

	case v1alpha1.ModelRegistrationPhaseConfiguring:
		return r.reconcileConfiguring(ctx, &mr)

	case v1alpha1.ModelRegistrationPhaseReady:
		if mr.Generation !=
			mr.Status.ObservedGeneration {
			return r.reconcileStart(ctx, &mr)
		}
		if mr.Annotations[reconcileRequestedAtAn] != "" &&
			mr.Annotations[reconcileRequestedAtAn] !=
				mr.Status.LastHandledReconcileAt {
			return r.mrPropagateRetry(ctx, &mr)
		}
		return ctrl.Result{}, nil

	case v1alpha1.ModelRegistrationPhaseFailed:
		if mr.Generation !=
			mr.Status.ObservedGeneration {
			mr.Status.Conditions = nil
			return r.reconcileStart(ctx, &mr)
		}
		if mr.Annotations[reconcileRequestedAtAn] != "" &&
			mr.Annotations[reconcileRequestedAtAn] !=
				mr.Status.LastHandledReconcileAt {
			return r.mrPropagateRetry(ctx, &mr)
		}
		if mr.Annotations[retryAnnotation] != "" {
			delete(mr.Annotations, retryAnnotation)
			if err := r.Update(ctx, &mr); err != nil {
				return ctrl.Result{}, fmt.Errorf(
					"clearing retry annotation: %w",
					err,
				)
			}
			mr.Status.Conditions = nil
			return r.reconcileStart(ctx, &mr)
		}
		return ctrl.Result{}, nil
	}

	return ctrl.Result{}, nil
}

// reconcileStart transitions to Configuring phase.
func (r *ModelRegistrationReconciler) reconcileStart(
	ctx context.Context,
	mr *v1alpha1.ModelRegistration,
) (ctrl.Result, error) {
	mr.Status.Phase =
		v1alpha1.ModelRegistrationPhaseConfiguring
	mr.Status.ObservedGeneration = mr.Generation
	mr.Status.TraceParent,
		mr.Status.LastOperationTraceId =
		saveTrace(ctx)
	if err := r.Status().Update(ctx, mr); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating phase to Configuring: %w", err,
		)
	}
	return ctrl.Result{Requeue: true}, nil
}

// reconcileConfiguring executes the condition-tracked
// step machine for model governance setup.
func (r *ModelRegistrationReconciler) reconcileConfiguring(
	ctx context.Context,
	mr *v1alpha1.ModelRegistration,
) (ctrl.Result, error) {
	// Step 1: Upload model (or verify it exists).
	if !r.mrConditionIsTrue(mr,
		v1alpha1.ConditionTypeMRModelUploaded) {
		return r.stepUploadModel(ctx, mr)
	}

	// Step 2: Ensure CcfUser is Active.
	if !r.mrConditionIsTrue(mr,
		v1alpha1.ConditionTypeMRCcfUserReady) {
		return r.stepEnsureCcfUser(ctx, mr)
	}

	// Step 3: Ensure OIDC issuer is ready (uploaded by
	// GovernanceService controller).
	if !r.mrConditionIsTrue(mr,
		v1alpha1.ConditionTypeMROidcIssuerReady) {
		return r.stepWaitOidcIssuer(ctx, mr)
	}

	// Step 4: Setup access (federated credential + RBAC).
	if !r.mrConditionIsTrue(mr,
		v1alpha1.ConditionTypeMRAccessConfigured) {
		return r.stepSetupAccess(ctx, mr)
	}

	// Resolve user's CGS endpoint for remaining steps.
	ep, err := r.resolveMRUserCgsEndpoint(ctx, mr)
	if err != nil {
		return ctrl.Result{
			RequeueAfter: mrRequeueDelay,
		}, nil
	}

	contractId, err := r.resolveContractId(ctx, mr)
	if err != nil {
		return ctrl.Result{
			RequeueAfter: mrRequeueDelay,
		}, nil
	}

	// Step 5: Ensure dataset document is accepted.
	if !r.mrConditionIsTrue(mr,
		v1alpha1.ConditionTypeMRDatasetDocReady) {
		return r.stepEnsureDatasetDoc(
			ctx, mr, ep, contractId,
		)
	}

	// Step 6: Ensure model governance doc is accepted.
	if !r.mrConditionIsTrue(mr,
		v1alpha1.ConditionTypeMRModelDocReady) {
		return r.stepEnsureModelDoc(
			ctx, mr, ep, contractId,
		)
	}

	// Auto-create ModelDeployment if requested.
	if mr.Spec.AutoDeploy {
		if err := r.ensureModelDeployment(
			ctx, mr,
		); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"ensuring ModelDeployment: %w",
				err,
			)
		}
	}

	// All steps complete — mark Ready.
	mr.Status.Phase = v1alpha1.ModelRegistrationPhaseReady
	mr.Status.TraceParent = ""
	if err := r.Status().Update(ctx, mr); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating phase to Ready: %w", err,
		)
	}

	r.mrEvent(mr, corev1.EventTypeNormal,
		"Ready", "ModelRegistration is ready")

	return ctrl.Result{}, nil
}

// stepUploadModel uploads the model to blob storage or
// verifies it already exists.
func (r *ModelRegistrationReconciler) stepUploadModel(
	ctx context.Context,
	mr *v1alpha1.ModelRegistration,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "ModelRegistration",
		"UploadModel", mr.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)

	mode := mr.Spec.Upload.Mode
	if mode == "" {
		mode = "auto"
	}

	var blobPath string
	if mode == "skip" {
		blobPath = mr.Spec.Model.Path
		log.Info("Upload mode is skip, using "+
			"spec.model.path",
			"blobPath", blobPath)
	} else {
		// Fetch model info from HuggingFace API.
		info, err := azure.FetchHFModelInfo(
			ctx, mr.Spec.Model.ID,
		)
		if err != nil {
			if sErr := r.mrSetFailed(ctx, mr,
				"HFModelFetchFailed", fmt.Errorf(
					"fetching HuggingFace model info "+
						"for %s: %w",
					mr.Spec.Model.ID, err,
				)); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{}, nil
		}

		// Plan which files to upload.
		plan, err := azure.PlanModelUpload(
			info, mr.Spec.Model.ID,
			mr.Spec.Model.SourceFile,
			mr.Spec.Model.BlobPrefix,
		)
		if err != nil {
			if sErr := r.mrSetFailed(ctx, mr,
				"ModelPlanFailed", fmt.Errorf(
					"planning model upload for %s: %w",
					mr.Spec.Model.ID, err,
				)); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{}, nil
		}

		r.mrEvent(mr, corev1.EventTypeNormal,
			"ModelUploadStarted",
			fmt.Sprintf(
				"Uploading %d files to %s",
				len(plan.Files), plan.BlobPrefix,
			))

		// Parse storage account from ARM ID.
		sub, _, saName, err := azure.ParseArmResource(
			mr.Spec.Storage.StorageAccountId,
		)
		if err != nil {
			if sErr := r.mrSetFailed(ctx, mr,
				"InvalidSAArmId", err); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{}, nil
		}

		containerName := mr.Spec.Storage.ContainerName
		if containerName == "" {
			containerName = "models"
		}

		// Self-ensure upload prerequisites so the flow works
		// regardless of who created this ModelRegistration —
		// the az CLI (which pre-provisions storage + RBAC) or
		// an in-cluster provider such as AIRunway (which
		// cannot run az). The operator uploads via its own
		// workload identity, so grant that identity blob-data
		// access on the storage account and ensure the
		// container exists. The grant is best-effort: on
		// management-cluster deployments the operator uses the
		// caller's credentials and already has access.
		if oid, oErr := r.AzureClient.GetOwnObjectID(
			ctx,
		); oErr != nil {
			log.Info("Skipping operator blob RBAC "+
				"self-grant: could not determine own "+
				"object ID", "error", oErr.Error())
		} else if aErr := r.AzureClient.AssignRole(
			ctx, sub,
			mr.Spec.Storage.StorageAccountId,
			azure.StorageBlobDataContributorRoleID,
			oid,
		); aErr != nil {
			log.Info("Operator blob RBAC self-grant "+
				"failed (continuing; may already have "+
				"access)", "error", aErr.Error())
		}

		if cErr := r.AzureClient.EnsureBlobContainer(
			ctx, saName, containerName,
		); cErr != nil {
			if azure.IsAuthorizationError(cErr) {
				log.Info("Waiting for blob access to "+
					"ensure container; requeueing",
					"error", cErr.Error())
				return ctrl.Result{
					RequeueAfter: 20 * time.Second,
				}, nil
			}
			if sErr := r.mrSetFailed(ctx, mr,
				"ContainerCreateFailed", fmt.Errorf(
					"ensuring container %s: %w",
					containerName, cErr,
				)); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{}, nil
		}

		// Execute the upload.
		if err := r.AzureClient.UploadModelFromHF(
			ctx, saName,
			containerName,
			mr.Spec.Model.ID, plan,
			func(
				file string,
				index int,
				total int,
				msg string,
			) {
				r.mrEvent(
					mr,
					corev1.EventTypeNormal,
					"ModelUploadProgress",
					msg,
				)
			},
		); err != nil {
			// Blob-data RBAC can take up to ~2 minutes to
			// propagate after the self-grant; requeue on an
			// authorization error rather than failing.
			if azure.IsAuthorizationError(err) {
				log.Info("Blob authorization not yet "+
					"propagated; requeueing",
					"error", err.Error())
				return ctrl.Result{
					RequeueAfter: 20 * time.Second,
				}, nil
			}
			if sErr := r.mrSetFailed(ctx, mr,
				"ModelUploadFailed", fmt.Errorf(
					"uploading model %s: %w",
					mr.Spec.Model.ID, err,
				)); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{}, nil
		}

		blobPath = plan.BlobPath
		log.Info("Model upload completed",
			"modelId", mr.Spec.Model.ID,
			"blobPath", blobPath)
	}

	mr.Status.ModelBlobPath = blobPath
	r.mrSetConditionTrue(mr,
		v1alpha1.ConditionTypeMRModelUploaded,
		"Uploaded",
		"Model is available in blob storage")
	if err := r.Status().Update(ctx, mr); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after upload: %w", err,
		)
	}

	r.mrEvent(mr, corev1.EventTypeNormal,
		"ModelUploaded",
		"Model is available in blob storage")

	return ctrl.Result{Requeue: true}, nil
}

// stepEnsureCcfUser creates a CcfUser CR for the
// publisher and waits for it to reach Active phase.
func (r *ModelRegistrationReconciler) stepEnsureCcfUser(
	ctx context.Context,
	mr *v1alpha1.ModelRegistration,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "ModelRegistration",
		"EnsureCcfUser", mr.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	userName := mr.Name + "-publisher"
	memberName := mr.Spec.EnvironmentRef + "-member0"

	// Look up the member to get NetworkRef.
	var member v1alpha1.CcfMember
	if err := r.Get(ctx, types.NamespacedName{
		Name:      memberName,
		Namespace: mr.Namespace,
	}, &member); err != nil {
		if sErr := r.mrSetFailed(ctx, mr,
			"MemberNotFound", fmt.Errorf(
				"fetching CcfMember %s: %w",
				memberName, err,
			)); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{}, nil
	}

	// Check if CcfUser already exists.
	var user v1alpha1.CcfUser
	err := r.Get(ctx, types.NamespacedName{
		Name:      userName,
		Namespace: mr.Namespace,
	}, &user)

	if apierrors.IsNotFound(err) {
		log.Info("Creating CcfUser for publisher",
			"name", userName,
			"member", memberName)

		user = v1alpha1.CcfUser{
			ObjectMeta: metav1.ObjectMeta{
				Name:      userName,
				Namespace: mr.Namespace,
			},
			Spec: v1alpha1.CcfUserSpec{
				Identifier: mr.Name,
				MemberRef:  memberName,
				NetworkRef: member.Spec.NetworkRef,
			},
		}

		if err := controllerutil.SetControllerReference(
			mr, &user, r.Scheme,
		); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"setting owner reference on CcfUser: %w",
				err,
			)
		}

		if err := r.Create(ctx, &user); err != nil {
			if apierrors.IsAlreadyExists(err) {
				return ctrl.Result{Requeue: true}, nil
			}
			if sErr := r.mrSetFailed(ctx, mr,
				"CcfUserCreateFailed", err); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{
				RequeueAfter: mrRequeueDelay,
			}, nil
		}

		r.mrEvent(mr, corev1.EventTypeNormal,
			"CcfUserCreated",
			fmt.Sprintf("CcfUser %s created", userName))

		return ctrl.Result{
			RequeueAfter: mrRequeueDelay,
		}, nil
	}
	if err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"fetching CcfUser %s: %w", userName, err,
		)
	}

	// Check if CcfUser has reached Active.
	if user.Status.Phase ==
		v1alpha1.CcfUserPhaseFailed {
		msg := failedConditionMessage(
			user.Status.Conditions)
		if msg == "" {
			msg = "CcfUser " + userName + " failed"
		}
		if sErr := r.mrSetFailed(ctx, mr,
			"CcfUserFailed", fmt.Errorf(
				"CcfUser %s failed: %s",
				userName, msg,
			)); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{}, nil
	}

	if user.Status.Phase !=
		v1alpha1.CcfUserPhaseActive {
		log.Info("Waiting for CcfUser to become Active",
			"name", userName,
			"phase", user.Status.Phase)
		return ctrl.Result{
			RequeueAfter: mrRequeueDelay,
		}, nil
	}

	// Store the user's CGS identity (JWT oid) in
	// annotation — this is the ID that CGS uses to
	// identify the caller for approvals.
	if mr.Annotations == nil {
		mr.Annotations = make(map[string]string)
	}
	mr.Annotations["cleanroom.azure.com/publisher-user-id"] =
		user.Status.UserId
	if err := r.Update(ctx, mr); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"storing publisher user annotation: %w",
			err,
		)
	}

	r.mrSetConditionTrue(mr,
		v1alpha1.ConditionTypeMRCcfUserReady,
		"Active",
		fmt.Sprintf("CcfUser %s is active", userName))
	if err := r.Status().Update(ctx, mr); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after CcfUser: %w", err,
		)
	}

	r.mrEvent(mr, corev1.EventTypeNormal,
		"CcfUserReady",
		fmt.Sprintf("CcfUser %s is active", userName))

	return ctrl.Result{Requeue: true}, nil
}

// stepWaitOidcIssuer waits for the GovernanceService to
// have uploaded OIDC documents and copies the issuer URL
// into ModelRegistration status.
func (r *ModelRegistrationReconciler) stepWaitOidcIssuer(
	ctx context.Context,
	mr *v1alpha1.ModelRegistration,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "ModelRegistration",
		"WaitOidcIssuer", mr.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)

	// Look up the GovernanceService for this
	// environment (named <env>-gs by convention).
	gsName := mr.Spec.EnvironmentRef + "-gs"
	var gs v1alpha1.GovernanceService
	if err := r.Get(ctx, types.NamespacedName{
		Name:      gsName,
		Namespace: mr.Namespace,
	}, &gs); err != nil {
		log.Info(
			"GovernanceService not found, requeuing",
			"name", gsName,
			"error", err.Error())
		return ctrl.Result{
			RequeueAfter: mrRequeueDelay,
		}, nil
	}

	if gs.Status.OidcIssuerUrl == "" {
		log.Info(
			"GovernanceService OIDC issuer not "+
				"ready, requeuing",
			"name", gsName)
		return ctrl.Result{
			RequeueAfter: mrRequeueDelay,
		}, nil
	}

	issuerURL := gs.Status.OidcIssuerUrl
	mr.Status.OidcIssuerUrl = issuerURL
	r.mrSetConditionTrue(mr,
		v1alpha1.ConditionTypeMROidcIssuerReady,
		"Configured",
		fmt.Sprintf(
			"OIDC issuer configured: %s", issuerURL,
		))
	if err := r.Status().Update(ctx, mr); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after OIDC issuer: %w",
			err,
		)
	}

	r.mrEvent(mr, corev1.EventTypeNormal,
		"OidcIssuerReady",
		fmt.Sprintf(
			"OIDC issuer configured: %s", issuerURL,
		))

	return ctrl.Result{Requeue: true}, nil
}

// stepSetupAccess creates a federated identity
// credential on the managed identity and assigns the
// Storage Blob Data Contributor role, using Azure SDK
// with workload identity credentials.
func (r *ModelRegistrationReconciler) stepSetupAccess(
	ctx context.Context,
	mr *v1alpha1.ModelRegistration,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "ModelRegistration",
		"SetupAccess", mr.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)

	// Parse MI ARM ID → subscription, RG, name.
	miSubID, miRG, miName, err :=
		azure.ParseArmResource(
			mr.Spec.Storage.ManagedIdentityId,
		)
	if err != nil {
		if sErr := r.mrSetFailed(ctx, mr,
			"InvalidMIArmId", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{}, nil
	}

	// 1. Get the MI details.
	miInfo, err :=
		r.AzureClient.GetManagedIdentityPrincipalID(
			ctx, miSubID, miRG, miName,
		)
	if err != nil {
		if sErr := r.mrSetFailed(ctx, mr,
			"MIPrincipalLookupFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{}, nil
	}

	log.Info("Assigning Storage Blob Data Contributor",
		"principalId", miInfo.PrincipalID,
		"scope", mr.Spec.Storage.StorageAccountId)

	// 2. Assign Storage Blob Data Contributor RBAC.
	saSubID, _, _, saErr :=
		azure.ParseArmResource(
			mr.Spec.Storage.StorageAccountId,
		)
	if saErr != nil {
		if sErr := r.mrSetFailed(ctx, mr,
			"InvalidSAArmId", saErr); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{}, nil
	}

	roleDefID := mr.Spec.Storage.StorageAccountId +
		azure.StorageBlobDataContributorRoleID
	if err := r.AzureClient.AssignRole(
		ctx, saSubID,
		mr.Spec.Storage.StorageAccountId,
		roleDefID,
		miInfo.PrincipalID,
	); err != nil {
		if sErr := r.mrSetFailed(ctx, mr,
			"RBACAssignFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{}, nil
	}

	// 3. Create federated credential.
	contractId, err := r.resolveContractId(ctx, mr)
	if err != nil {
		return ctrl.Result{
			RequeueAfter: mrRequeueDelay,
		}, nil
	}

	// Look up the CcfUser to get the publisher's UserId.
	// The subject must be contractId + "-" + userId to match
	// what the identity sidecar presents via CGS OIDC tokens.
	publisherUserName := mr.Name + "-publisher"
	var publisherUser v1alpha1.CcfUser
	if err := r.Get(ctx, types.NamespacedName{
		Name:      publisherUserName,
		Namespace: mr.Namespace,
	}, &publisherUser); err != nil {
		if sErr := r.mrSetFailed(ctx, mr,
			"PublisherUserLookupFailed", fmt.Errorf(
				"fetching CcfUser %s: %w",
				publisherUserName, err,
			)); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{}, nil
	}

	subject := contractId + "-" + publisherUser.Status.UserId
	fedCredName := subject + "-federation"
	issuerURL := mr.Status.OidcIssuerUrl

	log.Info("Creating federated credential",
		"fedCredName", fedCredName,
		"issuer", issuerURL,
		"subject", subject)

	if err := r.AzureClient.CreateFederatedCredential(
		ctx, miSubID, miRG, miName,
		fedCredName, issuerURL, subject,
	); err != nil {
		if sErr := r.mrSetFailed(ctx, mr,
			"FedCredCreateFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{}, nil
	}

	r.mrSetConditionTrue(mr,
		v1alpha1.ConditionTypeMRAccessConfigured,
		"Configured",
		"Federated credential and RBAC configured")
	if err := r.Status().Update(ctx, mr); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after access setup: %w",
			err,
		)
	}

	r.mrEvent(mr, corev1.EventTypeNormal,
		"AccessConfigured",
		"Federated credential and RBAC configured")

	return ctrl.Result{Requeue: true}, nil
}

// buildDatasetData builds the dataset specification
// JSON string for a given dataset document ID.
func (r *ModelRegistrationReconciler) buildDatasetData(
	ctx context.Context,
	mr *v1alpha1.ModelRegistration,
	datasetName string,
) (string, error) {
	configJSON, _ := json.Marshal(map[string]string{
		"KeyType":        "KEK",
		"EncryptionMode": mr.Spec.Storage.EncryptionMode,
	})
	configB64 := base64.StdEncoding.EncodeToString(
		configJSON,
	)

	// Resolve MI clientId and tenantId.
	miSubID, miRG, miName, err :=
		azure.ParseArmResource(
			mr.Spec.Storage.ManagedIdentityId,
		)
	if err != nil {
		return "", fmt.Errorf(
			"parsing MI ARM ID: %w", err,
		)
	}
	miInfo, err :=
		r.AzureClient.GetManagedIdentityPrincipalID(
			ctx, miSubID, miRG, miName,
		)
	if err != nil {
		return "", fmt.Errorf(
			"getting MI details: %w", err,
		)
	}

	// Resolve storage blob endpoint URL.
	storeBlobEndpoint :=
		azure.GetBlobEndpointFromArmID(
			mr.Spec.Storage.StorageAccountId,
		)

	identityName := datasetName + "-identity"
	oidcIssuerUrl := mr.Status.OidcIssuerUrl

	// Build the federated identity chain that matches
	// the reference document structure.
	identityObj := map[string]interface{}{
		"name":     identityName,
		"clientId": miInfo.ClientID,
		"tenantId": miInfo.TenantID,
		"tokenIssuer": map[string]interface{}{
			"issuer": map[string]interface{}{
				"protocol":      "AzureAD_Federated",
				"url":           oidcIssuerUrl,
				"configuration": "",
			},
			"federatedIdentity": map[string]interface{}{
				"name":     "cleanroom_cgs_oidc",
				"clientId": "",
				"tenantId": "",
				"tokenIssuer": map[string]interface{}{
					"issuer": map[string]interface{}{
						"protocol":      "Attested_OIDC",
						"url":           "https://cgs/oidc",
						"configuration": "",
					},
					"issuerType": "AttestationBasedTokenIssuer",
				},
			},
			"issuerType": "FederatedIdentityBasedTokenIssuer",
		},
	}

	datasetSpec := map[string]interface{}{
		"name": datasetName,
		"datasetAccessPoint": map[string]interface{}{
			"name": datasetName,
			"type": "Volume_ReadOnly",
			"path": "",
			"store": map[string]interface{}{
				"name": mr.Spec.Storage.ContainerName,
				"type": "Azure_BlobStorage",
				"id":   datasetName,
				"provider": map[string]interface{}{
					"protocol":      "Azure_BlobStorage",
					"url":           storeBlobEndpoint,
					"configuration": "",
				},
			},
			"identity": identityObj,
			"protection": map[string]interface{}{
				"proxyType": "SecureVolume__ReadOnly" +
					"__Azure__BlobStorage",
				"proxyMode":                      "Secure",
				"configuration":                  configB64,
				"encryptionSecretAccessIdentity": identityObj,
			},
		},
		"datasetAccessPolicy": map[string]interface{}{
			"accessMode":    "read",
			"allowedFields": []string{},
		},
		"datasetSchema": map[string]interface{}{
			"format": "parquet",
			"fields": []interface{}{},
		},
	}

	specData, err := json.Marshal(datasetSpec)
	if err != nil {
		return "", fmt.Errorf(
			"marshaling dataset spec: %w", err,
		)
	}
	return string(specData), nil
}

// contentHash returns the first 8 hex chars of the
// SHA-256 digest of data.
func contentHash(data string) string {
	h := sha256.Sum256([]byte(data))
	return hex.EncodeToString(h[:4])
}

// resolveDocId checks if a candidate document ID can be
// used. If the document exists in CGS with matching
// content it returns the existing doc and the candidate
// ID. If the content differs (stale doc), it derives a
// new ID using a content hash and checks that. If no
// document exists it returns nil and the candidate ID.
func (r *ModelRegistrationReconciler) resolveDocId(
	ctx context.Context,
	endpoint string,
	baseId string,
	statusDocId string,
	desiredData string,
) (string, *client.UserDocumentResponse, error) {
	candidateId := baseId
	if statusDocId != "" {
		candidateId = statusDocId
	}

	doc, err := r.CgsClient.GetUserDocument(
		ctx, endpoint, candidateId,
	)
	if errors.Is(err, client.ErrDocumentNotFound) {
		return candidateId, nil, nil
	}
	if err != nil {
		return "", nil, fmt.Errorf(
			"checking document %s: %w",
			candidateId, err,
		)
	}

	if doc.Data == desiredData {
		return candidateId, doc, nil
	}

	// Content differs — derive a new ID from content
	// hash so the same desired content always produces
	// the same ID (idempotent across retries).
	newId := baseId + "-" + contentHash(desiredData)
	if newId == candidateId {
		// Already using the hash-suffixed name — should
		// not happen but guard against it.
		return "", nil, fmt.Errorf(
			"document %s exists with different "+
				"content and hash collision",
			newId,
		)
	}

	doc2, err := r.CgsClient.GetUserDocument(
		ctx, endpoint, newId,
	)
	if errors.Is(err, client.ErrDocumentNotFound) {
		return newId, nil, nil
	}
	if err != nil {
		return "", nil, fmt.Errorf(
			"checking document %s: %w", newId, err,
		)
	}

	if doc2.Data == desiredData {
		return newId, doc2, nil
	}
	return "", nil, fmt.Errorf(
		"document %s exists with different content "+
			"(hash collision)",
		newId,
	)
}

// stepEnsureDatasetDoc creates, proposes, and votes the
// dataset user document in a single reconcile step.
// Progress is reported via Kubernetes Events. On resume,
// resolveDocId detects the existing state and skips
// already-completed sub-stages.
func (r *ModelRegistrationReconciler) stepEnsureDatasetDoc(
	ctx context.Context,
	mr *v1alpha1.ModelRegistration,
	endpoint string,
	contractId string,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "ModelRegistration",
		"EnsureDatasetDoc", mr.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	baseId := mr.Name + "-dataset"
	publisherUserId :=
		mr.Annotations["cleanroom.azure.com/publisher-user-id"]

	desiredData, err := r.buildDatasetData(
		ctx, mr, baseId,
	)
	if err != nil {
		return ctrl.Result{}, err
	}

	docId, existing, err := r.resolveDocId(
		ctx, endpoint, baseId,
		mr.Status.DatasetDocId, desiredData,
	)
	if err != nil {
		if sErr := r.mrSetFailed(ctx, mr,
			"DatasetDocResolveFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{}, nil
	}

	log.Info("Resolved dataset document ID",
		"docId", docId,
		"existing", existing != nil)

	state := ""
	if existing != nil {
		mr.Status.DatasetDocId = docId
		if existing.ProposalID != "" {
			mr.Status.DatasetProposalId =
				existing.ProposalID
		}
		state = existing.State
	}

	// Create if no document exists yet.
	if state == "" {
		approvers := []map[string]string{
			{
				"approverId":     publisherUserId,
				"approverIdType": "user",
			},
		}

		err = r.CgsClient.CreateUserDocument(
			ctx, endpoint, docId, contractId,
			desiredData,
			map[string]string{"type": "dataset"},
			approvers,
		)
		if err != nil {
			if sErr := r.mrSetFailed(ctx, mr,
				"DatasetDocCreateFailed",
				err); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{
				RequeueAfter: mrRequeueDelay,
			}, nil
		}

		mr.Status.DatasetDocId = docId
		r.mrEvent(mr, corev1.EventTypeNormal,
			"DatasetDocCreated",
			fmt.Sprintf(
				"Dataset document %s created", docId))
		state = "Draft"
	}

	// Propose if still in Draft.
	if state == "Draft" {
		resp, err := r.CgsClient.ProposeUserDocument(
			ctx, endpoint, docId,
		)
		if err != nil {
			if sErr := r.mrSetFailed(ctx, mr,
				"DatasetDocProposeFailed",
				err); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{
				RequeueAfter: mrRequeueDelay,
			}, nil
		}

		mr.Status.DatasetProposalId = resp.ProposalID
		r.mrEvent(mr, corev1.EventTypeNormal,
			"DatasetDocProposed",
			fmt.Sprintf(
				"Dataset document %s proposed", docId))
		state = "Proposed"
	}

	// Vote if Proposed.
	if state == "Proposed" {
		err := r.CgsClient.VoteUserDocument(
			ctx, endpoint, docId,
			mr.Status.DatasetProposalId,
		)
		if err != nil {
			if sErr := r.mrSetFailed(ctx, mr,
				"DatasetDocVoteFailed",
				err); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{
				RequeueAfter: mrRequeueDelay,
			}, nil
		}

		r.mrEvent(mr, corev1.EventTypeNormal,
			"DatasetDocAccepted",
			fmt.Sprintf(
				"Dataset document %s accepted", docId))
	}

	r.mrSetConditionTrue(mr,
		v1alpha1.ConditionTypeMRDatasetDocReady,
		"Accepted",
		"Dataset document accepted")
	if err := r.Status().Update(ctx, mr); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after dataset doc: %w",
			err,
		)
	}

	return ctrl.Result{Requeue: true}, nil
}

// buildModelDocData builds the model governance document
// JSON string.
func (r *ModelRegistrationReconciler) buildModelDocData(
	mr *v1alpha1.ModelRegistration,
	docId string,
	datasetDocId string,
) (string, error) {
	modelDir := datasetDocId
	if mr.Status.ModelBlobPath != "" {
		modelDir = datasetDocId + "/" +
			mr.Status.ModelBlobPath
	}

	modelDoc := map[string]interface{}{
		"name": docId,
		"application": map[string]interface{}{
			"applicationType": "KServe-Inferencing",
			"modelDir":        modelDir,
			"modelDatasets": []map[string]interface{}{
				{"specification": datasetDocId},
			},
		},
	}

	docData, err := json.Marshal(modelDoc)
	if err != nil {
		return "", fmt.Errorf(
			"marshaling model document: %w", err,
		)
	}
	return string(docData), nil
}

// stepEnsureModelDoc creates, proposes, and votes the
// model governance document in a single reconcile step.
// Progress is reported via Kubernetes Events. On resume,
// resolveDocId detects the existing state and skips
// already-completed sub-stages.
func (r *ModelRegistrationReconciler) stepEnsureModelDoc(
	ctx context.Context,
	mr *v1alpha1.ModelRegistration,
	endpoint string,
	contractId string,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "ModelRegistration",
		"EnsureModelDoc", mr.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	baseId := mr.Name + "-model-doc"
	datasetDocId := mr.Status.DatasetDocId
	publisherUserId :=
		mr.Annotations["cleanroom.azure.com/publisher-user-id"]

	desiredData, err := r.buildModelDocData(
		mr, baseId, datasetDocId,
	)
	if err != nil {
		return ctrl.Result{}, err
	}

	docId, existing, err := r.resolveDocId(
		ctx, endpoint, baseId,
		mr.Status.ModelDocId, desiredData,
	)
	if err != nil {
		if sErr := r.mrSetFailed(ctx, mr,
			"ModelDocResolveFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{}, nil
	}

	log.Info("Resolved model document ID",
		"docId", docId,
		"existing", existing != nil)

	state := ""
	if existing != nil {
		mr.Status.ModelDocId = docId
		if existing.ProposalID != "" {
			mr.Status.ModelDocProposalId =
				existing.ProposalID
		}
		state = existing.State
	}

	// Create if no document exists yet.
	if state == "" {
		approvers := []map[string]string{
			{
				"approverId":     publisherUserId,
				"approverIdType": "user",
			},
		}

		err = r.CgsClient.CreateUserDocument(
			ctx, endpoint, docId, contractId,
			desiredData, nil, approvers,
		)
		if err != nil {
			if sErr := r.mrSetFailed(ctx, mr,
				"ModelDocCreateFailed",
				err); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{
				RequeueAfter: mrRequeueDelay,
			}, nil
		}

		mr.Status.ModelDocId = docId
		r.mrEvent(mr, corev1.EventTypeNormal,
			"ModelDocCreated",
			fmt.Sprintf(
				"Model document %s created", docId))
		state = "Draft"
	}

	// Propose if still in Draft.
	if state == "Draft" {
		resp, err := r.CgsClient.ProposeUserDocument(
			ctx, endpoint, docId,
		)
		if err != nil {
			if sErr := r.mrSetFailed(ctx, mr,
				"ModelDocProposeFailed",
				err); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{
				RequeueAfter: mrRequeueDelay,
			}, nil
		}

		mr.Status.ModelDocProposalId = resp.ProposalID
		r.mrEvent(mr, corev1.EventTypeNormal,
			"ModelDocProposed",
			fmt.Sprintf(
				"Model document %s proposed", docId))
		state = "Proposed"
	}

	// Vote if Proposed.
	if state == "Proposed" {
		err := r.CgsClient.VoteUserDocument(
			ctx, endpoint, docId,
			mr.Status.ModelDocProposalId,
		)
		if err != nil {
			if sErr := r.mrSetFailed(ctx, mr,
				"ModelDocVoteFailed",
				err); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{
				RequeueAfter: mrRequeueDelay,
			}, nil
		}

		r.mrEvent(mr, corev1.EventTypeNormal,
			"ModelDocAccepted",
			fmt.Sprintf(
				"Model document %s accepted", docId))
	}

	r.mrSetConditionTrue(mr,
		v1alpha1.ConditionTypeMRModelDocReady,
		"Accepted",
		"Model governance document accepted")
	if err := r.Status().Update(ctx, mr); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after model doc: %w",
			err,
		)
	}

	return ctrl.Result{Requeue: true}, nil
}

// resolveMRUserCgsEndpoint looks up the CcfUser's
// governance client endpoint for user-level CGS
// operations.
func (r *ModelRegistrationReconciler) resolveMRUserCgsEndpoint(
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

// resolveMRCgsEndpoint looks up the CcfMember via the
// Environment and returns the CGS client endpoint.
func (r *ModelRegistrationReconciler) resolveMRCgsEndpoint(
	ctx context.Context,
	mr *v1alpha1.ModelRegistration,
) (string, error) {
	// The Environment's operator member follows naming
	// convention: <envName>-member0
	memberName := mr.Spec.EnvironmentRef + "-member0"

	var member v1alpha1.CcfMember
	if err := r.Get(ctx, types.NamespacedName{
		Name:      memberName,
		Namespace: mr.Namespace,
	}, &member); err != nil {
		return "", fmt.Errorf(
			"fetching CcfMember %s: %w",
			memberName, err,
		)
	}

	if member.Status.GovernanceClientEndpoint == "" {
		return "", fmt.Errorf(
			"CcfMember %s has no governance client "+
				"endpoint",
			memberName,
		)
	}

	return member.Status.GovernanceClientEndpoint, nil
}

// resolveContractId derives the contract ID from the
// Environment.
func (r *ModelRegistrationReconciler) resolveContractId(
	ctx context.Context,
	mr *v1alpha1.ModelRegistration,
) (string, error) {
	var env v1alpha1.Environment
	if err := r.Get(ctx, types.NamespacedName{
		Name:      mr.Spec.EnvironmentRef,
		Namespace: mr.Namespace,
	}, &env); err != nil {
		return "", fmt.Errorf(
			"fetching Environment %s: %w",
			mr.Spec.EnvironmentRef, err,
		)
	}

	contractId := env.Name + "-inferencing"
	if env.Spec.ContractId != "" {
		contractId = env.Spec.ContractId + "-inferencing"
	}
	return contractId, nil
}

// mrConditionIsTrue checks if a condition is True.
func (r *ModelRegistrationReconciler) mrConditionIsTrue(
	mr *v1alpha1.ModelRegistration,
	conditionType string,
) bool {
	return meta.IsStatusConditionTrue(
		mr.Status.Conditions, conditionType,
	)
}

// mrSetConditionTrue sets a condition to True.
func (r *ModelRegistrationReconciler) mrSetConditionTrue(
	mr *v1alpha1.ModelRegistration,
	conditionType string,
	reason string,
	message string,
) {
	meta.SetStatusCondition(
		&mr.Status.Conditions,
		metav1.Condition{
			Type:    conditionType,
			Status:  metav1.ConditionTrue,
			Reason:  reason,
			Message: message,
		},
	)
}

// mrSetFailed sets the phase to Failed with a condition.
func (r *ModelRegistrationReconciler) mrSetFailed(
	ctx context.Context,
	mr *v1alpha1.ModelRegistration,
	reason string,
	err error,
) error {
	log := ctrllog.FromContext(ctx)
	log.Error(err, "Operation failed: "+reason)
	recordContextError(ctx, err)

	message := truncateMessage(
		err.Error(), maxConditionMessageLen,
	)
	mr.Status.Phase =
		v1alpha1.ModelRegistrationPhaseFailed
	mr.Status.Message = message
	mr.Status.TraceParent = ""
	meta.SetStatusCondition(
		&mr.Status.Conditions,
		metav1.Condition{
			Type:    "Ready",
			Status:  metav1.ConditionFalse,
			Reason:  reason,
			Message: message,
		},
	)
	if err := r.Status().Update(ctx, mr); err != nil {
		return fmt.Errorf(
			"updating failed status: %w", err,
		)
	}
	return nil
}

// mrEvent emits a Kubernetes event for the
// ModelRegistration resource.
func (r *ModelRegistrationReconciler) mrEvent(
	mr *v1alpha1.ModelRegistration,
	eventType string,
	reason string,
	message string,
) {
	r.Recorder.Event(mr, eventType, reason, message)
}

// ensureModelDeployment creates a
// ModelDeployment child CR if it does not
// already exist. Sets ownerRef for cascading delete.
func (r *ModelRegistrationReconciler) ensureModelDeployment(
	ctx context.Context,
	mr *v1alpha1.ModelRegistration,
) error {
	instanceName := mr.Name + "-instance"

	// Check if already created.
	var existing v1alpha1.ModelDeployment
	err := r.Get(ctx, types.NamespacedName{
		Name:      instanceName,
		Namespace: mr.Namespace,
	}, &existing)
	if err == nil {
		// Already exists.
		mr.Status.InstanceRef = instanceName
		return nil
	}
	if !apierrors.IsNotFound(err) {
		return fmt.Errorf(
			"checking ModelDeployment: %w",
			err,
		)
	}

	mdi := &v1alpha1.ModelDeployment{
		ObjectMeta: metav1.ObjectMeta{
			Name:      instanceName,
			Namespace: mr.Namespace,
		},
		Spec: v1alpha1.ModelDeploymentSpec{
			ModelRegistrationRef: mr.Name,
		},
	}

	if err := controllerutil.SetControllerReference(
		mr, mdi, r.Scheme,
	); err != nil {
		return fmt.Errorf(
			"setting owner reference: %w", err,
		)
	}

	if err := r.Create(ctx, mdi); err != nil {
		if apierrors.IsAlreadyExists(err) {
			mr.Status.InstanceRef = instanceName
			return nil
		}
		return fmt.Errorf(
			"creating ModelDeployment: %w",
			err,
		)
	}

	mr.Status.InstanceRef = instanceName

	r.mrEvent(mr, corev1.EventTypeNormal,
		"InstanceCreated",
		fmt.Sprintf(
			"ModelDeployment %q created",
			instanceName,
		),
	)

	return nil
}

// SetupWithManager registers the controller with the
// manager.
func (r *ModelRegistrationReconciler) SetupWithManager(
	mgr ctrl.Manager,
) error {
	return ctrl.NewControllerManagedBy(mgr).
		For(&v1alpha1.ModelRegistration{}).
		Owns(&v1alpha1.ModelDeployment{}).
		Complete(r)
}

// resolveCcfNetworkEndpoint looks up the CcfMember →
// CcfNetwork chain to find the CCF network endpoint URL.
func (r *ModelRegistrationReconciler) resolveCcfNetworkEndpoint(
	ctx context.Context,
	mr *v1alpha1.ModelRegistration,
) (string, error) {
	memberName := mr.Spec.EnvironmentRef + "-member0"

	var member v1alpha1.CcfMember
	if err := r.Get(ctx, types.NamespacedName{
		Name:      memberName,
		Namespace: mr.Namespace,
	}, &member); err != nil {
		return "", fmt.Errorf(
			"fetching CcfMember %s: %w",
			memberName, err,
		)
	}

	var network v1alpha1.CcfNetwork
	if err := r.Get(ctx, types.NamespacedName{
		Name:      member.Spec.NetworkRef,
		Namespace: mr.Namespace,
	}, &network); err != nil {
		return "", fmt.Errorf(
			"fetching CcfNetwork %s: %w",
			member.Spec.NetworkRef, err,
		)
	}

	if network.Status.Endpoint == "" {
		return "", fmt.Errorf(
			"CcfNetwork %s has no endpoint",
			member.Spec.NetworkRef,
		)
	}

	return network.Status.Endpoint, nil
}

// fetchJWKSFromCCF fetches the JWKS document from the
// CCF endpoint's /app/oidc/keys path. Uses TLS skip
// because CCF uses a self-signed certificate.
func fetchJWKSFromCCF(
	ctx context.Context,
	ccfEndpoint string,
) ([]byte, error) {
	url := ccfEndpoint + "/app/oidc/keys"

	httpClient := &http.Client{
		Timeout: 30 * time.Second,
		Transport: &http.Transport{
			TLSClientConfig: &tls.Config{
				MinVersion:         tls.VersionTLS12,
				InsecureSkipVerify: true, //nolint:gosec // CCF self-signed cert
			},
		},
	}

	req, err := http.NewRequestWithContext(
		ctx, http.MethodGet, url, nil,
	)
	if err != nil {
		return nil, fmt.Errorf(
			"creating JWKS request: %w", err,
		)
	}

	resp, err := httpClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf(
			"GET %s: %w", url, err,
		)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf(
			"GET %s returned %d", url,
			resp.StatusCode,
		)
	}

	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, fmt.Errorf(
			"reading JWKS response: %w", err,
		)
	}
	return body, nil
}

// mrPropagateRetry handles the reconcile annotation by
// setting retry on the failed CcfUser child and resetting
// the ModelRegistration phase to Configuring.
func (r *ModelRegistrationReconciler) mrPropagateRetry(
	ctx context.Context,
	mr *v1alpha1.ModelRegistration,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "ModelRegistration", "Retry", mr.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	log.Info("Propagating retry to failed children")

	requestedAt :=
		mr.Annotations[reconcileRequestedAtAn]

	// Retry the CcfUser child if it exists and is Failed.
	userName := mr.Name + "-publisher"
	var user v1alpha1.CcfUser
	if err := r.Get(ctx, types.NamespacedName{
		Name:      userName,
		Namespace: mr.Namespace,
	}, &user); err == nil {
		if user.Status.Phase ==
			v1alpha1.CcfUserPhaseFailed {
			annotations := user.GetAnnotations()
			if annotations == nil {
				annotations = map[string]string{}
			}
			annotations[retryAnnotation] = "true"
			user.SetAnnotations(annotations)
			injectTraceAnnotation(ctx, &user)
			if err := r.Update(
				ctx, &user,
			); err != nil {
				log.Error(err,
					"Failed to set retry on CcfUser",
					"name", userName)
			}
		}
	}

	// Retry the ModelDeployment child if it
	// exists and is Failed.
	if mr.Status.InstanceRef != "" {
		var md v1alpha1.ModelDeployment
		if err := r.Get(ctx, types.NamespacedName{
			Name:      mr.Status.InstanceRef,
			Namespace: mr.Namespace,
		}, &md); err == nil {
			if md.Status.Phase ==
				v1alpha1.ModelDeploymentPhaseFailed {
				annotations := md.GetAnnotations()
				if annotations == nil {
					annotations = map[string]string{}
				}
				annotations[retryAnnotation] = "true"
				md.SetAnnotations(annotations)
				injectTraceAnnotation(ctx, &md)
				if err := r.Update(
					ctx, &md,
				); err != nil {
					log.Error(err,
						"Failed to set retry on "+
							"ModelDeployment",
						"name",
						mr.Status.InstanceRef)
				}
			}
		}
	}

	// Clear reconcile annotation from ModelRegistration.
	delete(mr.Annotations, reconcileRequestedAtAn)
	if err := r.Update(ctx, mr); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"clearing reconcile annotation: %w", err,
		)
	}

	// Reset to Configuring and store acknowledgment.
	// Clear proposal IDs (may be stale) but keep doc IDs
	// so the content-based idempotency can resume.
	mr.Status.Phase =
		v1alpha1.ModelRegistrationPhaseConfiguring
	mr.Status.Conditions = nil
	mr.Status.DatasetProposalId = ""
	mr.Status.ModelDocProposalId = ""
	mr.Status.LastHandledReconcileAt = requestedAt
	mr.Status.TraceParent,
		mr.Status.LastOperationTraceId =
		saveTrace(ctx)
	if err := r.Status().Update(ctx, mr); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"resetting phase to Configuring: %w", err,
		)
	}

	r.mrEvent(mr, corev1.EventTypeNormal,
		"ReconcileRequested",
		"Reconciliation requested, retrying")

	return ctrl.Result{Requeue: true}, nil
}
