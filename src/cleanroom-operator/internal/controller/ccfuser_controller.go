package controller

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"time"

	"github.com/google/uuid"
	appsv1 "k8s.io/api/apps/v1"
	corev1 "k8s.io/api/core/v1"
	apierrors "k8s.io/apimachinery/pkg/api/errors"
	"k8s.io/apimachinery/pkg/api/meta"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/runtime"
	"k8s.io/apimachinery/pkg/types"
	"k8s.io/apimachinery/pkg/util/intstr"
	"k8s.io/client-go/tools/record"
	ctrl "sigs.k8s.io/controller-runtime"
	ctrlclient "sigs.k8s.io/controller-runtime/pkg/client"
	"sigs.k8s.io/controller-runtime/pkg/controller/controllerutil"
	ctrllog "sigs.k8s.io/controller-runtime/pkg/log"

	v1alpha1 "github.com/Azure/azure-cleanroom/cleanroom-operator/api/v1alpha1"
	"github.com/Azure/azure-cleanroom/cleanroom-operator/client"
)

const (
	ccfUserFinalizerName = "cleanroom.azure.com/ccfuser-finalizer"
	localIdpServiceName  = "local-idp"
	localIdpServicePort  = 8321
	localIdpIssuerUrl    = "http://local-idp.com/oidc"
	userRequeueDelay     = 15 * time.Second
)

// CcfUserReconciler reconciles CcfUser objects.
type CcfUserReconciler struct {
	ctrlclient.Client
	Scheme    *runtime.Scheme
	CgsClient *client.CgsClient
	Recorder  record.EventRecorder
}

// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=ccfusers,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=ccfusers/status,verbs=get;update;patch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=ccfusers/finalizers,verbs=update
// +kubebuilder:rbac:groups=apps,resources=deployments,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups="",resources=services,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups="",resources=configmaps,verbs=get;list;watch;create;update;patch
// +kubebuilder:rbac:groups="",resources=events,verbs=create;patch

// Reconcile handles the reconciliation loop for CcfUser.
func (r *CcfUserReconciler) Reconcile(
	ctx context.Context,
	req ctrl.Request,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	var user v1alpha1.CcfUser
	if err := r.Get(
		ctx, req.NamespacedName, &user,
	); err != nil {
		if apierrors.IsNotFound(err) {
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"fetching CcfUser: %w", err,
		)
	}

	ctx = resolveTraceContext(
		ctx, &user, user.Status.TraceParent,
	)
	ctx, span := startSpan(
		ctx, "CcfUser", "Reconcile", user.Name,
	)
	defer span.End()

	// Handle deletion.
	if !user.DeletionTimestamp.IsZero() {
		return r.reconcileDelete(ctx, &user)
	}

	// Ensure finalizer is set.
	if !controllerutil.ContainsFinalizer(
		&user, ccfUserFinalizerName,
	) {
		controllerutil.AddFinalizer(
			&user, ccfUserFinalizerName,
		)
		if err := r.Update(ctx, &user); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"adding finalizer: %w", err,
			)
		}
		return ctrl.Result{Requeue: true}, nil
	}

	// Route based on phase.
	switch user.Status.Phase {
	case "", v1alpha1.CcfUserPhasePending:
		return r.reconcileJwtIssuer(ctx, &user)

	case v1alpha1.CcfUserPhaseGovClientDeployed:
		return r.reconcileAddUserIdentity(
			ctx, &user,
		)

	case v1alpha1.CcfUserPhaseActive:
		return r.ensureGovernanceClient(ctx, &user)

	case v1alpha1.CcfUserPhaseFailed:
		if user.Generation !=
			user.Status.ObservedGeneration ||
			user.Annotations[retryAnnotation] != "" {
			if user.Annotations[retryAnnotation] != "" {
				delete(
					user.Annotations,
					retryAnnotation,
				)
				if err := r.Update(
					ctx, &user,
				); err != nil {
					return ctrl.Result{}, fmt.Errorf(
						"clearing retry annotation: %w",
						err,
					)
				}
				r.userEvent(
					&user,
					corev1.EventTypeNormal,
					"Retrying",
					"Retry requested via annotation",
				)
			}
			user.Status.Phase =
				v1alpha1.CcfUserPhasePending
			meta.RemoveStatusCondition(
				&user.Status.Conditions,
				v1alpha1.ConditionTypeJwtIssuerRegistered,
			)
			meta.RemoveStatusCondition(
				&user.Status.Conditions,
				v1alpha1.ConditionTypeUserGovClientReady,
			)
			meta.RemoveStatusCondition(
				&user.Status.Conditions,
				v1alpha1.ConditionTypeUserIdentityAdded,
			)
			if err := r.Status().Update(
				ctx, &user,
			); err != nil {
				return ctrl.Result{}, fmt.Errorf(
					"resetting phase to Pending: %w",
					err,
				)
			}
			return ctrl.Result{Requeue: true}, nil
		}
		return ctrl.Result{}, nil

	default:
		log.Info("Unknown phase, requeueing",
			"phase", user.Status.Phase)
		return ctrl.Result{
			RequeueAfter: userRequeueDelay,
		}, nil
	}
}

// reconcileJwtIssuer registers the local IDP as a JWT
// issuer in CCF (set_jwt_issuer + set_jwt_public_signing_keys),
// then deploys the user's governance client.
func (r *CcfUserReconciler) reconcileJwtIssuer(
	ctx context.Context,
	user *v1alpha1.CcfUser,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	// Skip if already done.
	if userConditionIsTrue(
		user, v1alpha1.ConditionTypeJwtIssuerRegistered,
	) {
		return r.reconcileGovernanceClient(ctx, user)
	}

	// Generate userId and tenantId if not already set.
	if user.Status.UserId == "" {
		// Check if a persisted user ID exists in the
		// shared ConfigMap (survives cluster recreation
		// via dev save/restore).
		userId, tenantId, found := r.lookupPersistedUserIds(
			ctx, user,
		)
		if found {
			user.Status.UserId = userId
			user.Status.TenantId = tenantId
			log.Info(
				"Restored user IDs from ConfigMap",
				"userId", userId,
				"tenantId", tenantId,
			)
		} else {
			user.Status.UserId = uuid.New().String()
			user.Status.TenantId = uuid.New().String()
		}

		if err := r.Status().Update(
			ctx, user,
		); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"storing generated user/tenant IDs: %w",
				err,
			)
		}

		// Persist user IDs in the shared ConfigMap so
		// they survive cluster recreation.
		if err := r.persistUserIds(
			ctx, user,
		); err != nil {
			log.Error(err,
				"Failed to persist user IDs to ConfigMap")
		}

		return ctrl.Result{Requeue: true}, nil
	}

	// Look up the member's CGS endpoint.
	memberEndpoint, err := r.resolveMemberEndpoint(
		ctx, user,
	)
	if err != nil {
		log.Info(
			"Member CGS not ready, waiting",
			"member", user.Spec.MemberRef,
			"error", err.Error(),
		)
		return ctrl.Result{
			RequeueAfter: userRequeueDelay,
		}, nil
	}

	// Resolve local IDP endpoint.
	idpEndpoint := r.localIdpEndpoint(user.Namespace)

	ctx, span := startSpan(
		ctx, "CcfUser", "RegisterJwtIssuer", user.Name,
	)
	defer span.End()

	log.Info("Registering local IDP JWT issuer in CCF")

	// 1. Configure issuer URL on the local IDP.
	if err := r.CgsClient.SetLocalIdpIssuerUrl(
		ctx, idpEndpoint, localIdpIssuerUrl,
	); err != nil {
		if sErr := r.setUserFailed(ctx, user,
			"SetIssuerUrlFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: userRequeueDelay,
		}, nil
	}

	// 2. Submit set_jwt_issuer proposal and accept.
	jwtIssuerResp, err := r.CgsClient.ProposeSetJwtIssuer(
		ctx, memberEndpoint, localIdpIssuerUrl,
	)
	if err != nil {
		if sErr := r.setUserFailed(ctx, user,
			"JwtIssuerProposalFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: userRequeueDelay,
		}, nil
	}
	if err := r.CgsClient.VoteAcceptProposal(
		ctx, memberEndpoint, jwtIssuerResp.ProposalID,
	); err != nil {
		if sErr := r.setUserFailed(ctx, user,
			"JwtIssuerVoteFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: userRequeueDelay,
		}, nil
	}

	// 3. Generate signing key on the local IDP.
	signingKey, err := r.CgsClient.GenerateLocalIdpSigningKey(
		ctx, idpEndpoint,
	)
	if err != nil {
		if sErr := r.setUserFailed(ctx, user,
			"SigningKeyGenerationFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: userRequeueDelay,
		}, nil
	}

	// 4. Submit set_jwt_public_signing_keys proposal and
	// accept.
	sigKeysResp, err := r.CgsClient.ProposeSetJwtPublicSigningKeys(
		ctx, memberEndpoint, localIdpIssuerUrl,
		signingKey.KID, signingKey.X5C,
	)
	if err != nil {
		if sErr := r.setUserFailed(ctx, user,
			"JwtSigningKeysProposalFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: userRequeueDelay,
		}, nil
	}
	if err := r.CgsClient.VoteAcceptProposal(
		ctx, memberEndpoint, sigKeysResp.ProposalID,
	); err != nil {
		if sErr := r.setUserFailed(ctx, user,
			"JwtSigningKeysVoteFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: userRequeueDelay,
		}, nil
	}

	r.userSetConditionTrue(user,
		v1alpha1.ConditionTypeJwtIssuerRegistered,
		"Registered",
		"Local IDP JWT issuer registered in CCF",
	)
	user.Status.TraceParent,
		user.Status.LastOperationTraceId = saveTrace(ctx)
	if err := r.Status().Update(ctx, user); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after JWT issuer: %w", err,
		)
	}

	r.userEvent(user, corev1.EventTypeNormal,
		"JwtIssuerRegistered",
		"Local IDP JWT issuer registered in CCF")

	return ctrl.Result{Requeue: true}, nil
}

// reconcileGovernanceClient deploys a CGS client
// Deployment + Service for the user with local IDP auth.
func (r *CcfUserReconciler) reconcileGovernanceClient(
	ctx context.Context,
	user *v1alpha1.CcfUser,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)
	deployName := r.userGovClientDeploymentName(user)
	svcName := r.userGovClientServiceName(user)

	// Wait for CcfNetwork.
	network, err := r.resolveNetwork(ctx, user)
	if err != nil {
		log.Info("CcfNetwork not ready, waiting",
			"network", user.Spec.NetworkRef,
			"error", err.Error(),
		)
		return ctrl.Result{
			RequeueAfter: userRequeueDelay,
		}, nil
	}

	// Check if Deployment exists.
	var deploy appsv1.Deployment
	getErr := r.Get(ctx, types.NamespacedName{
		Name:      deployName,
		Namespace: user.Namespace,
	}, &deploy)

	if apierrors.IsNotFound(getErr) {
		ctx, span := startSpan(
			ctx, "CcfUser", "DeployGovClient",
			user.Name,
		)
		defer span.End()

		log.Info(
			"Creating user governance client Deployment",
			"deployment", deployName,
		)

		if err := r.createUserGovClientDeployment(
			ctx, user, network,
		); err != nil {
			if sErr := r.setUserFailed(ctx, user,
				"DeploymentFailed", err); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{
				RequeueAfter: userRequeueDelay,
			}, nil
		}

		if err := r.createUserGovClientService(
			ctx, user,
		); err != nil && !apierrors.IsAlreadyExists(err) {
			if sErr := r.setUserFailed(ctx, user,
				"ServiceFailed", err); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{
				RequeueAfter: userRequeueDelay,
			}, nil
		}

		r.userEvent(user, corev1.EventTypeNormal,
			"GovernanceClientCreated",
			"User governance client Deployment and "+
				"Service created")
	} else if getErr != nil {
		return ctrl.Result{}, fmt.Errorf(
			"checking deployment %s: %w",
			deployName, getErr,
		)
	}

	// Check Deployment readiness.
	if deploy.Status.ReadyReplicas < 1 {
		log.Info(
			"Waiting for user governance client ready",
			"deployment", deployName,
		)
		return ctrl.Result{
			RequeueAfter: userRequeueDelay,
		}, nil
	}

	// Set status.
	endpoint := fmt.Sprintf(
		"http://%s.%s.svc.cluster.local:%d",
		svcName, user.Namespace, cgsClientPort,
	)
	user.Status.Phase =
		v1alpha1.CcfUserPhaseGovClientDeployed
	user.Status.GovernanceClientEndpoint = endpoint
	r.userSetConditionTrue(user,
		v1alpha1.ConditionTypeUserGovClientReady,
		"DeploymentReady",
		"User governance client is running",
	)
	if err := r.Status().Update(ctx, user); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after gov client: %w", err,
		)
	}

	return ctrl.Result{Requeue: true}, nil
}

// reconcileAddUserIdentity adds the user identity in CGS
// via the member's governance client.
func (r *CcfUserReconciler) reconcileAddUserIdentity(
	ctx context.Context,
	user *v1alpha1.CcfUser,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	// Skip if already done.
	if userConditionIsTrue(
		user, v1alpha1.ConditionTypeUserIdentityAdded,
	) {
		return ctrl.Result{}, nil
	}

	memberEndpoint, err := r.resolveMemberEndpoint(
		ctx, user,
	)
	if err != nil {
		log.Info("Member CGS not ready, waiting",
			"member", user.Spec.MemberRef,
			"error", err.Error(),
		)
		return ctrl.Result{
			RequeueAfter: userRequeueDelay,
		}, nil
	}

	ctx, span := startSpan(
		ctx, "CcfUser", "AddUserIdentity", user.Name,
	)
	defer span.End()

	log.Info("Adding user identity in CGS",
		"userId", user.Status.UserId,
		"tenantId", user.Status.TenantId,
	)

	resp, err := r.CgsClient.AddUserIdentity(
		ctx, memberEndpoint,
		user.Spec.Identifier,
		user.Status.UserId,
		user.Status.TenantId,
	)
	if err != nil {
		if sErr := r.setUserFailed(ctx, user,
			"UserIdentityFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: userRequeueDelay,
		}, nil
	}

	// Auto-accept the proposal.
	if err := r.CgsClient.VoteAcceptProposal(
		ctx, memberEndpoint, resp.ProposalID,
	); err != nil {
		if sErr := r.setUserFailed(ctx, user,
			"UserIdentityVoteFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		return ctrl.Result{
			RequeueAfter: userRequeueDelay,
		}, nil
	}

	r.userSetConditionTrue(user,
		v1alpha1.ConditionTypeUserIdentityAdded,
		"Added",
		"User identity added to CGS",
	)
	user.Status.Phase = v1alpha1.CcfUserPhaseActive
	user.Status.TraceParent = ""
	user.Status.ObservedGeneration = user.Generation
	if err := r.Status().Update(ctx, user); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after user identity: %w",
			err,
		)
	}

	r.userEvent(user, corev1.EventTypeNormal,
		"Active",
		"User identity added, CcfUser is active")

	return ctrl.Result{}, nil
}

// ensureGovernanceClient checks the user's governance
// client Deployment still exists and is ready.
func (r *CcfUserReconciler) ensureGovernanceClient(
	ctx context.Context,
	user *v1alpha1.CcfUser,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)
	deployName := r.userGovClientDeploymentName(user)

	var deploy appsv1.Deployment
	err := r.Get(ctx, types.NamespacedName{
		Name:      deployName,
		Namespace: user.Namespace,
	}, &deploy)

	if apierrors.IsNotFound(err) {
		log.Info(
			"User governance client missing, recreating",
			"deployment", deployName,
		)

		network, netErr := r.resolveNetwork(ctx, user)
		if netErr != nil {
			return ctrl.Result{
				RequeueAfter: userRequeueDelay,
			}, nil
		}

		if err := r.createUserGovClientDeployment(
			ctx, user, network,
		); err != nil {
			if apierrors.IsInvalid(err) ||
				apierrors.IsForbidden(err) {
				if sErr := r.setUserFailed(
					ctx, user,
					"DeploymentCreateRejected",
					fmt.Errorf(
						"recreating user deployment: %w",
						err,
					),
				); sErr != nil {
					return ctrl.Result{}, sErr
				}
				return ctrl.Result{}, nil
			}
			return ctrl.Result{}, fmt.Errorf(
				"recreating user deployment: %w", err,
			)
		}
		if err := r.createUserGovClientService(
			ctx, user,
		); err != nil && !apierrors.IsAlreadyExists(err) {
			if apierrors.IsInvalid(err) ||
				apierrors.IsForbidden(err) {
				if sErr := r.setUserFailed(
					ctx, user,
					"ServiceCreateRejected",
					fmt.Errorf(
						"recreating user service: %w",
						err,
					),
				); sErr != nil {
					return ctrl.Result{}, sErr
				}
				return ctrl.Result{}, nil
			}
			return ctrl.Result{}, fmt.Errorf(
				"recreating user service: %w", err,
			)
		}
		r.userEvent(user, corev1.EventTypeNormal,
			"GovernanceClientRecreated",
			"User governance client recreated")
		return ctrl.Result{
			RequeueAfter: userRequeueDelay,
		}, nil
	}
	if err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"checking deployment %s: %w",
			deployName, err,
		)
	}

	if deploy.Status.ReadyReplicas < 1 {
		log.Info(
			"Waiting for user governance client ready",
			"deployment", deployName,
		)
		return ctrl.Result{
			RequeueAfter: userRequeueDelay,
		}, nil
	}

	return ctrl.Result{}, nil
}

// createUserGovClientDeployment creates a CGS client
// Deployment for the user. Unlike CcfMember, the user
// authenticates via the local IDP (no cert volume mount).
func (r *CcfUserReconciler) createUserGovClientDeployment(
	ctx context.Context,
	user *v1alpha1.CcfUser,
	network *v1alpha1.CcfNetwork,
) error {
	image := user.Spec.CgsImage
	if image == "" {
		image = os.Getenv("CGS_CLIENT_IMAGE")
	}
	if image == "" {
		image = defaultCgsImage
	}

	uiImage := os.Getenv("CGS_UI_IMAGE")
	if uiImage == "" {
		uiImage = defaultCgsUIImage
	}

	labels := r.userGovClientLabels(user)
	replicas := int32(1)

	// Local IDP token endpoint for this user.
	idpEndpoint := fmt.Sprintf(
		"http://%s.%s.svc.cluster.local:%d",
		localIdpServiceName,
		r.operatorNamespace(),
		localIdpServicePort,
	)
	tokenEndpoint := fmt.Sprintf(
		"%s/oauth/token?oid=%s&tid=%s",
		idpEndpoint,
		user.Status.UserId,
		user.Status.TenantId,
	)

	envVars := []corev1.EnvVar{
		{
			Name:  "CGS_CLIENT_USE_LOCAL_IDENTITY",
			Value: "true",
		},
		{
			Name:  "LOCAL_IDP_ENDPOINT",
			Value: tokenEndpoint,
		},
	}
	if network != nil {
		envVars = append(envVars,
			corev1.EnvVar{
				Name:  "CGS_CLIENT_CCF_ENDPOINT",
				Value: network.Status.Endpoint,
			},
			corev1.EnvVar{
				Name:  "CGS_CLIENT_SERVICE_CERT",
				Value: network.Status.ServiceCert,
			},
		)
	}

	// Forward OTEL env vars when telemetry is enabled.
	if otlpEndpoint := os.Getenv(
		"OTEL_EXPORTER_OTLP_ENDPOINT",
	); otlpEndpoint != "" {
		envVars = append(envVars,
			corev1.EnvVar{
				Name:  "CGS_CLIENT_ENABLE_OPEN_TELEMETRY",
				Value: "true",
			},
			corev1.EnvVar{
				Name:  "OTEL_EXPORTER_OTLP_ENDPOINT",
				Value: otlpEndpoint,
			},
			corev1.EnvVar{
				Name:  "OTEL_SERVICE_NAME",
				Value: "cgs-user-client",
			},
		)
	}

	deploy := &appsv1.Deployment{
		ObjectMeta: metav1.ObjectMeta{
			Name:      r.userGovClientDeploymentName(user),
			Namespace: user.Namespace,
			Labels:    labels,
		},
		Spec: appsv1.DeploymentSpec{
			Replicas: &replicas,
			Selector: &metav1.LabelSelector{
				MatchLabels: labels,
			},
			Template: corev1.PodTemplateSpec{
				ObjectMeta: metav1.ObjectMeta{
					Labels: labels,
				},
				Spec: corev1.PodSpec{
					Containers: []corev1.Container{
						{
							Name:  "cgs-client",
							Image: image,
							Ports: []corev1.ContainerPort{{
								ContainerPort: int32(
									cgsClientPort,
								),
								Protocol: corev1.ProtocolTCP,
							}},
							Env: envVars,
							ReadinessProbe: &corev1.Probe{
								ProbeHandler: corev1.ProbeHandler{
									HTTPGet: &corev1.HTTPGetAction{
										Path: "/ready",
										Port: intstr.FromInt32(
											int32(cgsClientPort),
										),
									},
								},
								InitialDelaySeconds: 5,
								PeriodSeconds:       10,
							},
						},
						{
							Name:  "cgs-ui",
							Image: uiImage,
							Ports: []corev1.ContainerPort{{
								ContainerPort: int32(
									cgsUIPort,
								),
								Protocol: corev1.ProtocolTCP,
							}},
							Env: []corev1.EnvVar{{
								Name: "cgsclientEndpoint",
								Value: fmt.Sprintf(
									"http://localhost:%d",
									cgsClientPort,
								),
							}},
						},
					},
				},
			},
		},
	}

	if err := controllerutil.SetControllerReference(
		user, deploy, r.Scheme,
	); err != nil {
		return fmt.Errorf(
			"setting owner reference on user "+
				"deployment: %w", err,
		)
	}

	return r.Create(ctx, deploy)
}

func (r *CcfUserReconciler) createUserGovClientService(
	ctx context.Context,
	user *v1alpha1.CcfUser,
) error {
	labels := r.userGovClientLabels(user)
	svc := &corev1.Service{
		ObjectMeta: metav1.ObjectMeta{
			Name:      r.userGovClientServiceName(user),
			Namespace: user.Namespace,
			Labels:    labels,
		},
		Spec: corev1.ServiceSpec{
			Selector: labels,
			Type:     corev1.ServiceTypeClusterIP,
			Ports: []corev1.ServicePort{
				{
					Name: "client",
					Port: int32(cgsClientPort),
					TargetPort: intstr.FromInt32(
						int32(cgsClientPort),
					),
					Protocol: corev1.ProtocolTCP,
				},
				{
					Name: "ui",
					Port: int32(cgsUIPort),
					TargetPort: intstr.FromInt32(
						int32(cgsUIPort),
					),
					Protocol: corev1.ProtocolTCP,
				},
			},
		},
	}

	if err := controllerutil.SetControllerReference(
		user, svc, r.Scheme,
	); err != nil {
		return fmt.Errorf(
			"setting owner reference on user "+
				"service: %w", err,
		)
	}

	return r.Create(ctx, svc)
}

// resolveMemberEndpoint looks up the CcfMember's
// governance client endpoint.
func (r *CcfUserReconciler) resolveMemberEndpoint(
	ctx context.Context,
	user *v1alpha1.CcfUser,
) (string, error) {
	var member v1alpha1.CcfMember
	if err := r.Get(ctx, ctrlclient.ObjectKey{
		Name:      user.Spec.MemberRef,
		Namespace: user.Namespace,
	}, &member); err != nil {
		return "", fmt.Errorf(
			"fetching CcfMember %s: %w",
			user.Spec.MemberRef, err,
		)
	}
	if member.Status.Phase !=
		v1alpha1.CcfMemberPhaseActive {
		return "", fmt.Errorf(
			"CcfMember %s not active (phase=%s)",
			user.Spec.MemberRef, member.Status.Phase,
		)
	}
	if member.Status.GovernanceClientEndpoint == "" {
		return "", fmt.Errorf(
			"CcfMember %s has no governance endpoint",
			user.Spec.MemberRef,
		)
	}
	return member.Status.GovernanceClientEndpoint, nil
}

// resolveNetwork looks up the CcfNetwork and checks it is
// ready.
func (r *CcfUserReconciler) resolveNetwork(
	ctx context.Context,
	user *v1alpha1.CcfUser,
) (*v1alpha1.CcfNetwork, error) {
	var net v1alpha1.CcfNetwork
	if err := r.Get(ctx, ctrlclient.ObjectKey{
		Name:      user.Spec.NetworkRef,
		Namespace: user.Namespace,
	}, &net); err != nil {
		return nil, fmt.Errorf(
			"fetching CcfNetwork %s: %w",
			user.Spec.NetworkRef, err,
		)
	}
	if net.Status.Endpoint == "" ||
		net.Status.ServiceCert == "" {
		return nil, fmt.Errorf(
			"CcfNetwork %s not ready",
			user.Spec.NetworkRef,
		)
	}
	if net.Status.Phase !=
		v1alpha1.CcfNetworkPhaseOpen {
		return nil, fmt.Errorf(
			"CcfNetwork %s not open (phase=%s)",
			user.Spec.NetworkRef, net.Status.Phase,
		)
	}
	return &net, nil
}

// localIdpEndpoint returns the in-cluster HTTP endpoint
// for the local IDP service.
func (r *CcfUserReconciler) localIdpEndpoint(
	namespace string,
) string {
	ns := r.operatorNamespace()
	if ns == "" {
		ns = namespace
	}
	return fmt.Sprintf(
		"http://%s.%s.svc.cluster.local:%d",
		localIdpServiceName, ns, localIdpServicePort,
	)
}

// operatorNamespace returns the namespace where the
// operator (and local-idp) are deployed.
func (r *CcfUserReconciler) operatorNamespace() string {
	ns := os.Getenv("POD_NAMESPACE")
	if ns == "" {
		ns = "cleanroom-system"
	}
	return ns
}

func (r *CcfUserReconciler) reconcileDelete(
	ctx context.Context,
	user *v1alpha1.CcfUser,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	if !controllerutil.ContainsFinalizer(
		user, ccfUserFinalizerName,
	) {
		return ctrl.Result{}, nil
	}

	log.Info("Deleting CcfUser resources",
		"name", user.Name)

	// Owned resources (Deployment, Service) are GC'd via
	// ownerReferences.
	controllerutil.RemoveFinalizer(
		user, ccfUserFinalizerName,
	)
	if err := r.Update(ctx, user); err != nil {
		if apierrors.IsNotFound(err) {
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"removing finalizer: %w", err,
		)
	}

	log.Info("CcfUser deleted", "name", user.Name)
	return ctrl.Result{}, nil
}

func (r *CcfUserReconciler) setUserFailed(
	ctx context.Context,
	user *v1alpha1.CcfUser,
	reason string,
	err error,
) error {
	log := ctrllog.FromContext(ctx)
	log.Error(err, "Operation failed: "+reason)
	recordContextError(ctx, err)

	message := truncateMessage(
		err.Error(), maxConditionMessageLen,
	)
	user.Status.Phase = v1alpha1.CcfUserPhaseFailed
	user.Status.ObservedGeneration = user.Generation
	user.Status.TraceParent = ""
	meta.SetStatusCondition(
		&user.Status.Conditions,
		metav1.Condition{
			Type:    v1alpha1.ConditionTypeReady,
			Status:  metav1.ConditionFalse,
			Reason:  reason,
			Message: message,
		},
	)
	if err := r.Status().Update(ctx, user); err != nil {
		return fmt.Errorf(
			"updating failed status: %w", err,
		)
	}
	return nil
}

func (r *CcfUserReconciler) userGovClientDeploymentName(
	user *v1alpha1.CcfUser,
) string {
	return fmt.Sprintf("cgs-user-%s", user.Name)
}

func (r *CcfUserReconciler) userGovClientServiceName(
	user *v1alpha1.CcfUser,
) string {
	return fmt.Sprintf("cgs-user-%s", user.Name)
}

func (r *CcfUserReconciler) userGovClientLabels(
	user *v1alpha1.CcfUser,
) map[string]string {
	return map[string]string{
		"app.kubernetes.io/name":       "cgs-user-client",
		"app.kubernetes.io/instance":   user.Name,
		"app.kubernetes.io/managed-by": "cleanroom-operator",
		"cleanroom.azure.com/user":     user.Name,
	}
}

func userConditionIsTrue(
	user *v1alpha1.CcfUser,
	conditionType string,
) bool {
	for _, c := range user.Status.Conditions {
		if c.Type == conditionType {
			return c.Status == metav1.ConditionTrue
		}
	}
	return false
}

func (r *CcfUserReconciler) userSetConditionTrue(
	user *v1alpha1.CcfUser,
	conditionType string,
	reason string,
	message string,
) {
	meta.SetStatusCondition(
		&user.Status.Conditions,
		metav1.Condition{
			Type:    conditionType,
			Status:  metav1.ConditionTrue,
			Reason:  reason,
			Message: message,
		},
	)
}

func (r *CcfUserReconciler) userEvent(
	user *v1alpha1.CcfUser,
	eventType string,
	reason string,
	message string,
) {
	if r.Recorder != nil {
		r.Recorder.Event(
			user, eventType, reason, message,
		)
	}
}

// userIdsConfigMapName returns the well-known ConfigMap name
// for persisting CcfUser userId/tenantId pairs.
func userIdsConfigMapName(namespace string) string {
	return namespace + "-user-ids"
}

// lookupPersistedUserIds checks the shared user-ids
// ConfigMap for a previously generated userId/tenantId
// for this CcfUser. Returns ("","",false) if not found.
func (r *CcfUserReconciler) lookupPersistedUserIds(
	ctx context.Context,
	user *v1alpha1.CcfUser,
) (string, string, bool) {
	cmName := userIdsConfigMapName(user.Namespace)
	var cm corev1.ConfigMap
	if err := r.Get(ctx, types.NamespacedName{
		Name:      cmName,
		Namespace: user.Namespace,
	}, &cm); err != nil {
		return "", "", false
	}

	raw, ok := cm.Data[user.Name]
	if !ok {
		return "", "", false
	}

	var ids struct {
		UserId   string `json:"userId"`
		TenantId string `json:"tenantId"`
	}
	if err := json.Unmarshal(
		[]byte(raw), &ids,
	); err != nil {
		return "", "", false
	}
	if ids.UserId == "" || ids.TenantId == "" {
		return "", "", false
	}
	return ids.UserId, ids.TenantId, true
}

// persistUserIds writes the CcfUser's userId/tenantId
// to the shared ConfigMap. The ConfigMap is NOT owned by
// the CcfUser (no owner reference) so it survives CR
// deletion and can be restored to a new cluster.
func (r *CcfUserReconciler) persistUserIds(
	ctx context.Context,
	user *v1alpha1.CcfUser,
) error {
	cmName := userIdsConfigMapName(user.Namespace)
	idsJSON, err := json.Marshal(map[string]string{
		"userId":   user.Status.UserId,
		"tenantId": user.Status.TenantId,
	})
	if err != nil {
		return fmt.Errorf("marshaling user IDs: %w", err)
	}

	var cm corev1.ConfigMap
	err = r.Get(ctx, types.NamespacedName{
		Name:      cmName,
		Namespace: user.Namespace,
	}, &cm)

	if apierrors.IsNotFound(err) {
		cm = corev1.ConfigMap{
			ObjectMeta: metav1.ObjectMeta{
				Name:      cmName,
				Namespace: user.Namespace,
				Labels: map[string]string{
					"cleanroom.azure.com/user-ids": "true",
				},
			},
			Data: map[string]string{
				user.Name: string(idsJSON),
			},
		}
		return r.Create(ctx, &cm)
	}
	if err != nil {
		return fmt.Errorf(
			"fetching ConfigMap %s: %w", cmName, err,
		)
	}

	if cm.Data == nil {
		cm.Data = map[string]string{}
	}
	cm.Data[user.Name] = string(idsJSON)
	return r.Update(ctx, &cm)
}

// SetupWithManager sets up the controller with the Manager.
func (r *CcfUserReconciler) SetupWithManager(
	mgr ctrl.Manager,
) error {
	return ctrl.NewControllerManagedBy(mgr).
		For(&v1alpha1.CcfUser{}).
		Owns(&appsv1.Deployment{}).
		Owns(&corev1.Service{}).
		Complete(r)
}
