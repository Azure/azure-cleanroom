from typing import Optional

from ..builders.confidential_vm_inference_service_builder import (
    ConfidentialVmInferenceServiceBuilder,
)
from ..builders.i_inference_service_builder import IInferenceServiceBuilder
from ..builders.virtual_inference_service_builder import VirtualInferenceServiceBuilder
from ..config.configuration import (
    CleanroomSettings,
    InferencingComputeProvider,
    TelemetrySettings,
)
from ..models.input_models import GovernanceSettings, JobInput


class InferenceServiceBuilderFactory:
    @staticmethod
    def get_inference_svc_builder(
        provider_type: InferencingComputeProvider,
        job: JobInput,
        cleanroom_settings: CleanroomSettings,
        telemetry_settings: TelemetrySettings,
    ) -> IInferenceServiceBuilder:
        governance = job.governance if job.governance else None
        if provider_type == InferencingComputeProvider.Virtual:
            inference_svc_spec_builder = VirtualInferenceServiceBuilder(
                cleanroom_settings, telemetry_settings, governance
            ).CreateBuilder(job.contract_id)
        elif provider_type == InferencingComputeProvider.ConfidentialVM:
            inference_svc_spec_builder = ConfidentialVmInferenceServiceBuilder(
                cleanroom_settings, telemetry_settings, governance
            ).CreateBuilder(job.contract_id)
        elif provider_type == InferencingComputeProvider.ConfidentialVirtualNode:
            raise ValueError(
                f"Confidential VN2 compute provider support not yet added!"
            )
        else:
            raise ValueError(f"Unsupported compute provider: {provider_type}")

        return inference_svc_spec_builder
