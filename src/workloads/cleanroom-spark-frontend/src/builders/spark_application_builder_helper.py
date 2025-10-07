from src.builders.conf_vn2_spark_application_builder import (
    ConfidentialVN2SparkApplicationBuilder,
)
from src.builders.i_spark_application_builder import ISparkApplicationBuilder
from src.builders.k8s_spark_application_builder import K8sSparkApplicationBuilder
from src.config.configuration import CleanroomSettings, SparkComputeProvider
from src.models.input_models import JobInput


class SparkApplicationBuilderFactory:
    @staticmethod
    def get_spark_application_builder(
        provider_type: SparkComputeProvider,
        job: JobInput,
        cleanroom_settings: CleanroomSettings,
    ) -> ISparkApplicationBuilder:
        if provider_type == SparkComputeProvider.ConfidentialVirtualNode:
            spark_app_spec_builder = ConfidentialVN2SparkApplicationBuilder(
                cleanroom_settings
            ).CreateBuilder()
        elif provider_type == SparkComputeProvider.Kubernetes:
            spark_app_spec_builder = K8sSparkApplicationBuilder(
                cleanroom_settings
            ).CreateBuilder()
        else:
            raise ValueError(f"Unsupported compute provider: {provider_type}")

        return spark_app_spec_builder
