import logging

from ..builders.i_spark_application_builder import CleanRoomSparkApplication
from ..builders.spark_application_builder import SparkApplicationBuilder
from ..config.configuration import CleanroomSettings, TelemetrySettings
from ..models.input_models import GovernanceSettings

logger = logging.getLogger("virtual_spark_application_builder")


class VirtualSparkApplicationBuilder(SparkApplicationBuilder):
    def __init__(
        self,
        cleanroom_settings: CleanroomSettings,
        telemetry_settings: TelemetrySettings,
        governance_settings: GovernanceSettings,
    ):
        super().__init__(cleanroom_settings, telemetry_settings, governance_settings)

    def WithPolicy(
        self, policy_file: str, debug_mode: bool = False, allow_all: bool = False
    ):
        super().WithPolicy(policy_file, debug_mode, allow_all)

        # The k8s environment is not a confidential environment.
        self._allow_all = True
        return self

    def Build(self) -> CleanRoomSparkApplication:
        app = super().Build()

        # Override the governance sidecar image to use the virtual (non-confidential) variant.
        versions_registry_tag = self._cleanroom_settings.versions_document.split(":")[
            -1
        ]
        virtual_governance_image = f"{self._cleanroom_settings.registry_url}/ccr-governance-virtual:{versions_registry_tag}"

        for container in app.spec.driver.initContainers:
            if container.name == "ccr-governance":
                container.image = virtual_governance_image
                logger.info(
                    f"Overriding driver governance image to: {virtual_governance_image}"
                )
                break

        for container in app.spec.executor.initContainers:
            if container.name == "ccr-governance":
                container.image = virtual_governance_image
                logger.info(
                    f"Overriding executor governance image to: {virtual_governance_image}"
                )
                break

        # The skr sidecar will not work in a non-confidential environment.
        app.spec.driver.initContainers = [
            c for c in app.spec.driver.initContainers if c.name != "skr"
        ]
        app.spec.executor.initContainers = [
            c for c in app.spec.executor.initContainers if c.name != "skr"
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
