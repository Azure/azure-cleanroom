import base64
import json
import logging
from abc import abstractmethod
from enum import StrEnum
from typing import Optional

from cleanroom_internal.utilities import otel_utilities

from ..builders.i_spark_application_builder import CleanRoomSparkApplication
from ..builders.spark_application_builder_helper import SparkApplicationBuilderFactory
from ..config.configuration import (
    CleanroomSettings,
    Configuration,
    SkuSettings,
    SparkComputeProvider,
    TelemetrySettings,
)
from ..models.input_models import EnvData, JobInput, SQLJobInput

logger = logging.getLogger("job_helper")


from .helpers import to_spark_app_name


class SparkJobConverter:
    def __init__(
        self,
        cleanroom_settings: CleanroomSettings,
        compute_provider: SparkComputeProvider,
    ):
        self._cleanroom_settings = cleanroom_settings
        self._compute_provider = compute_provider

    @abstractmethod
    def to_spark_spec(
        self,
        job_id: str,
        jobInput: JobInput,
        telemetry_settings: Optional[TelemetrySettings] = None,
        override_sku_settings: Optional[SkuSettings] = None,
    ) -> CleanRoomSparkApplication:
        raise NotImplementedError("Must be implemented in subclasses.")

    def _convert_to_spark_spec(
        self,
        job_id: str,
        job: JobInput,
        application_image: str,
        application_file: str,
        policy_file: str,
        sku_settings: SkuSettings,
        debug_mode: bool = False,
        allow_all: bool = False,
        arguments: Optional[list[str]] = None,
        env_vars: Optional[list[EnvData]] = None,
        telemetry_settings: Optional[TelemetrySettings] = None,
    ) -> CleanRoomSparkApplication:
        spark_app_spec_builder = (
            SparkApplicationBuilderFactory.get_spark_application_builder(
                self._compute_provider,
                job,
                self._cleanroom_settings,
                telemetry_settings or TelemetrySettings(),
            )
        )

        spark_app_spec_builder = (
            spark_app_spec_builder.WithName(job_id)
            .WithImage(application_image)
            .WithPolicy(policy_file, debug_mode, allow_all)
            .WithMainApplicationFile(application_file)
            .WithEnvVars(env_vars or [])
            .WithArguments(arguments or [])
            .AddDriver(settings=sku_settings.driver)
            .AddExecutor(settings=sku_settings.executor)
        )

        for dataset in job.datasets:
            spark_app_spec_builder = spark_app_spec_builder.AddDataset(dataset)
        if job.datasink:
            spark_app_spec_builder = spark_app_spec_builder.AddDatasink(job.datasink)

        return spark_app_spec_builder.Build()


class SQLSparkJobConverter(SparkJobConverter):
    def __init__(self, config: Configuration):
        super().__init__(
            config.cleanroom,
            config.spark.compute_provider,
        )
        self._sku_settings = config.applications.analytics.sql
        self._application_settings = config.applications.analytics
        self._service_url = config.service.url

    def _to_app_input(
        self,
        job: SQLJobInput,
    ):
        assert job.datasink is not None, "Datasink must be provided for SQL jobs"
        app_input = {"query": job.query, "datasets": []}
        for dataset in job.datasets:
            app_input["datasets"].append(
                {
                    "name": dataset.name,
                    "viewName": dataset.viewName,
                    "path": "/mnt/remote/" + dataset.name,
                    "format": str(dataset.format.value),
                    "schema": dataset.schema_,
                    "allowedFields": dataset.allowedFields,
                }
            )
        app_input["datasink"] = {
            "name": job.datasink.name,
            "viewName": job.datasink.viewName,
            "path": "/mnt/remote/" + job.datasink.name + "/" + job.contract_id,
            "format": str(job.datasink.format.value),
            "schema": job.datasink.schema_,
            "allowedFields": job.datasink.allowedFields,
        }

        return base64.b64encode(json.dumps(app_input).encode()).decode()

    def to_spark_spec(
        self,
        job_id: str,
        job: JobInput,
        telemetry_settings: Optional[TelemetrySettings] = None,
        override_sku_settings: Optional[SkuSettings] = None,
    ):
        if not isinstance(job, SQLJobInput):
            raise ValueError("Job is not of type SQLJobInput")
        sql_job: SQLJobInput = job  # Typecast to SQLJobInput
        app_name = to_spark_app_name(job_id)
        # The statistics and operational endpoints are hosted by the frontend service which translates them to k8s events.
        # The audit events are persisted in CGS and hence the endpoint points to the governance sidecar that is running as a sidecar.
        app_config = {
            "eventRecorderConfiguration": {
                "providerType": "http",
                "statisticsRecorderEndpoint": f"{self._service_url}/analytics/{app_name}/record_statistics",
                "eventEmitterEndpoint": f"{self._service_url}/analytics/{app_name}/record_operational_event",
                "auditRecordLoggerEndpoint": "http://localhost:8300/events",
            }
        }

        # Use override settings if provided, otherwise use default
        sku_settings = (
            override_sku_settings if override_sku_settings else self._sku_settings
        )

        return super()._convert_to_spark_spec(
            app_name,
            job,
            env_vars=[
                EnvData(
                    key="JOB_CONFIG", value=self._to_app_input(job), isMeasured=True
                ),
                EnvData(key="JOB_ID", value=job_id, isMeasured=False),
                EnvData(
                    key="APPLICATION_CONFIGURATION",
                    value=json.dumps(app_config),
                    isMeasured=False,
                ),
                EnvData(
                    key="START_DATE",
                    value=(
                        sql_job.start_date.strftime("%Y-%m-%d")
                        if sql_job.start_date is not None
                        else ""
                    ),
                    isMeasured=False,
                ),
                EnvData(
                    key="END_DATE",
                    value=(
                        sql_job.end_date.strftime("%Y-%m-%d")
                        if sql_job.end_date is not None
                        else ""
                    ),
                    isMeasured=False,
                ),
            ],
            sku_settings=sku_settings,
            application_image=self._application_settings.image,
            application_file=self._application_settings.application_file,
            policy_file=self._application_settings.policy_file,
            debug_mode=self._application_settings.debug_mode,
            allow_all=self._application_settings.allow_all,
            telemetry_settings=telemetry_settings,
        )


class PiSparkJobConverter(SparkJobConverter):
    def __init__(self, config: Configuration):
        super().__init__(
            config.cleanroom,
            config.spark.compute_provider,
        )
        self._sku_settings = config.applications.examples.pi
        self._application_image = config.applications.examples.image
        self._application_file = config.applications.examples.application_file

    def to_spark_spec(
        self,
        job_id: str,
        job: JobInput,
        telemetry_settings: Optional[TelemetrySettings] = None,
        override_sku_settings: Optional[SkuSettings] = None,
    ):
        return super()._convert_to_spark_spec(
            job_id,
            job,
            sku_settings=self._sku_settings,
            application_image=self._application_image,
            application_file=self._application_file,
            policy_file="",
            debug_mode=True,
            allow_all=True,
            telemetry_settings=telemetry_settings,
        )


class SparkJobProviderType(StrEnum):
    SQL = "sql"
    PI = "pi"


def get(
    provider_type: SparkJobProviderType, config: Configuration
) -> SparkJobConverter:
    if provider_type == SparkJobProviderType.SQL:
        return SQLSparkJobConverter(config)
    elif provider_type == SparkJobProviderType.PI:
        return PiSparkJobConverter(config)
    else:
        raise ValueError(f"Unsupported provider type: {providerType}")
