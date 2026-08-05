import logging
from typing import Optional

from kubernetes.client import models as k8smodels

from frontend_internal.models.input_models import GovernanceSettings, TelemetrySettings

from ..builders.i_inference_service_builder import CleanRoomInferencingApplication
from ..builders.inference_service_builder import InferenceServiceBuilder
from ..config.configuration import CleanroomSettings
from ..utilities.constants import Constants
from ..utilities.container_utils import (
    find_container,
    find_container_optional,
    remove_containers_by_name,
    remove_volume_mounts_by_name,
    set_mount_propagation,
)

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

    def _get_virtual_governance_image(self) -> str:
        """Build the virtual (non-confidential) governance sidecar image reference."""
        tag = self._cleanroom_settings.versions_document.split(":")[-1]
        return f"{self._cleanroom_settings.registry_url}/ccr-governance-virtual:{tag}"

    def _customize_app(self, app: CleanRoomInferencingApplication):
        # Override the governance sidecar image to use the virtual (non-confidential) variant.
        virtual_governance_image = self._get_virtual_governance_image()

        gov_container = find_container_optional(
            app.spec.predictor.initContainers,
            Constants.CCR_GOVERNANCE_CONTAINER,
        )
        if gov_container:
            gov_container.image = virtual_governance_image
            logger.info(f"Overriding governance image to: {virtual_governance_image}")
            if gov_container.env is None:
                gov_container.env = []
            gov_container.env.append(
                k8smodels.V1EnvVar(
                    name="INSECURE_VIRTUAL_DIR",
                    value="/app/cvm/insecure-virtual/",
                )
            )

        # Remove tpmrm0 volume mount from cvm-attestation-agent (no TPM in virtual env).
        agent = find_container_optional(
            app.spec.predictor.initContainers,
            Constants.CVM_ATTESTATION_AGENT_CONTAINER,
        )
        if agent:
            remove_volume_mounts_by_name(agent, Constants.TPMRM0_VOLUME)
            logger.info(
                f"Removed {Constants.TPMRM0_VOLUME} volume mount from "
                f"{Constants.CVM_ATTESTATION_AGENT_CONTAINER}"
            )

        # Set serving container to privileged with Bidirectional mount propagation.
        serving_container = find_container(
            app.spec.predictor.containers, Constants.KSERVE_CONTAINER
        )
        if serving_container.security_context is None:
            serving_container.security_context = k8smodels.V1SecurityContext(
                privileged=True,
                allow_privilege_escalation=True,
            )
        else:
            serving_container.security_context.privileged = True
            serving_container.security_context.allow_privilege_escalation = True

        # Virtual environment: Bidirectional mount propagation on all containers.
        set_mount_propagation(
            app.spec.predictor.containers,
            Constants.REMOTE_MOUNTS_VOLUME,
            "Bidirectional",
        )
        set_mount_propagation(
            app.spec.predictor.initContainers,
            Constants.REMOTE_MOUNTS_VOLUME,
            "Bidirectional",
        )

        # Remove cvm-attestation-agent (not functional in non-confidential env).
        if app.spec.predictor.initContainers:
            app.spec.predictor.initContainers = remove_containers_by_name(
                app.spec.predictor.initContainers,
                Constants.CVM_ATTESTATION_AGENT_CONTAINER,
            )

        # Transformer mount propagation (if present).
        if app.spec.transformer:
            set_mount_propagation(
                app.spec.transformer.initContainers,
                Constants.REMOTE_MOUNTS_VOLUME,
                "Bidirectional",
            )
            set_mount_propagation(
                app.spec.transformer.containers,
                Constants.REMOTE_MOUNTS_VOLUME,
                "Bidirectional",
            )
