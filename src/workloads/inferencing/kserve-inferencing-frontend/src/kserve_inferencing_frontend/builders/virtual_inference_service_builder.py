import logging
from typing import Optional

from frontend_internal.models.input_models import GovernanceSettings, TelemetrySettings
from kubernetes.client import models as k8smodels

from ..builders.i_inference_service_builder import CleanRoomInferencingApplication
from ..builders.inference_service_builder import InferenceServiceBuilder
from ..config.configuration import CleanroomSettings

logger = logging.getLogger("virtual_inference_service_builder")


class VirtualInferenceServiceBuilder(InferenceServiceBuilder):
    def __init__(
        self,
        cleanroom_settings: CleanroomSettings,
        telemetry_settings: TelemetrySettings,
        governance_settings: Optional[GovernanceSettings],
    ):
        super().__init__(cleanroom_settings, telemetry_settings, governance_settings)

        # The k8s environment is not a confidential environment.
        self._allow_all = True

    def Build(self) -> CleanRoomInferencingApplication:
        app = super().Build()

        # Override the governance sidecar image to use the virtual (non-confidential) variant.
        versions_registry_tag = self._cleanroom_settings.versions_document.split(":")[
            -1
        ]
        virtual_governance_image = (
            f"{self._cleanroom_settings.registry_url}"
            f"/ccr-governance-virtual:{versions_registry_tag}"
        )

        if app.spec.predictor.initContainers:
            for container in app.spec.predictor.initContainers:
                if container.name == "ccr-governance":
                    container.image = virtual_governance_image
                    logger.info(
                        f"Overriding governance image to: {virtual_governance_image}"
                    )
                    insecure_env = k8smodels.V1EnvVar(
                        name="INSECURE_VIRTUAL_DIR",
                        value="/app/cvm/insecure-virtual/",
                    )
                    if container.env is None:
                        container.env = []
                    container.env.append(insecure_env)
                    break

        # The cvm-attestation-agent sidecar requires TPM access which is not
        # available in a non-confidential environment. Remove the tpmrm0 volume
        # mount from the sidecar.
        if app.spec.predictor.initContainers:
            for container in app.spec.predictor.initContainers:
                if (
                    container.name == "cvm-attestation-agent"
                    and container.volume_mounts is not None
                ):
                    container.volume_mounts = [
                        vm for vm in container.volume_mounts if vm.name != "tpmrm0"
                    ]
                    logger.info(
                        "Removed tpmrm0 volume mount from cvm-attestation-agent"
                    )
                    break

        # Update mount propagation and security context on the
        # serving container for Kind cluster compatibility.
        assert app.spec.predictor.containers is not None
        serving_container = next(
            c for c in app.spec.predictor.containers if c.name == "kserve-container"
        )
        if serving_container.volume_mounts:
            for vm in serving_container.volume_mounts:
                if vm.name == "remotemounts":
                    vm.mount_propagation = "Bidirectional"

        if serving_container.security_context is None:
            serving_container.security_context = k8smodels.V1SecurityContext(
                privileged=True,
                allow_privilege_escalation=True,
            )
        else:
            serving_container.security_context.privileged = True
            serving_container.security_context.allow_privilege_escalation = True

        if app.spec.predictor.initContainers:
            for container in app.spec.predictor.containers:
                if container.volume_mounts is not None:
                    for volume_mount in container.volume_mounts:
                        if volume_mount.name == "remotemounts":
                            volume_mount.mount_propagation = "Bidirectional"

        if app.spec.predictor.initContainers:
            for container in app.spec.predictor.initContainers:
                if container.volume_mounts is not None:
                    for volume_mount in container.volume_mounts:
                        if volume_mount.name == "remotemounts":
                            volume_mount.mount_propagation = "Bidirectional"

        # The cvm-attestation-agent sidecar will not work in a non-confidential environment.
        if app.spec.predictor.initContainers:
            app.spec.predictor.initContainers = [
                c
                for c in app.spec.predictor.initContainers
                if c.name != "cvm-attestation-agent"
            ]

        if app.spec.transformer and app.spec.transformer.initContainers:
            for container in app.spec.transformer.initContainers:
                if container.volume_mounts is not None:
                    for volume_mount in container.volume_mounts:
                        if volume_mount.name == "remotemounts":
                            volume_mount.mount_propagation = "Bidirectional"

        if app.spec.transformer and app.spec.transformer.containers:
            for container in app.spec.transformer.containers:
                if container.volume_mounts is not None:
                    for volume_mount in container.volume_mounts:
                        if volume_mount.name == "remotemounts":
                            volume_mount.mount_propagation = "Bidirectional"

        return app
