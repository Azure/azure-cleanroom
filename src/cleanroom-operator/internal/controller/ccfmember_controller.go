package controller

import (
	"context"
	"fmt"
	"os"
	"time"

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
	"github.com/Azure/azure-cleanroom/cleanroom-operator/internal/crypto"
)

const (
	ccfMemberFinalizerName = "cleanroom.azure.com/ccfmember-finalizer"
	defaultCgsImage        = "mcr.microsoft.com/azurecleanroom/cgs-client:11.0.0"
	defaultCgsUIImage      = "mcr.microsoft.com/azurecleanroom/cgs-ui:11.0.0"
	cgsClientPort          = 8080
	cgsUIPort              = 6300
	memberReadyTimeout     = 5 * time.Minute
	memberRequeueDelay     = 15 * time.Second
	memberCertsMountPath   = "/mnt/member-certs"
	memberCertsVolumeName  = "member-certs"
)

// CcfMemberReconciler reconciles CcfMember objects.
type CcfMemberReconciler struct {
	ctrlclient.Client
	Scheme    *runtime.Scheme
	CgsClient *client.CgsClient
	Recorder  record.EventRecorder
}

// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=ccfmembers,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=ccfmembers/status,verbs=get;update;patch
// +kubebuilder:rbac:groups=cleanroom.azure.com,resources=ccfmembers/finalizers,verbs=update
// +kubebuilder:rbac:groups="",resources=secrets,verbs=get;list;watch;create;update;patch
// +kubebuilder:rbac:groups="",resources=services,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=apps,resources=deployments,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups="",resources=events,verbs=create;patch

// Reconcile handles the reconciliation loop for CcfMember.
func (r *CcfMemberReconciler) Reconcile(
	ctx context.Context,
	req ctrl.Request,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	var member v1alpha1.CcfMember
	if err := r.Get(
		ctx, req.NamespacedName, &member,
	); err != nil {
		if apierrors.IsNotFound(err) {
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"fetching CcfMember: %w", err,
		)
	}

	ctx = resolveTraceContext(
		ctx, &member, member.Status.TraceParent,
	)
	ctx, span := startSpan(
		ctx, "CcfMember", "Reconcile", member.Name,
	)
	defer span.End()

	// Handle deletion.
	if !member.DeletionTimestamp.IsZero() {
		return r.reconcileDelete(ctx, &member)
	}

	// Ensure finalizer is set.
	if !controllerutil.ContainsFinalizer(
		&member, ccfMemberFinalizerName,
	) {
		controllerutil.AddFinalizer(
			&member, ccfMemberFinalizerName,
		)
		if err := r.Update(ctx, &member); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"adding finalizer: %w", err,
			)
		}
		return ctrl.Result{Requeue: true}, nil
	}

	// Route based on phase.
	switch member.Status.Phase {
	case "", v1alpha1.CcfMemberPhasePending:
		return r.reconcileCerts(ctx, &member)

	case v1alpha1.CcfMemberPhaseCertsGenerated:
		return r.reconcileGovernanceClient(ctx, &member)

	case v1alpha1.CcfMemberPhaseGovClientDeployed:
		// The Deployment was created with all CCF env
		// vars baked in. Proceed directly to activation.
		return r.reconcileActivation(ctx, &member)

	case v1alpha1.CcfMemberPhaseActivating:
		return r.reconcileActivation(ctx, &member)

	case v1alpha1.CcfMemberPhaseActive:
		return r.ensureGovernanceClient(ctx, &member)

	case v1alpha1.CcfMemberPhaseFailed:
		if member.Generation !=
			member.Status.ObservedGeneration ||
			member.Annotations[retryAnnotation] != "" {
			if member.Annotations[retryAnnotation] != "" {
				delete(
					member.Annotations,
					retryAnnotation,
				)
			}
			if err := r.Update(
				ctx, &member,
			); err != nil {
				return ctrl.Result{}, fmt.Errorf(
					"clearing retry annotation: %w",
					err,
				)
			}
			r.event(
				&member,
				corev1.EventTypeNormal,
				"Retrying",
				"Retry requested via annotation",
			)
			// Set transitional phase immediately so
			// the parent controller does not see a
			// stale Failed phase after the retry
			// annotation has been cleared.
			member.Status.Phase =
				v1alpha1.CcfMemberPhasePending
			meta.RemoveStatusCondition(
				&member.Status.Conditions,
				v1alpha1.ConditionTypeCertsReady,
			)
			meta.RemoveStatusCondition(
				&member.Status.Conditions,
				v1alpha1.ConditionTypeGovClientReady,
			)
			meta.RemoveStatusCondition(
				&member.Status.Conditions,
				v1alpha1.ConditionTypeMemberActivated,
			)
			if err := r.Status().Update(
				ctx, &member,
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
			"phase", member.Status.Phase)
		return ctrl.Result{
			RequeueAfter: memberRequeueDelay,
		}, nil
	}
}

// reconcileCerts generates identity certs and stores them in a
// Secret.
func (r *CcfMemberReconciler) reconcileCerts(
	ctx context.Context,
	member *v1alpha1.CcfMember,
) (ctrl.Result, error) {
	ctx, span := startSpan(
		ctx, "CcfMember", "GenerateCerts", member.Name,
	)
	defer span.End()

	log := ctrllog.FromContext(ctx)
	secretName := member.Spec.SecretRef.Name

	// Check if Secret already exists.
	var existing corev1.Secret
	err := r.Get(ctx, types.NamespacedName{
		Name:      secretName,
		Namespace: member.Namespace,
	}, &existing)
	if err == nil {
		// Secret exists — certs already generated.
		log.Info("Secret already exists, skipping cert gen",
			"secret", secretName)

		// Adopt the Secret if it has no owner reference
		// to this CcfMember (happens when the Secret was
		// pre-created via `dev restore`).
		if !hasOwnerReference(
			&existing, member,
		) {
			if err := controllerutil.SetControllerReference(
				member, &existing, r.Scheme,
			); err != nil {
				return ctrl.Result{}, fmt.Errorf(
					"adopting secret %s: %w",
					secretName, err,
				)
			}
			if err := r.Update(
				ctx, &existing,
			); err != nil {
				return ctrl.Result{}, fmt.Errorf(
					"updating owner ref on secret %s: %w",
					secretName, err,
				)
			}
			log.Info(
				"Adopted pre-existing Secret",
				"secret", secretName,
			)
		}

		return r.transitionToCertsGenerated(ctx, member)
	}
	if !apierrors.IsNotFound(err) {
		return ctrl.Result{}, fmt.Errorf(
			"checking secret %s: %w", secretName, err,
		)
	}

	// Generate certs.
	log.Info("Generating member certs",
		"identifier", member.Spec.Identifier)
	certs, err := crypto.GenerateMemberCerts(
		member.Spec.Identifier,
		member.Spec.GenerateEncryptionKey,
	)
	if err != nil {
		if sErr := r.setFailed(ctx, member,
			"CertGenerationFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		r.event(member, corev1.EventTypeWarning,
			"CertGenerationFailed",
			fmt.Sprintf("Failed to generate certs: %s",
				err.Error()))
		return ctrl.Result{
			RequeueAfter: memberRequeueDelay,
		}, nil
	}

	// Create Secret.
	secretLabels := map[string]string{
		"cleanroom.azure.com/member-certs": "true",
		"cleanroom.azure.com/member":       member.Name,
	}
	if envName, ok :=
		member.Labels["cleanroom.azure.com/environment"]; ok {
		secretLabels["cleanroom.azure.com/environment"] =
			envName
	}
	secret := &corev1.Secret{
		ObjectMeta: metav1.ObjectMeta{
			Name:      secretName,
			Namespace: member.Namespace,
			Labels:    secretLabels,
		},
		Type: corev1.SecretTypeOpaque,
		Data: map[string][]byte{
			v1alpha1.SecretKeyCert:       certs.CertPEM,
			v1alpha1.SecretKeyPrivateKey: certs.PrivateKeyPEM,
		},
	}
	if certs.EncryptionPublicKeyPEM != nil {
		secret.Data[v1alpha1.SecretKeyEncPublicKey] =
			certs.EncryptionPublicKeyPEM
		secret.Data[v1alpha1.SecretKeyEncPrivateKey] =
			certs.EncryptionPrivateKeyPEM
	}

	// Set owner reference so the Secret is cleaned up when
	// the CcfMember is deleted.
	if err := controllerutil.SetControllerReference(
		member, secret, r.Scheme,
	); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"setting owner reference on secret: %w", err,
		)
	}

	if err := r.Create(ctx, secret); err != nil {
		if apierrors.IsAlreadyExists(err) {
			log.Info("Secret created concurrently",
				"secret", secretName)
		} else {
			return ctrl.Result{}, fmt.Errorf(
				"creating secret %s: %w", secretName, err,
			)
		}
	}

	r.event(member, corev1.EventTypeNormal,
		"CertsGenerated",
		"Member identity certs generated")

	return r.transitionToCertsGenerated(ctx, member)
}

func (r *CcfMemberReconciler) transitionToCertsGenerated(
	ctx context.Context,
	member *v1alpha1.CcfMember,
) (ctrl.Result, error) {
	member.Status.Phase = v1alpha1.CcfMemberPhaseCertsGenerated
	member.Status.ObservedGeneration = member.Generation
	member.Status.SecretRef = member.Spec.SecretRef.Name
	member.Status.TraceParent,
		member.Status.LastOperationTraceId =
		saveTrace(ctx)
	meta.SetStatusCondition(
		&member.Status.Conditions,
		metav1.Condition{
			Type:    v1alpha1.ConditionTypeCertsReady,
			Status:  metav1.ConditionTrue,
			Reason:  "CertsGenerated",
			Message: "Member certs stored in Secret",
		},
	)
	if err := r.Status().Update(ctx, member); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after cert gen: %w", err,
		)
	}
	return ctrl.Result{Requeue: true}, nil
}

// ensureGovernanceClient checks that the governance client
// Deployment still exists for an Active member, recreates
// it if deleted, and verifies the CCF env vars still match
// the current CcfNetwork (delete + recreate on mismatch).
func (r *CcfMemberReconciler) ensureGovernanceClient(
	ctx context.Context,
	member *v1alpha1.CcfMember,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)
	deployName := r.govClientDeploymentName(member)

	// Look up the CcfNetwork for current endpoint/cert.
	var network *v1alpha1.CcfNetwork
	if member.Spec.NetworkRef != "" {
		var net v1alpha1.CcfNetwork
		if err := r.Get(ctx, ctrlclient.ObjectKey{
			Name:      member.Spec.NetworkRef,
			Namespace: member.Namespace,
		}, &net); err != nil {
			if !apierrors.IsNotFound(err) {
				return ctrl.Result{}, fmt.Errorf(
					"fetching CcfNetwork: %w", err,
				)
			}
		} else {
			// Skip stale network from previous
			// Environment incarnation.
			if isStaleResource(
				ctx, controllerOwnerUID(member),
				&net, "CcfNetwork",
				member.Spec.NetworkRef,
			) || !net.DeletionTimestamp.IsZero() {
				return ctrl.Result{
					RequeueAfter: memberRequeueDelay,
				}, nil
			}
			network = &net
		}
	}

	var deploy appsv1.Deployment
	err := r.Get(ctx, types.NamespacedName{
		Name:      deployName,
		Namespace: member.Namespace,
	}, &deploy)

	if apierrors.IsNotFound(err) {
		log.Info(
			"Governance client Deployment missing, "+
				"recreating",
			"deployment", deployName,
		)
		if err := r.createGovClientDeployment(
			ctx, member, network,
		); err != nil {
			if apierrors.IsInvalid(err) ||
				apierrors.IsForbidden(err) {
				if sErr := r.setFailed(
					ctx, member,
					"DeploymentCreateRejected",
					fmt.Errorf(
						"recreating deployment: %w",
						err,
					),
				); sErr != nil {
					return ctrl.Result{}, sErr
				}
				return ctrl.Result{}, nil
			}
			return ctrl.Result{}, fmt.Errorf(
				"recreating deployment: %w", err,
			)
		}

		if err := r.createGovClientService(
			ctx, member,
		); err != nil && !apierrors.IsAlreadyExists(err) {
			if apierrors.IsInvalid(err) ||
				apierrors.IsForbidden(err) {
				if sErr := r.setFailed(
					ctx, member,
					"ServiceCreateRejected",
					fmt.Errorf(
						"recreating service: %w",
						err,
					),
				); sErr != nil {
					return ctrl.Result{}, sErr
				}
				return ctrl.Result{}, nil
			}
			return ctrl.Result{}, fmt.Errorf(
				"recreating service: %w", err,
			)
		}

		r.event(member, corev1.EventTypeNormal,
			"GovernanceClientRecreated",
			"Governance client Deployment recreated")
		return ctrl.Result{
			RequeueAfter: memberRequeueDelay,
		}, nil
	}
	if err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"checking deployment %s: %w", deployName, err,
		)
	}

	// Wait for readiness.
	if deploy.Status.ReadyReplicas < 1 {
		log.Info(
			"Waiting for governance client to be ready",
			"deployment", deployName,
		)
		return ctrl.Result{
			RequeueAfter: memberRequeueDelay,
		}, nil
	}

	// If the CCF endpoint or service cert changed,
	// delete the Deployment so it gets recreated with
	// the new values on the next reconcile.
	if network != nil &&
		network.Status.Endpoint != "" &&
		network.Status.ServiceCert != "" &&
		!r.deploymentCcfEnvVarsMatch(
			&deploy, network,
		) {
		log.Info(
			"CCF endpoint/cert changed, "+
				"recreating Deployment",
			"deployment", deployName)
		if err := r.Delete(
			ctx, &deploy,
		); err != nil {
			return ctrl.Result{}, fmt.Errorf(
				"deleting deployment for "+
					"recreate: %w", err,
			)
		}
		return ctrl.Result{Requeue: true}, nil
	}

	return ctrl.Result{}, nil
}

// deploymentCcfEnvVarsMatch checks whether the cgs-client
// container in the Deployment has CCF env vars matching
// the current CcfNetwork status.
func (r *CcfMemberReconciler) deploymentCcfEnvVarsMatch(
	deploy *appsv1.Deployment,
	network *v1alpha1.CcfNetwork,
) bool {
	for _, c := range deploy.Spec.Template.Spec.Containers {
		if c.Name != "cgs-client" {
			continue
		}
		endpoint := ""
		serviceCert := ""
		for _, e := range c.Env {
			switch e.Name {
			case "CGS_CLIENT_CCF_ENDPOINT":
				endpoint = e.Value
			case "CGS_CLIENT_SERVICE_CERT":
				serviceCert = e.Value
			}
		}
		return endpoint == network.Status.Endpoint &&
			serviceCert == network.Status.ServiceCert
	}
	return false
}

// reconcileGovernanceClient waits for the CcfNetwork to be
// ready (Running for operator, Open for non-operator),
// then creates the Deployment and Service with all CCF env
// vars baked in from the start.
func (r *CcfMemberReconciler) reconcileGovernanceClient(
	ctx context.Context,
	member *v1alpha1.CcfMember,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)
	deployName := r.govClientDeploymentName(member)
	svcName := r.govClientServiceName(member)

	// Wait for CcfNetwork so the Deployment is created
	// with all env vars from the start.
	var network *v1alpha1.CcfNetwork
	if member.Spec.NetworkRef != "" {
		var net v1alpha1.CcfNetwork
		if err := r.Get(ctx, ctrlclient.ObjectKey{
			Name:      member.Spec.NetworkRef,
			Namespace: member.Namespace,
		}, &net); err != nil {
			if apierrors.IsNotFound(err) {
				log.Info(
					"CcfNetwork not found, waiting",
					"network",
					member.Spec.NetworkRef)
				return ctrl.Result{
					RequeueAfter: memberRequeueDelay,
				}, nil
			}
			return ctrl.Result{}, fmt.Errorf(
				"fetching CcfNetwork: %w", err,
			)
		}
		// Wait if the CcfNetwork belongs to a different
		// Environment (stale resource from a previous
		// incarnation not yet garbage-collected).
		if isStaleResource(
			ctx, controllerOwnerUID(member),
			&net, "CcfNetwork",
			member.Spec.NetworkRef,
		) {
			return ctrl.Result{
				RequeueAfter: memberRequeueDelay,
			}, nil
		}
		// Also wait if the CcfNetwork is being deleted.
		if !net.DeletionTimestamp.IsZero() {
			log.Info(
				"CcfNetwork is being deleted, waiting",
				"network", member.Spec.NetworkRef)
			return ctrl.Result{
				RequeueAfter: memberRequeueDelay,
			}, nil
		}
		if net.Status.Endpoint == "" ||
			net.Status.ServiceCert == "" {
			log.Info(
				"CcfNetwork not ready, waiting",
				"network", member.Spec.NetworkRef,
				"phase", net.Status.Phase)
			return ctrl.Result{
				RequeueAfter: memberRequeueDelay,
			}, nil
		}
		// Operator members need Running or Open;
		// non-operator members need Open.
		if !member.Spec.IsOperator &&
			net.Status.Phase !=
				v1alpha1.CcfNetworkPhaseOpen {
			log.Info(
				"Waiting for network Open",
				"network", member.Spec.NetworkRef,
				"phase", net.Status.Phase)
			return ctrl.Result{
				RequeueAfter: memberRequeueDelay,
			}, nil
		}
		network = &net
	}

	// Check if Deployment exists.
	var deploy appsv1.Deployment
	err := r.Get(ctx, types.NamespacedName{
		Name:      deployName,
		Namespace: member.Namespace,
	}, &deploy)

	if apierrors.IsNotFound(err) {
		ctx, span := startSpan(
			ctx, "CcfMember", "DeployGovClient",
			member.Name,
		)
		defer span.End()

		log = ctrllog.FromContext(ctx)
		log.Info("Creating governance client Deployment",
			"deployment", deployName)

		if err := r.createGovClientDeployment(
			ctx, member, network,
		); err != nil {
			if sErr := r.setFailed(ctx, member,
				"DeploymentFailed", err); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{
				RequeueAfter: memberRequeueDelay,
			}, nil
		}

		if err := r.createGovClientService(
			ctx, member,
		); err != nil && !apierrors.IsAlreadyExists(err) {
			if sErr := r.setFailed(ctx, member,
				"ServiceFailed", err); sErr != nil {
				return ctrl.Result{}, sErr
			}
			return ctrl.Result{
				RequeueAfter: memberRequeueDelay,
			}, nil
		}

		r.event(member, corev1.EventTypeNormal,
			"GovernanceClientCreated",
			"Governance client Deployment and "+
				"Service created")
	} else if err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"checking deployment %s: %w", deployName, err,
		)
	}

	// Check Deployment readiness.
	if deploy.Status.ReadyReplicas < 1 {
		log.Info("Waiting for governance client to be ready",
			"deployment", deployName)
		return ctrl.Result{
			RequeueAfter: memberRequeueDelay,
		}, nil
	}

	// Set status.
	endpoint := fmt.Sprintf(
		"http://%s.%s.svc.cluster.local:%d",
		svcName, member.Namespace, cgsClientPort,
	)
	member.Status.Phase =
		v1alpha1.CcfMemberPhaseGovClientDeployed
	member.Status.GovernanceClientEndpoint = endpoint
	meta.SetStatusCondition(
		&member.Status.Conditions,
		metav1.Condition{
			Type:    v1alpha1.ConditionTypeGovClientReady,
			Status:  metav1.ConditionTrue,
			Reason:  "DeploymentReady",
			Message: "Governance client is running",
		},
	)
	if err := r.Status().Update(ctx, member); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after gov client: %w", err,
		)
	}

	return ctrl.Result{Requeue: true}, nil
}

func (r *CcfMemberReconciler) createGovClientDeployment(
	ctx context.Context,
	member *v1alpha1.CcfMember,
	network *v1alpha1.CcfNetwork,
) error {
	image := member.Spec.CgsImage
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

	labels := r.govClientLabels(member)
	replicas := int32(1)
	certMountPath := memberCertsMountPath

	envVars := []corev1.EnvVar{
		{
			Name: "CGS_CLIENT_SIGNING_CERT_PATH",
			Value: fmt.Sprintf(
				"%s/%s",
				certMountPath,
				v1alpha1.SecretKeyCert,
			),
		},
		{
			Name: "CGS_CLIENT_SIGNING_KEY_PATH",
			Value: fmt.Sprintf(
				"%s/%s",
				certMountPath,
				v1alpha1.SecretKeyPrivateKey,
			),
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
				Value: "cgs-client",
			},
		)
	}

	deploy := &appsv1.Deployment{
		ObjectMeta: metav1.ObjectMeta{
			Name:      r.govClientDeploymentName(member),
			Namespace: member.Namespace,
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
					Volumes: []corev1.Volume{
						{
							Name: memberCertsVolumeName,
							VolumeSource: corev1.VolumeSource{
								Secret: &corev1.SecretVolumeSource{
									SecretName: member.Spec.SecretRef.Name,
								},
							},
						},
					},
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
							VolumeMounts: []corev1.VolumeMount{
								{
									Name:      memberCertsVolumeName,
									MountPath: certMountPath,
									ReadOnly:  true,
								},
							},
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
		member, deploy, r.Scheme,
	); err != nil {
		return fmt.Errorf(
			"setting owner reference on deployment: %w", err,
		)
	}

	return r.Create(ctx, deploy)
}

func (r *CcfMemberReconciler) createGovClientService(
	ctx context.Context,
	member *v1alpha1.CcfMember,
) error {
	labels := r.govClientLabels(member)
	svc := &corev1.Service{
		ObjectMeta: metav1.ObjectMeta{
			Name:      r.govClientServiceName(member),
			Namespace: member.Namespace,
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
		member, svc, r.Scheme,
	); err != nil {
		return fmt.Errorf(
			"setting owner reference on service: %w", err,
		)
	}

	return r.Create(ctx, svc)
}

// reconcileActivation activates the member once the
// governance client is ready. The Deployment already has
// all CCF env vars baked in from reconcileGovernanceClient.
func (r *CcfMemberReconciler) reconcileActivation(
	ctx context.Context,
	member *v1alpha1.CcfMember,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	endpoint := member.Status.GovernanceClientEndpoint
	if endpoint == "" {
		log.Info("Waiting for governance client endpoint")
		return ctrl.Result{
			RequeueAfter: memberRequeueDelay,
		}, nil
	}

	// Wait for the cgs-client to be ready.
	if err := r.CgsClient.WaitForReady(
		ctx, endpoint,
	); err != nil {
		log.Info("CGS sidecar not ready yet, waiting",
			"member", member.Name)
		return ctrl.Result{
			RequeueAfter: memberRequeueDelay,
		}, nil
	}

	// Activate the member (ack state digest).
	ctx, span := startSpan(
		ctx, "CcfMember", "Activate", member.Name,
	)
	defer span.End()

	log = ctrllog.FromContext(ctx)
	log.Info("Activating member",
		"identifier", member.Spec.Identifier,
		"endpoint", endpoint)

	member.Status.Phase = v1alpha1.CcfMemberPhaseActivating
	_ = r.Status().Update(ctx, member)

	if err := r.CgsClient.ActivateMember(
		ctx, endpoint,
	); err != nil {
		if sErr := r.setFailed(ctx, member,
			"ActivationFailed", err); sErr != nil {
			return ctrl.Result{}, sErr
		}
		r.event(member, corev1.EventTypeWarning,
			"ActivationFailed",
			fmt.Sprintf("Failed to activate member: %s",
				err.Error()))
		return ctrl.Result{
			RequeueAfter: memberRequeueDelay,
		}, nil
	}

	member.Status.Phase = v1alpha1.CcfMemberPhaseActive
	member.Status.TraceParent = ""
	meta.SetStatusCondition(
		&member.Status.Conditions,
		metav1.Condition{
			Type:    v1alpha1.ConditionTypeMemberActivated,
			Status:  metav1.ConditionTrue,
			Reason:  "Activated",
			Message: "Member activated in CCF consortium",
		},
	)
	if err := r.Status().Update(ctx, member); err != nil {
		return ctrl.Result{}, fmt.Errorf(
			"updating status after activation: %w", err,
		)
	}

	r.event(member, corev1.EventTypeNormal,
		"Activated", "Member activated in CCF consortium")

	return ctrl.Result{}, nil
}

func (r *CcfMemberReconciler) reconcileDelete(
	ctx context.Context,
	member *v1alpha1.CcfMember,
) (ctrl.Result, error) {
	log := ctrllog.FromContext(ctx)

	if !controllerutil.ContainsFinalizer(
		member, ccfMemberFinalizerName,
	) {
		return ctrl.Result{}, nil
	}

	log.Info("Deleting CcfMember resources",
		"name", member.Name)

	// Owned resources (Secret, Deployment, Service) are
	// garbage-collected via ownerReferences.

	controllerutil.RemoveFinalizer(
		member, ccfMemberFinalizerName,
	)
	if err := r.Update(ctx, member); err != nil {
		if apierrors.IsNotFound(err) {
			return ctrl.Result{}, nil
		}
		return ctrl.Result{}, fmt.Errorf(
			"removing finalizer: %w", err,
		)
	}

	log.Info("CcfMember deleted", "name", member.Name)
	return ctrl.Result{}, nil
}

func (r *CcfMemberReconciler) setFailed(
	ctx context.Context,
	member *v1alpha1.CcfMember,
	reason string,
	err error,
) error {
	log := ctrllog.FromContext(ctx)
	log.Error(err, "Operation failed: "+reason)
	recordContextError(ctx, err)

	message := truncateMessage(
		err.Error(), maxConditionMessageLen,
	)
	member.Status.Phase = v1alpha1.CcfMemberPhaseFailed
	member.Status.ObservedGeneration = member.Generation
	member.Status.TraceParent = ""
	meta.SetStatusCondition(
		&member.Status.Conditions,
		metav1.Condition{
			Type:    v1alpha1.ConditionTypeReady,
			Status:  metav1.ConditionFalse,
			Reason:  reason,
			Message: message,
		},
	)
	if err := r.Status().Update(ctx, member); err != nil {
		return fmt.Errorf(
			"updating failed status: %w", err,
		)
	}
	return nil
}

func (r *CcfMemberReconciler) govClientDeploymentName(
	member *v1alpha1.CcfMember,
) string {
	return fmt.Sprintf("cgs-%s", member.Name)
}

func (r *CcfMemberReconciler) govClientServiceName(
	member *v1alpha1.CcfMember,
) string {
	return fmt.Sprintf("cgs-%s", member.Name)
}

func (r *CcfMemberReconciler) govClientLabels(
	member *v1alpha1.CcfMember,
) map[string]string {
	return map[string]string{
		"app.kubernetes.io/name":       "cgs-client",
		"app.kubernetes.io/instance":   member.Name,
		"app.kubernetes.io/managed-by": "cleanroom-operator",
		"cleanroom.azure.com/member":   member.Name,
	}
}

// event emits a Kubernetes event on the CcfMember resource.
func (r *CcfMemberReconciler) event(
	member *v1alpha1.CcfMember,
	eventType string,
	reason string,
	message string,
) {
	if r.Recorder != nil {
		r.Recorder.Event(
			member, eventType, reason, message,
		)
	}
}

// SetupWithManager sets up the controller with the Manager.
func (r *CcfMemberReconciler) SetupWithManager(
	mgr ctrl.Manager,
) error {
	return ctrl.NewControllerManagedBy(mgr).
		For(&v1alpha1.CcfMember{}).
		Owns(&corev1.Secret{}).
		Owns(&appsv1.Deployment{}).
		Owns(&corev1.Service{}).
		Complete(r)
}
