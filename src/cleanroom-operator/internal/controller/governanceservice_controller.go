package controller

import (
	"context"
	_ "embed"
	"encoding/json"
	"fmt"
	"os"
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
	"github.com/Azure/azure-cleanroom/cleanroom-operator/internal/oci"
)

//go:embed data/trusted_root_cas.pem
var trustedRootCACerts string

const (
	gsFinalizerName = "cleanroom.azure.com/gs-finalizer"
	gsRequeueDelay  = 15 * time.Second
)

// GovernanceServiceReconciler reconciles GovernanceService
// objects.
type GovernanceServiceReconciler struct {
	ctrlclient.Client
	Scheme      *runtime.Scheme
	CgsClient   *client.CgsClient
	AzureClient *azure.Client
	Recorder    record.EventRecorder
}

// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=governanceservices,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=governanceservices/status,verbs=get;update;patch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=governanceservices/finalizers,verbs=update
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=ccfnetworks,verbs=get;list;watch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=ccfmembers,verbs=get;list;watch
// +kubebuilder:rbac:groups="",resources=secrets,verbs=get;list;watch
// +kubebuilder:rbac:groups="",resources=events,verbs=create;patch

// Reconcile handles the reconciliation loop for
// GovernanceService.
func (r *GovernanceServiceReconciler) Reconcile(
	ctx context.Context,
	req ctrl.Request,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	var gs v1alpha1.GovernanceService
	if err := r.Get(
		ctx, req.NamespacedName, &gs,
	); err != nil {
		if apierrors.IsNotFound(err) {
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"fetching GovernanceService: %w", err,
		)
	}

	ctx = resolveTraceContext(
		ctx, &gs, gs.Status.TraceParent,
	)
	ctx, span := startSpan(
		ctx, "GovernanceService", "Reconcile",
		gs.Name,
	)
	defer span.End()

	// Handle deletion.
	if !gs.DeletionTimestamp.IsZero() {
		return r.reconcileDelete(ctx, &gs)
	}

	// Ensure finalizer.
	if !controllerutil.ContainsFinalizer(
		&gs, gsFinalizerName,
	) {
		controllerutil.AddFinalizer(&gs, gsFinalizerName)
		if err := r.Update(ctx, &gs); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"adding finalizer: %w", err,
			)
		}
		return ctrl.Result{Requeue: true}, nil
	}

	// Route based on phase.
	switch gs.Status.Phase {
	case "", v1alpha1.GovernanceServicePhasePending,
		v1alpha1.GovernanceServicePhaseWaiting:
		return r.reconcileWaitForDeps(ctx, &gs)

	case v1alpha1.GovernanceServicePhaseDeploying:
		return r.reconcileDeploying(ctx, &gs)

	case v1alpha1.GovernanceServicePhaseReady:
		return ctrl.Result{}, nil

	case v1alpha1.GovernanceServicePhaseFailed:
		if gs.Generation !=
			gs.Status.ObservedGeneration ||
			gs.Annotations[retryAnnotation] != "" {
			if gs.Annotations[retryAnnotation] != "" {
				delete(gs.Annotations, retryAnnotation)
			}
			if err := r.Update(ctx, &gs); err != nil {
				return ctrl.Result{}, fmt.Errorf(
					"clearing retry annotation: %w",
					err,
				)
			}
			// Set transitional phase immediately so
			// the parent controller does not see a
			// stale Failed phase after the retry
			// annotation has been cleared.
			gs.Status.Phase =
				v1alpha1.GovernanceServicePhaseWaiting
			gs.Status.Conditions = nil
			if err := r.Status().Update(
				ctx, &gs,
			); err != nil {
				return ctrl.Result{}, fmt.Errorf(
					"setting transitional phase: %w",
					err,
				)
			}
			return r.reconcileWaitForDeps(ctx, &gs)
		}
		return ctrl.Result{}, nil

	default:
		log.Info("Unknown phase, requeueing",
			"phase", gs.Status.Phase)
		return ctrl.Result{
			RequeueAfter: gsRequeueDelay,
		}, nil
	}
}

// reconcileWaitForDeps waits for CcfNetwork=Open and
// member0 CcfMember=Active.
func (r *GovernanceServiceReconciler) reconcileWaitForDeps(
	ctx context.Context,
	gs *v1alpha1.GovernanceService,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	if gs.Status.Phase !=
		v1alpha1.GovernanceServicePhaseWaiting {
		gs.Status.Phase =
			v1alpha1.GovernanceServicePhaseWaiting
		gs.Status.ObservedGeneration = gs.Generation
		if err := r.Status().Update(ctx, gs); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"setting WaitingForDeps phase: %w", err,
			)
		}
		return ctrl.Result{Requeue: true}, nil
	}

	// Check CcfNetwork is Open.
	var network v1alpha1.CcfNetwork
	if err := r.Get(ctx, types.NamespacedName{
		Name:      gs.Spec.NetworkRef,
		Namespace: gs.Namespace,
	}, &network); err != nil {
		if apierrors.IsNotFound(err) {
			log.Info("CcfNetwork not found, waiting",
				"network", gs.Spec.NetworkRef)
			return ctrl.Result{
				RequeueAfter: gsRequeueDelay,
			}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"fetching CcfNetwork: %w", err,
		)
	}

	if isStaleResource(
		ctx, controllerOwnerUID(gs),
		&network, "CcfNetwork",
		gs.Spec.NetworkRef,
	) || !network.DeletionTimestamp.IsZero() {
		return ctrl.Result{
			RequeueAfter: gsRequeueDelay,
		}, nil
	}

	if network.Status.Phase !=
		v1alpha1.CcfNetworkPhaseOpen {
		log.Info("CcfNetwork not Open yet, waiting",
			"network", gs.Spec.NetworkRef,
			"phase", network.Status.Phase)
		return ctrl.Result{
			RequeueAfter: gsRequeueDelay,
		}, nil
	}

	// Check member0 is Active.
	var member v1alpha1.CcfMember
	if err := r.Get(ctx, types.NamespacedName{
		Name:      gs.Spec.MemberRef,
		Namespace: gs.Namespace,
	}, &member); err != nil {
		if apierrors.IsNotFound(err) {
			log.Info("CcfMember not found, waiting",
				"member", gs.Spec.MemberRef)
			return ctrl.Result{
				RequeueAfter: gsRequeueDelay,
			}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"fetching CcfMember: %w", err,
		)
	}

	if isStaleResource(
		ctx, controllerOwnerUID(gs),
		&member, "CcfMember",
		gs.Spec.MemberRef,
	) || !member.DeletionTimestamp.IsZero() {
		return ctrl.Result{
			RequeueAfter: gsRequeueDelay,
		}, nil
	}

	if member.Status.Phase !=
		v1alpha1.CcfMemberPhaseActive {
		log.Info("CcfMember not Active yet, waiting",
			"member", gs.Spec.MemberRef,
			"phase", member.Status.Phase)
		return ctrl.Result{
			RequeueAfter: gsRequeueDelay,
		}, nil
	}

	// Transition to Deploying.
	_, span := startSpan(
		ctx, "GovernanceService", "DepsReady", gs.Name,
	)
	defer span.End()

	gs.Status.Phase =
		v1alpha1.GovernanceServicePhaseDeploying
	gs.Status.TraceParent,
		gs.Status.LastOperationTraceId =
		saveTrace(ctx)
	if err := r.Status().Update(ctx, gs); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"transitioning to Deploying: %w", err,
		)
	}

	r.gsEvent(gs, corev1.EventTypeNormal,
		"DepsReady",
		"CcfNetwork is Open and member is Active")

	return ctrl.Result{Requeue: true}, nil
}

// reconcileDeploying drives the governance service
// deployment step by step.
func (r *GovernanceServiceReconciler) reconcileDeploying(
	ctx context.Context,
	gs *v1alpha1.GovernanceService,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	// Resolve member endpoint.
	ep, err := r.resolveMemberEndpoint(ctx, gs)
	if err != nil {
		log.Error(err, "Failed to resolve member endpoint")
		return ctrl.Result{
			RequeueAfter: gsRequeueDelay,
		}, nil
	}

	// Step 1: Set CA cert bundle.
	if !r.conditionIsTrue(gs,
		v1alpha1.ConditionTypeCaCertBundleSet) {
		return r.stepSetCaCertBundle(ctx, gs, ep)
	}

	// Step 2: Set JWT issuers.
	if !r.conditionIsTrue(gs,
		v1alpha1.ConditionTypeJwtIssuersConfigured) {
		return r.stepSetJwtIssuers(ctx, gs, ep)
	}

	// Step 3: Set constitution.
	if !r.conditionIsTrue(gs,
		v1alpha1.ConditionTypeConstitutionDeployed) {
		return r.stepSetConstitution(ctx, gs, ep)
	}

	// Step 4: Set JS runtime options.
	if !r.conditionIsTrue(gs,
		v1alpha1.ConditionTypeJsRuntimeConfigured) {
		return r.stepSetJsRuntime(ctx, gs, ep)
	}

	// Step 5: Set JS app.
	if !r.conditionIsTrue(gs,
		v1alpha1.ConditionTypeJsAppDeployed) {
		return r.stepSetJsApp(ctx, gs, ep)
	}

	// Step 6: Enable OIDC issuer + generate signing key.
	if !r.conditionIsTrue(gs,
		v1alpha1.ConditionTypeOidcIssuerEnabled) {
		return r.stepEnableOidc(ctx, gs, ep)
	}

	// Step 7: Upload OIDC discovery documents to the
	// shared OIDC storage account.
	if !r.conditionIsTrue(gs,
		v1alpha1.ConditionTypeOidcDocumentsUploaded) {
		return r.stepUploadOidcDocuments(ctx, gs)
	}

	// All steps complete — transition to Ready.
	gs.Status.Phase = v1alpha1.GovernanceServicePhaseReady
	gs.Status.TraceParent = ""
	if err := r.Status().Update(ctx, gs); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"transitioning to Ready: %w", err,
		)
	}

	r.gsEvent(gs, corev1.EventTypeNormal,
		"Ready", "Governance service deployed")

	return ctrl.Result{}, nil
}

func (r *GovernanceServiceReconciler) resolveMemberEndpoint(
	ctx context.Context,
	gs *v1alpha1.GovernanceService,
) (string, error) {
	var member v1alpha1.CcfMember
	if err := r.Get(ctx, types.NamespacedName{
		Name:      gs.Spec.MemberRef,
		Namespace: gs.Namespace,
	}, &member); err != nil {
		return "", fmt.Errorf(
			"fetching CcfMember %s: %w",
			gs.Spec.MemberRef, err,
		)
	}

	if member.Status.GovernanceClientEndpoint == "" {
		return "", fmt.Errorf(
			"CcfMember %s has no governance endpoint",
			gs.Spec.MemberRef,
		)
	}

	return member.Status.GovernanceClientEndpoint, nil
}

func (r *GovernanceServiceReconciler) stepSetCaCertBundle(
	ctx context.Context,
	gs *v1alpha1.GovernanceService,
	ep string,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "GovernanceService",
		"SetCaCertBundle", gs.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	log.Info("Setting CA cert bundle")

	caCerts := trustedRootCAs()
	if _, err := r.CgsClient.SetCaCertBundle(
		ctx, ep, "trusted_root_cas", caCerts,
	); err != nil {
		if sErr := r.setGSFailed(ctx, gs,
			"CaCertBundleFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: gsRequeueDelay,
		}, nil
	}

	r.setConditionTrue(gs,
		v1alpha1.ConditionTypeCaCertBundleSet,
		"Set", "CA cert bundle configured")
	if err := r.Status().Update(ctx, gs); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after CA certs: %w", err,
		)
	}

	r.gsEvent(gs, corev1.EventTypeNormal,
		"CaCertBundleSet", "CA cert bundle configured")

	return ctrl.Result{Requeue: true}, nil
}

func (r *GovernanceServiceReconciler) stepSetJwtIssuers(
	ctx context.Context,
	gs *v1alpha1.GovernanceService,
	ep string,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "GovernanceService",
		"SetJwtIssuers", gs.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	log.Info("Setting JWT issuers")

	// Tenant-specific issuer.
	if gs.Spec.TenantId != "" {
		issuerURL := fmt.Sprintf(
			"https://sts.windows.net/%s/",
			gs.Spec.TenantId,
		)
		if _, err := r.CgsClient.SetJwtIssuer(
			ctx, ep, issuerURL, "trusted_root_cas", true,
		); err != nil {
			if sErr := r.setGSFailed(ctx, gs,
				"JwtIssuerFailed", err); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{
				RequeueAfter: gsRequeueDelay,
			}, nil
		}
	}

	// Common issuer.
	commonIssuer :=
		"https://login.microsoftonline.com/common/v2.0"
	if _, err := r.CgsClient.SetJwtIssuer(
		ctx, ep, commonIssuer, "trusted_root_cas", true,
	); err != nil {
		if sErr := r.setGSFailed(ctx, gs,
			"JwtIssuerFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: gsRequeueDelay,
		}, nil
	}

	r.setConditionTrue(gs,
		v1alpha1.ConditionTypeJwtIssuersConfigured,
		"Configured", "JWT issuers configured")
	if err := r.Status().Update(ctx, gs); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after JWT issuers: %w", err,
		)
	}

	r.gsEvent(gs, corev1.EventTypeNormal,
		"JwtIssuersConfigured", "JWT issuers configured")

	return ctrl.Result{Requeue: true}, nil
}

func (r *GovernanceServiceReconciler) stepSetConstitution(
	ctx context.Context,
	gs *v1alpha1.GovernanceService,
	ep string,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "GovernanceService",
		"SetConstitution", gs.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	log.Info("Setting constitution")

	image := gs.Spec.ConstitutionImage
	if image == "" {
		image = os.Getenv("CGS_CONSTITUTION_IMAGE")
	}
	if image == "" {
		if sErr := r.setGSFailed(ctx, gs,
			"ConstitutionImageMissing",
			fmt.Errorf(
				"no constitution image configured "+
					"(spec.constitutionImage or "+
					"CGS_CONSTITUTION_IMAGE env var)")); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{}, nil
	}

	constitution, err := oci.PullFileContent(
		ctx, image, "constitution.json",
	)
	if err != nil {
		if sErr := r.setGSFailed(ctx, gs,
			"ConstitutionDownloadFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: gsRequeueDelay,
		}, nil
	}

	// constitution.json is a JSON-encoded string.
	var constitutionStr string
	if err := json.Unmarshal(
		constitution, &constitutionStr,
	); err != nil {
		// If it's not a JSON string, use raw content.
		constitutionStr = string(constitution)
	}

	if _, err := r.CgsClient.SetConstitution(
		ctx, ep, constitutionStr,
	); err != nil {
		if sErr := r.setGSFailed(ctx, gs,
			"ConstitutionDeployFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: gsRequeueDelay,
		}, nil
	}

	r.setConditionTrue(gs,
		v1alpha1.ConditionTypeConstitutionDeployed,
		"Deployed", "Constitution deployed")
	if err := r.Status().Update(ctx, gs); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after constitution: %w", err,
		)
	}

	r.gsEvent(gs, corev1.EventTypeNormal,
		"ConstitutionDeployed", "Constitution deployed")

	return ctrl.Result{Requeue: true}, nil
}

func (r *GovernanceServiceReconciler) stepSetJsRuntime(
	ctx context.Context,
	gs *v1alpha1.GovernanceService,
	ep string,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "GovernanceService",
		"SetJsRuntime", gs.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	log.Info("Setting JS runtime options")

	if _, err := r.CgsClient.SetJsRuntimeOptions(
		ctx, ep,
	); err != nil {
		if sErr := r.setGSFailed(ctx, gs,
			"JsRuntimeFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: gsRequeueDelay,
		}, nil
	}

	r.setConditionTrue(gs,
		v1alpha1.ConditionTypeJsRuntimeConfigured,
		"Configured", "JS runtime options configured")
	if err := r.Status().Update(ctx, gs); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after JS runtime: %w", err,
		)
	}

	r.gsEvent(gs, corev1.EventTypeNormal,
		"JsRuntimeConfigured",
		"JS runtime options configured")

	return ctrl.Result{Requeue: true}, nil
}

func (r *GovernanceServiceReconciler) stepSetJsApp(
	ctx context.Context,
	gs *v1alpha1.GovernanceService,
	ep string,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "GovernanceService",
		"SetJsApp", gs.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	log.Info("Setting JS app")

	image := gs.Spec.JsAppImage
	if image == "" {
		image = os.Getenv("CGS_JS_APP_IMAGE")
	}
	if image == "" {
		if sErr := r.setGSFailed(ctx, gs,
			"JsAppImageMissing",
			fmt.Errorf(
				"no JS app image configured "+
					"(spec.jsAppImage or "+
					"CGS_JS_APP_IMAGE env var)")); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{}, nil
	}

	bundleData, err := oci.PullFileContent(
		ctx, image, "bundle.json",
	)
	if err != nil {
		if sErr := r.setGSFailed(ctx, gs,
			"JsAppDownloadFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: gsRequeueDelay,
		}, nil
	}

	var bundle client.JsAppBundle
	if err := json.Unmarshal(
		bundleData, &bundle,
	); err != nil {
		if sErr := r.setGSFailed(ctx, gs,
			"JsAppParseFailed",
			fmt.Errorf(
				"parsing bundle.json: %w", err)); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{}, nil
	}

	if _, err := r.CgsClient.SetJsApp(
		ctx, ep, &bundle,
	); err != nil {
		if sErr := r.setGSFailed(ctx, gs,
			"JsAppDeployFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: gsRequeueDelay,
		}, nil
	}

	r.setConditionTrue(gs,
		v1alpha1.ConditionTypeJsAppDeployed,
		"Deployed", "JS app deployed")
	if err := r.Status().Update(ctx, gs); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after JS app: %w", err,
		)
	}

	r.gsEvent(gs, corev1.EventTypeNormal,
		"JsAppDeployed", "JS app deployed")

	return ctrl.Result{Requeue: true}, nil
}

func (r *GovernanceServiceReconciler) stepEnableOidc(
	ctx context.Context,
	gs *v1alpha1.GovernanceService,
	ep string,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "GovernanceService",
		"EnableOidc", gs.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	log.Info("Enabling OIDC issuer")

	if _, err := r.CgsClient.EnableOidcIssuer(
		ctx, ep,
	); err != nil {
		if sErr := r.setGSFailed(ctx, gs,
			"OidcEnableFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: gsRequeueDelay,
		}, nil
	}

	if err := r.CgsClient.GenerateOidcSigningKey(
		ctx, ep,
	); err != nil {
		if sErr := r.setGSFailed(ctx, gs,
			"OidcKeyGenFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: gsRequeueDelay,
		}, nil
	}

	r.setConditionTrue(gs,
		v1alpha1.ConditionTypeOidcIssuerEnabled,
		"Enabled", "OIDC issuer enabled and key generated")
	if err := r.Status().Update(ctx, gs); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after OIDC: %w", err,
		)
	}

	r.gsEvent(gs, corev1.EventTypeNormal,
		"OidcEnabled",
		"OIDC issuer enabled and signing key generated")

	return ctrl.Result{Requeue: true}, nil
}

// stepUploadOidcDocuments fetches JWKS from the CCF
// network and uploads the OpenID configuration and JWKS
// documents to the shared OIDC storage account so that
// Azure AD can federate tokens issued by the CCF
// governance service.
func (r *GovernanceServiceReconciler) stepUploadOidcDocuments(
	ctx context.Context,
	gs *v1alpha1.GovernanceService,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "GovernanceService",
		"UploadOidcDocuments", gs.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)

	if r.AzureClient == nil {
		if sErr := r.setGSFailed(ctx, gs,
			"AzureClientMissing", fmt.Errorf(
				"Azure client not available; "+
					"OIDC document upload requires "+
					"Azure credentials",
			)); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{}, nil
	}

	// Resolve CCF network endpoint via NetworkRef.
	var network v1alpha1.CcfNetwork
	if err := r.Get(ctx, types.NamespacedName{
		Name:      gs.Spec.NetworkRef,
		Namespace: gs.Namespace,
	}, &network); err != nil {
		log.Info(
			"CcfNetwork not available, requeuing",
			"error", err.Error())
		return ctrl.Result{
			RequeueAfter: gsRequeueDelay,
		}, nil
	}

	if network.Status.Endpoint == "" {
		log.Info(
			"CcfNetwork has no endpoint, requeuing")
		return ctrl.Result{
			RequeueAfter: gsRequeueDelay,
		}, nil
	}

	// Fetch JWKS from CCF.
	jwks, err := fetchJWKSFromCCF(
		ctx, network.Status.Endpoint,
	)
	if err != nil {
		if sErr := r.setGSFailed(ctx, gs,
			"JWKSFetchFailed", fmt.Errorf(
				"fetching JWKS from CCF: %w", err,
			)); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{}, nil
	}

	// Derive OIDC container name from spec or fall
	// back to GS name.
	oidcSAName := v1alpha1.OidcStorageAccountName
	oidcContainer := gs.Spec.OidcContainerName
	if oidcContainer == "" {
		oidcContainer = gs.Name
	}
	issuerURL := azure.GetStaticWebsiteURL(
		oidcSAName,
	) + "/" + oidcContainer

	log.Info("Uploading OIDC documents",
		"storageAccount", oidcSAName,
		"container", oidcContainer,
		"issuerURL", issuerURL)

	// Upload OIDC discovery + JWKS docs.
	if err := r.AzureClient.UploadOidcDocuments(
		ctx, oidcSAName, oidcContainer,
		issuerURL, jwks,
	); err != nil {
		if sErr := r.setGSFailed(ctx, gs,
			"OidcUploadFailed", fmt.Errorf(
				"uploading OIDC documents: %w", err,
			)); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{}, nil
	}

	gs.Status.OidcIssuerUrl = issuerURL
	r.setConditionTrue(gs,
		v1alpha1.ConditionTypeOidcDocumentsUploaded,
		"Uploaded",
		fmt.Sprintf(
			"OIDC documents uploaded: %s", issuerURL,
		))
	if err := r.Status().Update(ctx, gs); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after OIDC upload: %w",
			err,
		)
	}

	r.gsEvent(gs, corev1.EventTypeNormal,
		"OidcDocumentsUploaded",
		fmt.Sprintf(
			"OIDC documents uploaded: %s", issuerURL,
		))

	return ctrl.Result{Requeue: true}, nil
}

func (r *GovernanceServiceReconciler) reconcileDelete(
	ctx context.Context,
	gs *v1alpha1.GovernanceService,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	if !controllerutil.ContainsFinalizer(
		gs, gsFinalizerName,
	) {
		return ctrl.Result{}, nil
	}

	log.Info("Deleting GovernanceService resources",
		"name", gs.Name)

	controllerutil.RemoveFinalizer(gs, gsFinalizerName)
	if err := r.Update(ctx, gs); err != nil {
		if apierrors.IsNotFound(err) {
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"removing finalizer: %w", err,
		)
	}

	return ctrl.Result{}, nil
}

// conditionIsTrue checks if a condition is True on the GS.
func (r *GovernanceServiceReconciler) conditionIsTrue(
	gs *v1alpha1.GovernanceService,
	condType string,
) bool {
	for _, c := range gs.Status.Conditions {
		if c.Type == condType &&
			c.Status == metav1.ConditionTrue {
			return true
		}
	}
	return false
}

func (r *GovernanceServiceReconciler) setConditionTrue(
	gs *v1alpha1.GovernanceService,
	condType string,
	reason string,
	message string,
) {
	meta.SetStatusCondition(
		&gs.Status.Conditions,
		metav1.Condition{
			Type:    condType,
			Status:  metav1.ConditionTrue,
			Reason:  reason,
			Message: message,
		},
	)
}

func (r *GovernanceServiceReconciler) setGSFailed(
	ctx context.Context,
	gs *v1alpha1.GovernanceService,
	reason string,
	err error,
) error {
	log := ctrllog.FromContext(ctx)
	log.Error(err, "Operation failed: "+reason)
	recordContextError(ctx, err)

	message := err.Error()
	gs.Status.Phase = v1alpha1.GovernanceServicePhaseFailed
	gs.Status.TraceParent = ""
	meta.SetStatusCondition(
		&gs.Status.Conditions,
		metav1.Condition{
			Type:    "Failed",
			Status:  metav1.ConditionTrue,
			Reason:  reason,
			Message: message,
		},
	)
	if err := r.Status().Update(ctx, gs); err != nil {
		return fmt.Errorf(
			"updating failed status: %w", err,
		)
	}
	r.gsEvent(gs, corev1.EventTypeWarning,
		reason, message)
	return nil
}

func (r *GovernanceServiceReconciler) gsEvent(
	gs *v1alpha1.GovernanceService,
	eventType string,
	reason string,
	message string,
) {
	if r.Recorder != nil {
		r.Recorder.Event(gs, eventType, reason, message)
	}
}

// trustedRootCAs returns the concatenated PEM certificates
// for trusted root CAs (Microsoft, DigiCert, etc.).
// Loaded via go:embed from data/trusted_root_cas.pem.
func trustedRootCAs() string {
	return trustedRootCACerts
}

// SetupWithManager sets up the controller with the Manager.
func (r *GovernanceServiceReconciler) SetupWithManager(
	mgr ctrl.Manager,
) error {
	return ctrl.NewControllerManagedBy(mgr).
		For(&v1alpha1.GovernanceService{}).
		Complete(r)
}
