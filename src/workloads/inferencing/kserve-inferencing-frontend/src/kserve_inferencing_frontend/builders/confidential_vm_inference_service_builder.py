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
from ..utilities.constants import Constants
from ..utilities.container_utils import (
    add_volume_mount,
    find_container_optional,
    prepend_env_path,
    set_mount_propagation,
)


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
        # CVM inferencing no more requires host networking with FlexNodeIpLayout design.
        # Default to False if not specified via placement input.
        if predictor.hostNetwork is None:
            predictor.hostNetwork = False
        return predictor

    def _customize_app(self, app: CleanRoomInferencingApplication):
        # Add tpmrm0 volume for CVM attestation agent.
        app.spec.predictor.volumes.append(
            k8smodels.V1Volume(
                name=Constants.TPMRM0_VOLUME,
                host_path=k8smodels.V1HostPathVolumeSource(
                    path=Constants.TPMRM0_HOST_PATH
                ),
            )
        )

        # Add attestation lock volume so concurrent pods serialize TPM
        # NV writes via flock. The lock file lives on the host's /run
        # tmpfs and is shared across all pods on the same node.
        # The matching volumeMount is in the sidecar template.
        app.spec.predictor.volumes.append(
            k8smodels.V1Volume(
                name=Constants.ATTESTATION_LOCK_VOLUME,
                host_path=k8smodels.V1HostPathVolumeSource(
                    path=Constants.ATTESTATION_LOCK_HOST_PATH,
                    type="DirectoryOrCreate",
                ),
            )
        )

        # Add NVIDIA driver libraries for GPU attestation via NVML.
        if self._requires_gpu():
            app.spec.predictor.volumes.append(
                k8smodels.V1Volume(
                    name=Constants.NVIDIA_DRIVER_LIBS_VOLUME,
                    host_path=k8smodels.V1HostPathVolumeSource(
                        path=Constants.NVIDIA_DRIVER_LIBS_HOST_PATH,
                        type="Directory",
                    ),
                ),
            )
            agent = find_container_optional(
                app.spec.predictor.initContainers,
                Constants.CVM_ATTESTATION_AGENT_CONTAINER,
            )
            if agent:
                add_volume_mount(
                    agent,
                    Constants.NVIDIA_DRIVER_LIBS_VOLUME,
                    Constants.NVIDIA_DRIVER_LIBS_MOUNT_PATH,
                    read_only=True,
                )
                prepend_env_path(
                    agent, "LD_LIBRARY_PATH", Constants.NVIDIA_DRIVER_LIBS_MOUNT_PATH
                )

            # Mount OpenSSL 3.4.1 from host into GPU inference containers.
            # The encrypted PCIe channel between CPU and H100 in CC mode uses
            # OpenSSL for encryption. OpenSSL 3.4.1 (AVX512) doubles CPU-GPU
            # bandwidth compared to the default OpenSSL 3.0.2.
            app.spec.predictor.volumes.append(
                k8smodels.V1Volume(
                    name=Constants.OPENSSL_VOLUME,
                    host_path=k8smodels.V1HostPathVolumeSource(
                        path=Constants.OPENSSL_HOST_PATH,
                        type="Directory",
                    ),
                ),
            )
            kserve_container = find_container_optional(
                app.spec.predictor.containers,
                Constants.KSERVE_CONTAINER,
            )
            if kserve_container:
                add_volume_mount(
                    kserve_container,
                    Constants.OPENSSL_VOLUME,
                    Constants.OPENSSL_MOUNT_PATH,
                    read_only=True,
                )
                prepend_env_path(
                    kserve_container,
                    "LD_LIBRARY_PATH",
                    Constants.OPENSSL_LIB_PATH,
                )

        # CVM mount propagation: Bidirectional on blobfuse, HostToContainer on others.
        set_mount_propagation(
            app.spec.predictor.initContainers,
            Constants.REMOTE_MOUNTS_VOLUME,
            "HostToContainer",
            blobfuse_mode="Bidirectional",
        )
        set_mount_propagation(
            app.spec.predictor.containers,
            Constants.REMOTE_MOUNTS_VOLUME,
            "HostToContainer",
        )
