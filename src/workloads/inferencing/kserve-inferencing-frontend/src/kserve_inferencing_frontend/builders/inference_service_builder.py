import base64
import logging
import os
import re
import tempfile
import threading
from typing import List, Optional

import oras.client
import yaml
from cleanroom_internal.utilities import otel_utilities
from frontend_internal.cleanroom_application_builder import CleanroomApplicationBuilder
from frontend_internal.models.cleanroom_application import Sidecar
from frontend_internal.models.input_models import AttestationType, TelemetrySettings
from kubernetes.client import models as k8smodels

from ..builders.i_inference_service_builder import (
    IInferenceServiceBuilder,
    IInferenceServiceBuilderWithName,
    IInferenceServiceBuilderWithPolicy,
    IInferenceServiceBuilderWithSpec,
)
from ..config.configuration import CleanroomSettings, PredictorSettings
from ..models.cleanroom_inferencing_application import (
    CleanRoomInferencingApplication,
    Policy,
)
from ..models.inference_service_models import InferenceServiceSpec, PredictorSpec
from ..models.input_models import *
from ..utilities.constants import Constants

logger = logging.getLogger("kserve_application_builder")

# Runtimes supported by the containers-spec builder.
SUPPORTED_RUNTIMES = {
    "kserve-sklearnserver",
    "llamacpp-server",
}

# Maps runtime name to the CLI argument used to specify the model path.
RUNTIME_MODEL_ARG_MAP = {
    "kserve-sklearnserver": "--model_dir",
    "llamacpp-server": "--model",
}

# Runtimes that accept the --model_name argument.
RUNTIMES_WITH_MODEL_NAME = {
    "kserve-sklearnserver",
}

# Maps runtime name to its health endpoint path for startup probes. The probe
# gates pod readiness on model loading completion so that KServe does not mark
# the InferenceService Ready before the model is actually serving.
RUNTIME_HEALTH_PATH_MAP = {
    "kserve-sklearnserver": "/v2/health/ready",
    "llamacpp-server": "/health",
}


def to_kserve_app_name(name: str) -> str:
    """
    Convert a name to a valid cr name.
    CR names must be lowercase and can only contain alphanumeric characters and hyphens.
    """
    name = "cl-kserve-" + re.sub(r"[^a-z0-9-]", "-", name.lower())
    return name[:63]


class InferenceServiceBuilder(
    IInferenceServiceBuilder,
    IInferenceServiceBuilderWithName,
    IInferenceServiceBuilderWithPolicy,
    IInferenceServiceBuilderWithSpec,
):
    def __init__(
        self,
        cleanroom_settings: CleanroomSettings,
        telemetry_settings: TelemetrySettings,
        governance_settings: Optional[GovernanceSettings],
    ):
        self._cleanroom_settings = cleanroom_settings
        self._telemetry = telemetry_settings
        self._governance_settings = governance_settings
        self._app_name = None
        self._contract_id = ""
        self._runtime = None
        self._predictor: Optional[PredictorSpec] = None
        self._debug_mode: bool = False
        self._allow_all: bool = False
        self._datasets: List[DatasetInfo] = []
        self._trace_context: dict[str, str] = {}
        self._governance_required = governance_settings is not None
        self._model_dir = None
        self._model_name = None
        self._namespace = None
        self._placement: Optional[PlacementInput] = None

    def CreateBuilder(self, contract_id: str = "") -> "IInferenceServiceBuilder":
        self._contract_id = contract_id
        self._trace_context = otel_utilities.inject_current_context_into_carrier()
        return self

    def WithPolicy(
        self, policy_file: str, debug_mode: bool, allow_all: bool
    ) -> "IInferenceServiceBuilderWithPolicy":
        self._debug_mode = debug_mode
        self._allow_all = allow_all
        return self

    def WithName(self, name: str) -> "IInferenceServiceBuilderWithName":
        self._app_name = to_kserve_app_name(name)
        return self

    def WithModelDir(self, model_dir: str) -> IInferenceServiceBuilderWithPolicy:
        self._model_dir = model_dir
        return self

    def WithModelName(self, model_name: str) -> IInferenceServiceBuilderWithPolicy:
        self._model_name = model_name
        return self

    def WithNamespace(self, namespace: str) -> IInferenceServiceBuilderWithPolicy:
        self._namespace = namespace
        return self

    def WithPlacement(
        self, placement: PlacementInput
    ) -> IInferenceServiceBuilderWithPolicy:
        self._placement = placement
        return self

    def AddPredictor(
        self,
        input: PredictorInput,
        settings: PredictorSettings,
    ) -> "IInferenceServiceBuilderWithSpec":
        self._predictor = self._get_predictor(input, settings)
        return self

    def AddDataset(self, dataset: DatasetInfo):
        self._datasets.append(dataset)
        return self

    def Build(self) -> CleanRoomInferencingApplication:
        if not self._app_name:
            raise ValueError("Missing required fields to build InferenceService")

        # Build the base cleanroom application to obtain sidecars.
        cleanroom_app_builder = (
            CleanroomApplicationBuilder(self._cleanroom_settings)
            .CreateBuilder()
            .WithName(self._app_name)
            .WithContractId(self._contract_id)
        )

        if self._telemetry and self._telemetry.telemetry_collection_enabled:
            cleanroom_app_builder = cleanroom_app_builder.WithTelemetry(
                self._telemetry,
                self._trace_context,
            )

        if self._governance_required:
            cleanroom_app_builder = cleanroom_app_builder.WithGovernance(
                self._governance_settings,
                attestation_type=AttestationType.CVM,
            )

        for dataset in self._datasets:
            cleanroom_app_builder = cleanroom_app_builder.AddStorage(
                dataset.accessPoint, dataset.ownerId
            )

        # ccr-proxy listens on 443 for external HTTPS traffic and terminates TLS.
        # If KServe agent is present (batcher/logger configured), route to
        # agent on 9081 which forwards to the serving container on 8080.
        # Otherwise, route directly to the serving container on 8080.
        has_agent = self._predictor and (
            self._predictor.batcher is not None or self._predictor.logger is not None
        )
        destination_port = 9081 if has_agent else 8080
        ccr_proxy_fqdn = ""
        if self._model_name and self._namespace:
            ccr_proxy_fqdn = (
                f"{self._model_name}-predictor-https"
                f".{self._namespace}.svc.cluster.local"
            )
        cleanroom_app_builder = cleanroom_app_builder.WithCcrProxyHttpsHttp(
            listener_port=443, destination_port=destination_port, fqdn=ccr_proxy_fqdn
        )

        cleanroom_app = cleanroom_app_builder.Build()
        sidecars = cleanroom_app.sidecars

        inferencing_pod_policy = self._get_inferencing_pod_policy(
            predictor_sidecars=sidecars,
            transformer_sidecars=[],
        )

        volumes: List[k8smodels.V1Volume] = []
        volumes.append(k8smodels.V1Volume(name="remotemounts", empty_dir={}))
        volumes.append(k8smodels.V1Volume(name="telemetrymounts", empty_dir={}))
        volumes.append(k8smodels.V1Volume(name="volumestatusmounts", empty_dir={}))
        volumes.append(k8smodels.V1Volume(name="shared", empty_dir={}))

        assert self._predictor is not None
        self._predictor.initContainers = []
        self._predictor.initContainers.extend([x.container for x in sidecars])

        self._predictor.volumes = volumes

        # Wire model path arg and model name onto the serving container.
        assert self._predictor.containers is not None
        serving_container = next(
            c for c in self._predictor.containers if c.name == "kserve-container"
        )
        assert self._runtime is not None
        model_arg_flag = RUNTIME_MODEL_ARG_MAP.get(self._runtime)
        if model_arg_flag and self._model_dir:
            model_path = f"/mnt/remote/{self._model_dir}"
            serving_container.args = serving_container.args or []
            serving_container.args.extend([model_arg_flag, model_path])
        if self._model_name and self._runtime in RUNTIMES_WITH_MODEL_NAME:
            serving_container.args = serving_container.args or []
            serving_container.args.extend(["--model_name", self._model_name])

        # Add volume mount for blobfuse model data.
        serving_container.volume_mounts = [
            k8smodels.V1VolumeMount(
                name="remotemounts",
                mount_path="/mnt/remote",
            )
        ]

        return CleanRoomInferencingApplication(
            InferenceServiceSpec(predictor=self._predictor),
            predictor_policy=inferencing_pod_policy["predictor"],
            transformer_policy=inferencing_pod_policy["transformer"],
            sidecars=sidecars,
        )

    def _get_predictor(
        self,
        input: PredictorInput,
        predictor_settings: PredictorSettings,
    ) -> PredictorSpec:
        runtime = input.model.runtime
        if runtime not in SUPPORTED_RUNTIMES:
            raise ValueError(
                f"Unsupported runtime: {runtime}. "
                f"Supported runtimes: {SUPPORTED_RUNTIMES}"
            )
        self._runtime = runtime

        # Resolve the digest-pinned container image for this runtime.
        image = self._resolve_container_image(runtime)

        # Build the serving container using containers spec. A startup probe
        # gates pod readiness on model loading completion so that KServe does
        # not report Ready before the model server can accept requests.
        health_path = RUNTIME_HEALTH_PATH_MAP.get(runtime, "/health")
        container = k8smodels.V1Container(
            name="kserve-container",
            image=image,
            ports=[k8smodels.V1ContainerPort(container_port=8080, protocol="TCP")],
            startup_probe=k8smodels.V1Probe(
                http_get=k8smodels.V1HTTPGetAction(
                    path=health_path,
                    port=8080,
                ),
                initial_delay_seconds=5,
                period_seconds=10,
                failure_threshold=120,
            ),
        )

        # Wire resources if provided.
        if input.model.resources:
            container.resources = k8smodels.V1ResourceRequirements(
                requests=input.model.resources.requests,
                limits=input.model.resources.limits,
            )

        # Wire env vars if provided.
        if input.model.env:
            container.env = [
                k8smodels.V1EnvVar(name=e.name, value=e.value) for e in input.model.env
            ]

        # Wire user-provided args.
        if input.model.args:
            container.args = list(input.model.args)

        predictor = PredictorSpec()
        predictor.containers = [container]

        if self._placement and self._placement.host_network is not None:
            predictor.hostNetwork = self._placement.host_network

        if input.min_replicas is not None:
            predictor.minReplicas = input.min_replicas
        if input.max_replicas is not None:
            predictor.maxReplicas = input.max_replicas
        if input.timeout is not None:
            predictor.timeout = input.timeout

        # Wire batcher if provided.
        if input.batcher:
            from ..models.inference_service_models import Batcher

            batcher = Batcher()
            if input.batcher.max_batch_size is not None:
                batcher.maxBatchSize = input.batcher.max_batch_size
            if input.batcher.max_latency is not None:
                batcher.maxLatency = input.batcher.max_latency
            if input.batcher.timeout is not None:
                batcher.timeout = input.batcher.timeout
            predictor.batcher = batcher

        # Wire deployment strategy if provided.
        if input.deployment_strategy:
            predictor.deploymentStrategy = input.deployment_strategy

        # Wire autoscaling fields if provided (v0.17+).
        if input.scale_metric_type:
            predictor.scaleMetricType = input.scale_metric_type
        if input.auto_scaling:
            from ..models.inference_service_models import (
                AutoScalingMetricSpec,
                AutoScalingSpec,
            )

            auto_scaling = AutoScalingSpec()
            if input.auto_scaling.metrics:
                auto_scaling.metrics = [
                    AutoScalingMetricSpec(
                        type=m.type,
                        resource=m.resource,
                        external=m.external,
                        podmetric=m.podmetric,
                    )
                    for m in input.auto_scaling.metrics
                ]
            if input.auto_scaling.behavior:
                auto_scaling.behavior = input.auto_scaling.behavior
            predictor.autoScaling = auto_scaling

        predictor.nodeSelector = {"pod-policy": "required"}
        predictor.tolerations = [
            {
                "key": "pod-policy",
                "operator": "Equal",
                "value": "required",
                "effect": "NoSchedule",
            }
        ]

        return predictor

    def _resolve_container_image(self, runtime: str) -> str:
        """Resolve a runtime name to a digest-pinned image reference."""
        runtime_digests = self._get_runtime_digests()
        for entry in runtime_digests:
            if entry.get("runtime") == runtime:
                image = entry["image"]
                digest = entry["digest"]
                return f"{image}@{digest}"

        raise ValueError(f"Runtime '{runtime}' not found in runtime digests document.")

    def _get_runtime_digests(self) -> list:
        """Download and cache the runtime digests OCI artifact."""
        temp_dir = tempfile.gettempdir()
        digests_path = os.path.join(temp_dir, "inf-runtime-digests.yaml")

        lock = threading.Lock()
        if not os.path.exists(digests_path):
            with lock:
                if not os.path.exists(digests_path):
                    digests_url = self._cleanroom_settings.runtime_digests_document
                    logger.warning(f"Using runtime digests document: {digests_url}")

                    insecure = self._cleanroom_settings.use_http
                    client = oras.client.OrasClient(insecure=insecure)
                    client.pull(
                        target=digests_url,
                        outdir=temp_dir,
                    )

        with open(digests_path) as f:
            return yaml.safe_load(f)

    def _get_inferencing_pod_policy(
        self, predictor_sidecars: List[Sidecar], transformer_sidecars: List[Sidecar]
    ) -> dict:
        if self._allow_all:
            logger.warning(
                "Allow all mode is enabled. This should only be used for "
                "development purposes."
            )
            allow_all_json_policy = base64.b64decode(
                Constants.ALLOW_ALL_POLICY_BASE64
            ).decode("utf-8")
            cvm_measurements = self._get_cvm_measurements()
            first_image = next(iter(cvm_measurements.values()))
            pcrs = first_image["pcrs"]
            return {
                "predictor": Policy(
                    json=allow_all_json_policy,
                    json_base64=Constants.ALLOW_ALL_POLICY_BASE64,
                    pcrs=pcrs,
                ),
                "transformer": Policy(
                    json=allow_all_json_policy,
                    json_base64=Constants.ALLOW_ALL_POLICY_BASE64,
                    pcrs=pcrs,
                ),
            }

        raise NotImplementedError("Custom policy generation is not implemented yet.")

    def _get_cvm_measurements(self):
        temp_dir = tempfile.gettempdir()

        lock = threading.Lock()
        if not os.path.exists(os.path.join(temp_dir, "cvm-measurements.yaml")):
            with lock:
                if not os.path.exists(os.path.join(temp_dir, "cvm-measurements.yaml")):
                    measurements_url = (
                        self._cleanroom_settings.cvm_measurements_document
                    )
                    logger.warning(
                        "Using CVM measurements document: " f"{measurements_url}"
                    )

                    insecure = self._cleanroom_settings.use_http
                    client = oras.client.OrasClient(insecure=insecure)
                    client.pull(
                        target=measurements_url,
                        outdir=temp_dir,
                    )

        with open(os.path.join(temp_dir, "cvm-measurements.yaml")) as f:
            cvm_measurements = yaml.safe_load(f)
        return cvm_measurements
