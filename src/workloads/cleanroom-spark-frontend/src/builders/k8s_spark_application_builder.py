import logging

from src.builders.i_spark_application_builder import CleanRoomSparkApplication
from src.builders.spark_application_builder import Sidecar, SparkApplicationBuilder
from src.config.configuration import CleanroomSettings
from src.models.input_models import GovernanceSettings

logger = logging.getLogger("k8s_spark_application_builder")


class K8sSparkApplicationBuilder(SparkApplicationBuilder):
    def __init__(self, cleanroom_settings: CleanroomSettings):
        super().__init__(cleanroom_settings)

    def _get_ccr_governance_sidecar(
        self,
        contract_id: str,
        governance_settings: GovernanceSettings,
        telemetry_mount_path: str,
    ) -> Sidecar:

        sidecar = super()._get_ccr_governance_sidecar(
            contract_id, governance_settings, telemetry_mount_path
        )

        versions_registry_tag = self._cleanroom_settings.versions_document.split(":")[
            -1
        ]
        sidecar.container.image = f"{self._get_container_registry_url()}/ccr-governance-virtual:{versions_registry_tag}"
        logger.info(f"Overriding governance image to: {sidecar.container.image}")
        return sidecar

    def WithPolicy(
        self, policy_file: str, debug_mode: bool = False, allow_all: bool = False
    ):
        super().WithPolicy(policy_file, debug_mode, allow_all)

        # The k8s environment is not a confidential environment.
        self._allow_all = True
        return self

    def Build(self) -> CleanRoomSparkApplication:
        app = super().Build()

        # The ccr-attestation sidecar will not work in a non-confidential environment.
        app.spec.driver.initContainers = [
            c for c in app.spec.driver.initContainers if c.name != "ccr-attestation"
        ]
        app.spec.executor.initContainers = [
            c for c in app.spec.executor.initContainers if c.name != "ccr-attestation"
        ]

        # Update the mount_propagation property for blobfuse backed volumes to Bidirectional.
        # This is required for the files to be visible in the driver/executor containers in a
        # Kind cluster.
        for vm in app.spec.driver.volumeMounts:
            if vm.name == "remotemounts":
                vm.mount_propagation = "Bidirectional"

        for vm in app.spec.executor.volumeMounts:
            if vm.name == "remotemounts":
                vm.mount_propagation = "Bidirectional"

        for container in app.spec.driver.initContainers:
            for volume_mount in container.volume_mounts:
                if volume_mount.name == "remotemounts":
                    volume_mount.mount_propagation = "Bidirectional"

        for container in app.spec.executor.initContainers:
            for volume_mount in container.volume_mounts:
                if volume_mount.name == "remotemounts":
                    volume_mount.mount_propagation = "Bidirectional"
        return app
