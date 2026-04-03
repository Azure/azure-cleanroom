from ..builders.conf_vn2_spark_application_builder import (
    ConfidentialVN2SparkApplicationBuilder,
)
from ..builders.i_spark_application_builder import ISparkApplicationBuilder
from ..builders.virtual_spark_application_builder import VirtualSparkApplicationBuilder
from ..config.configuration import (
    CleanroomSettings,
    SparkComputeProvider,
    TelemetrySettings,
)
from ..models.input_models import JobInput


class SparkApplicationBuilderFactory:
    @staticmethod
    def get_spark_application_builder(
        provider_type: SparkComputeProvider,
        job: JobInput,
        cleanroom_settings: CleanroomSettings,
        telemetry_settings: TelemetrySettings,
    ) -> ISparkApplicationBuilder:
        if provider_type == SparkComputeProvider.ConfidentialVirtualNode:
            spark_app_spec_builder = ConfidentialVN2SparkApplicationBuilder(
                cleanroom_settings, telemetry_settings, job.governance
            ).CreateBuilder(job.contract_id)
        elif provider_type == SparkComputeProvider.Virtual:
            spark_app_spec_builder = VirtualSparkApplicationBuilder(
                cleanroom_settings, telemetry_settings, job.governance
            ).CreateBuilder(job.contract_id)
        else:
            raise ValueError(f"Unsupported compute provider: {provider_type}")

        return spark_app_spec_builder
