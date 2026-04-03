import logging
from typing import Optional

from frontend_internal.models.input_models import TelemetrySettings

from ..builders.i_inference_service_builder import CleanRoomInferencingApplication
from ..builders.inference_service_builder_helper import InferenceServiceBuilderFactory
from ..config.configuration import (
    CleanroomSettings,
    Configuration,
    InferencingComputeProvider,
    SkuSettings,
)
from ..models.input_models import JobInput

logger = logging.getLogger("job_helper")


class InferenceServiceConverterBase:
    def __init__(
        self,
        cleanroom_settings: CleanroomSettings,
        compute_provider: InferencingComputeProvider,
        namespace: str,
    ):
        self._cleanroom_settings = cleanroom_settings
        self._compute_provider = compute_provider
        self._namespace = namespace

    def to_inference_svc_spec(
        self,
        job_id: str,
        jobInput: JobInput,
        telemetry_settings: Optional[TelemetrySettings] = None,
    ) -> CleanRoomInferencingApplication:
        raise NotImplementedError("Must be implemented in subclasses.")

    def _convert_to_inference_svc_spec(
        self,
        job_id: str,
        job: JobInput,
        policy_file: str,
        sku_settings: SkuSettings,
        debug_mode: bool = False,
        allow_all: bool = False,
        telemetry_settings: Optional[TelemetrySettings] = None,
    ) -> CleanRoomInferencingApplication:
        inference_svc_spec_builder = (
            InferenceServiceBuilderFactory.get_inference_svc_builder(
                self._compute_provider,
                job,
                self._cleanroom_settings,
                telemetry_settings or TelemetrySettings(),
            )
        )

        inference_svc_spec_builder = (
            inference_svc_spec_builder.WithName(job_id)
            .WithPolicy(policy_file, debug_mode, allow_all)
            .WithNamespace(self._namespace)
        )

        if job.model_dir:
            inference_svc_spec_builder = inference_svc_spec_builder.WithModelDir(
                job.model_dir,
            )

        inference_svc_spec_builder = inference_svc_spec_builder.WithModelName(
            job.model_name
        )

        if job.placement:
            inference_svc_spec_builder = inference_svc_spec_builder.WithPlacement(
                job.placement,
            )

        inference_svc_spec_builder = inference_svc_spec_builder.AddPredictor(
            input=job.predictor,
            settings=sku_settings.predictor,
        )

        for dataset in job.datasets:
            inference_svc_spec_builder = inference_svc_spec_builder.AddDataset(dataset)

        return inference_svc_spec_builder.Build()


class InferenceServiceConverter(InferenceServiceConverterBase):
    def __init__(self, config: Configuration):
        super().__init__(
            config.cleanroom,
            config.kserve.compute_provider,
            config.applications.inferencing.namespace,
        )
        self._sku_settings = config.applications.inferencing.kserve

    def to_inference_svc_spec(
        self,
        job_id: str,
        job: JobInput,
        telemetry_settings: Optional[TelemetrySettings] = None,
    ):
        return super()._convert_to_inference_svc_spec(
            job_id,
            job,
            sku_settings=self._sku_settings,
            policy_file="",
            debug_mode=True,
            allow_all=True,
            telemetry_settings=telemetry_settings,
        )


def get(config: Configuration) -> InferenceServiceConverter:
    return InferenceServiceConverter(config)
    return InferenceServiceConverter(config)
