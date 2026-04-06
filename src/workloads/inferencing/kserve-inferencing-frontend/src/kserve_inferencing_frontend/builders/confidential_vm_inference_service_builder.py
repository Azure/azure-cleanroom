# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

from typing import Optional

from kubernetes.client import models as k8smodels

from ..builders.inference_service_builder import InferenceServiceBuilder
from ..config.configuration import (
    CleanroomSettings,
    PredictorSettings,
    TelemetrySettings,
)
from ..models.cleanroom_inferencing_application import CleanRoomInferencingApplication
from ..models.inference_service_models import PredictorSpec
from ..models.input_models import GovernanceSettings, PredictorInput


class ConfidentialVmInferenceServiceBuilder(InferenceServiceBuilder):
    def __init__(
        self,
        cleanroom_settings: CleanroomSettings,
        telemetry_settings: TelemetrySettings,
        governance_settings: Optional[GovernanceSettings],
    ):
        super().__init__(cleanroom_settings, telemetry_settings, governance_settings)

    def _get_predictor(
        self,
        input: PredictorInput,
        predictor_settings: PredictorSettings,
    ) -> PredictorSpec:
        predictor = super()._get_predictor(input, predictor_settings)
        # CVM inferencing requires host networking. Default to True if not specified
        # via placement input.
        if predictor.hostNetwork is None:
            predictor.hostNetwork = True
        return predictor

    def Build(self) -> CleanRoomInferencingApplication:
        app = super().Build()

        # Add tpmrm0 volume for CVM attestation agent.
        app.spec.predictor.volumes.append(
            k8smodels.V1Volume(
                name="tpmrm0",
                host_path=k8smodels.V1HostPathVolumeSource(path="/dev/tpmrm0"),
            )
        )

        # On a CVM flex node each container has its own mount namespace. The blobfuse sidecar
        # creates a FUSE mount inside the shared emptyDir which is only visible to other
        # containers when mount propagation is configured:
        # - Bidirectional on the blobfuse sidecar so the FUSE mount propagates out.
        # - HostToContainer on consuming containers so they receive the propagated mount
        #   (does not require the container to be privileged).
        if app.spec.predictor.initContainers:
            for container in app.spec.predictor.initContainers:
                for vm in container.volume_mounts:
                    if vm.name == "remotemounts":
                        if "blobfuse" in container.name:
                            vm.mount_propagation = "Bidirectional"
                        else:
                            vm.mount_propagation = "HostToContainer"

        serving_container = next(
            c for c in app.spec.predictor.containers if c.name == "kserve-container"
        )
        if serving_container.volume_mounts:
            for vm in serving_container.volume_mounts:
                if vm.name == "remotemounts":
                    vm.mount_propagation = "HostToContainer"

        if app.spec.predictor.containers:
            for container in app.spec.predictor.containers:
                for vm in container.volume_mounts:
                    if vm.name == "remotemounts":
                        vm.mount_propagation = "HostToContainer"

        return app
