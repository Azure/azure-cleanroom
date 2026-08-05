import base64
import copy
import json
import logging
import os
import re
import tempfile
import threading
from dataclasses import dataclass
from typing import List, Optional

import oras.client
import yaml
from kubernetes.client import models as k8smodels

from cleanroom_internal.utilities import otel_utilities
from frontend_internal.cleanroom_application_builder import CleanroomApplicationBuilder
from frontend_internal.models.cleanroom_application import Sidecar
from frontend_internal.models.input_models import AttestationType, TelemetrySettings

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
from ..utilities.container_utils import find_container

logger = logging.getLogger("kserve_application_builder")


@dataclass
class RuntimeConfig:
    """Configuration for a supported KServe runtime."""

    model_arg: str
    health_path: str
    supports_model_name: bool = False
    startup_failure_threshold: int = 120
    metrics_path: Optional[str] = None
    default_args: Optional[List[str]] = None


RUNTIMES: dict[str, RuntimeConfig] = {
    "kserve-sklearnserver": RuntimeConfig(
        model_arg="--model_dir",
        health_path="/v2/health/ready",
        supports_model_name=True,
        metrics_path="/metrics",
    ),
    "llamacpp-server": RuntimeConfig(
        model_arg="--model",
        health_path="/health",
        metrics_path="/metrics",
        default_args=["--metrics"],
    ),
    "llamacpp-server-cuda": RuntimeConfig(
        model_arg="--model",
        health_path="/health",
        metrics_path="/metrics",
        default_args=["--metrics"],
    ),
    "vllm-openai": RuntimeConfig(
        model_arg="--model",
        health_path="/health",
        startup_failure_threshold=360,
        metrics_path="/metrics",
    ),
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
            extra_vars = self._get_telemetry_extra_vars()
            cleanroom_app_builder = cleanroom_app_builder.WithTelemetry(
                self._telemetry,
                self._trace_context,
                extra_vars,
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
            ccr_proxy_fqdn = f"{self._model_name}-predictor-https.{self._namespace}.svc"
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
        volumes.append(
            k8smodels.V1Volume(name=Constants.REMOTE_MOUNTS_VOLUME, empty_dir={})
        )
        volumes.append(
            k8smodels.V1Volume(name=Constants.TELEMETRY_MOUNTS_VOLUME, empty_dir={})
        )
        volumes.append(
            k8smodels.V1Volume(name=Constants.VOLUME_STATUS_MOUNTS_VOLUME, empty_dir={})
        )
        volumes.append(k8smodels.V1Volume(name=Constants.SHARED_VOLUME, empty_dir={}))

        assert self._predictor is not None
        self._predictor.initContainers = []
        self._predictor.initContainers.extend([x.container for x in sidecars])

        self._predictor.volumes = volumes

        # Wire model path arg and model name onto the serving container.
        assert self._predictor.containers is not None
        serving_container = find_container(
            self._predictor.containers, Constants.KSERVE_CONTAINER
        )
        assert self._runtime is not None
        model_arg_flag = RUNTIMES[self._runtime].model_arg
        if model_arg_flag and self._model_dir:
            model_path = f"{Constants.REMOTE_MOUNT_PATH}/{self._model_dir}"
            serving_container.args = serving_container.args or []
            serving_container.args.extend([model_arg_flag, model_path])
        if self._model_name and RUNTIMES[self._runtime].supports_model_name:
            serving_container.args = serving_container.args or []
            serving_container.args.extend(["--model_name", self._model_name])

        # Add volume mount for blobfuse model data.
        serving_container.volume_mounts = [
            k8smodels.V1VolumeMount(
                name=Constants.REMOTE_MOUNTS_VOLUME,
                mount_path=Constants.REMOTE_MOUNT_PATH,
            )
        ]

        app = CleanRoomInferencingApplication(
            InferenceServiceSpec(predictor=self._predictor),
            predictor_policy=inferencing_pod_policy["predictor"],
            transformer_policy=inferencing_pod_policy["transformer"],
            sidecars=sidecars,
        )

        # Hook for subclasses to customize the built application.
        self._customize_app(app)

        return app

    def _customize_app(self, app: CleanRoomInferencingApplication):
        """Override in subclasses to customize the built application.

        Called at the end of Build() before returning. Subclasses should
        modify the app in-place rather than overriding Build().
        """
        pass

    def _get_predictor(
        self,
        input: PredictorInput,
        predictor_settings: PredictorSettings,
    ) -> PredictorSpec:
        runtime = input.model.runtime
        if runtime not in RUNTIMES:
            raise ValueError(
                f"Unsupported runtime: {runtime}. "
                f"Supported runtimes: {list(RUNTIMES.keys())}"
            )
        self._runtime = runtime

        # The runtime name is supplied by the caller (the inferencing
        # agent forwards what the user requested). The implementing
        # image+digest is not a caller input — it's pinned by the
        # frontend's bundled digest table (operationally pinned by the
        # workload release version) and resolved from the runtime name
        # here.
        try:
            image = _resolve_runtime_image(runtime, self._cleanroom_settings)
        except Exception as e:
            raise ValueError(
                f"Failed to resolve image for runtime '{runtime}' "
                f"from the inferencing digests document: {e}"
            ) from e
        if not image:
            raise ValueError(
                f"Runtime '{runtime}' not found in the inferencing "
                "digests document. The runtime name must match an "
                "entry in the digest table bundled with this frontend. "
                f"Known runtimes: {list(RUNTIMES.keys())}"
            )

        # Build the serving container using containers spec. A startup probe
        # gates pod readiness on model loading completion so that KServe does
        # not report Ready before the model server can accept requests.
        health_path = RUNTIMES[runtime].health_path
        failure_threshold = RUNTIMES[runtime].startup_failure_threshold
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
                failure_threshold=failure_threshold,
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

        # Wire runtime default args (e.g. --metrics for llama.cpp).
        runtime_defaults = RUNTIMES[runtime].default_args
        if runtime_defaults:
            container.args = list(runtime_defaults)

        # Wire user-provided args (appended after defaults).
        if input.model.args:
            container.args = (container.args or []) + list(input.model.args)

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

        predictor.nodeSelector = {Constants.POD_POLICY_KEY: Constants.POD_POLICY_VALUE}
        if self._requires_gpu(predictor):
            gpu_expr = {
                "key": Constants.GPU_PRODUCT_LABEL_KEY,
                "operator": "Exists",
            }
            combined: dict = {}
            if input.affinity:
                combined = copy.deepcopy(input.affinity)
            # Deep-merge nodeAffinity: inject the GFD match expression
            # into every existing nodeSelectorTerm (terms are OR'd, but
            # expressions within a term are AND'd). This ensures the GPU
            # constraint is enforced alongside any user-provided terms.
            user_na = combined.get("nodeAffinity", {})
            user_required = user_na.get(
                "requiredDuringSchedulingIgnoredDuringExecution", {}
            )
            user_terms = user_required.get("nodeSelectorTerms", [])
            if user_terms:
                for term in user_terms:
                    exprs = term.setdefault("matchExpressions", [])
                    exprs.append(gpu_expr)
            else:
                user_terms.append({"matchExpressions": [gpu_expr]})
            user_required["nodeSelectorTerms"] = user_terms
            user_na["requiredDuringSchedulingIgnoredDuringExecution"] = user_required
            combined["nodeAffinity"] = user_na
            predictor.affinity = combined
        elif input.affinity:
            predictor.affinity = input.affinity

        predictor.tolerations = [
            {
                "key": Constants.POD_POLICY_KEY,
                "operator": "Equal",
                "value": Constants.POD_POLICY_VALUE,
                "effect": "NoSchedule",
            }
        ]

        return predictor

    def _resolve_kserve_agent_image(self) -> Optional[str]:
        """Resolve the kserve-agent image to a digest-pinned reference."""
        return resolve_kserve_agent_image(self._cleanroom_settings)

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
            sku = "gpu" if self._requires_gpu() else "cpu"
            # TODO: Currently we iterate over ALL known image measurement
            # sets and union their PCR values. Long-term, the deployment
            # should query the cluster for the active flex node image
            # version and look up only the corresponding PCR measurements
            # from the measurements document, instead of accepting all.
            all_pcrs: dict[str, list[str]] = {}
            for image_data in cvm_measurements.values():
                if sku not in image_data:
                    continue
                for pcr_index, pcr_value in image_data[sku]["pcrs"].items():
                    values = all_pcrs.setdefault(pcr_index, [])
                    if pcr_value not in values:
                        values.append(pcr_value)
            return {
                "predictor": Policy(
                    json=allow_all_json_policy,
                    json_base64=Constants.ALLOW_ALL_POLICY_BASE64,
                    pcrs=all_pcrs,
                ),
                "transformer": Policy(
                    json=allow_all_json_policy,
                    json_base64=Constants.ALLOW_ALL_POLICY_BASE64,
                    pcrs=all_pcrs,
                ),
            }

        raise NotImplementedError("Custom policy generation is not implemented yet.")

    def _requires_gpu(self, predictor: PredictorSpec = None) -> bool:
        """Check if the predictor requests GPU resources."""
        p = predictor or self._predictor
        if not p or not p.containers:
            return False
        for container in p.containers:
            if not container.resources:
                continue
            for field in (container.resources.requests, container.resources.limits):
                if field and "nvidia.com/gpu" in field:
                    return True
        return False

    def _get_telemetry_extra_vars(self) -> dict:
        """Build extra telemetry variables for the OTEL collector sidecar.

        Configures:
        - Resource attributes stamped onto all metrics for filtering
          and aggregation at pod/model/runtime/contract granularity.
        - Prometheus scrape targets for containers that expose /metrics.
        """
        extra_vars: dict[str, str] = {}

        # Resource attributes enable per-pod, per-model, per-runtime,
        # per-contract filtering and aggregation in Grafana/Prometheus.
        resource_attributes = otel_utilities.get_current_baggage()
        resource_attributes["service.name"] = self._app_name or "kserve-inference"
        if self._model_name:
            resource_attributes["model.name"] = self._model_name
        if self._runtime:
            resource_attributes["runtime"] = self._runtime
        if self._contract_id:
            resource_attributes["contract.id"] = self._contract_id

        extra_vars["resourceAttributes"] = base64.b64encode(
            json.dumps(resource_attributes).encode("utf-8")
        ).decode("utf-8")

        # Prometheus scrape targets.
        scrape_targets: list[dict[str, str]] = []

        if self._runtime and self._runtime in RUNTIMES:
            runtime_config = RUNTIMES[self._runtime]
            if runtime_config.metrics_path:
                scrape_targets.append(
                    {
                        "job_name": "kserve-container",
                        "target": "localhost:8080",
                        "metrics_path": runtime_config.metrics_path,
                    }
                )

        # kserve-agent: injected by KServe when batcher or logger is
        # configured. Exposes Prometheus metrics on port 9081.
        has_agent = self._predictor and (
            self._predictor.batcher is not None or self._predictor.logger is not None
        )
        if has_agent:
            scrape_targets.append(
                {
                    "job_name": "kserve-agent",
                    "target": "localhost:9081",
                    "metrics_path": "/metrics",
                }
            )

        if scrape_targets:
            extra_vars["prometheusScrapeTargets"] = base64.b64encode(
                json.dumps(scrape_targets).encode("utf-8")
            ).decode("utf-8")

        return extra_vars

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
                        f"Using CVM measurements document: {measurements_url}"
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


def resolve_kserve_agent_image(
    cleanroom_settings: CleanroomSettings,
) -> Optional[str]:
    """Resolve the kserve-agent image to a digest-pinned reference from
    the inferencing digests OCI artifact. Returns None on failure."""
    try:
        return _resolve_runtime_image("kserve-agent", cleanroom_settings)
    except Exception as e:
        logger.warning(f"Failed to resolve digest for runtime 'kserve-agent': {e}")
        return None


def _resolve_runtime_image(
    runtime_name: str,
    cleanroom_settings: CleanroomSettings,
) -> Optional[str]:
    """Resolve a runtime name to a digest-pinned image@digest reference
    from the inferencing digests OCI artifact. Returns None if no
    matching entry is found. Raises on fetch/parse errors so callers
    can decide whether to suppress or propagate."""
    digests = _get_inferencing_digests(cleanroom_settings)
    entries = digests if isinstance(digests, list) else [digests]
    for entry in entries:
        if entry.get("name") == runtime_name:
            image = entry["image"]
            digest = entry["digest"]
            return f"{image}@{digest}"
    return None


def _get_inferencing_digests(cleanroom_settings: CleanroomSettings) -> list:
    """Download and cache the inferencing digests OCI artifact."""
    temp_dir = tempfile.gettempdir()
    digests_path = os.path.join(temp_dir, "inferencing-digests.yaml")

    lock = threading.Lock()
    if not os.path.exists(digests_path):
        with lock:
            if not os.path.exists(digests_path):
                digests_url = cleanroom_settings.inferencing_digests_document
                logger.warning(f"Using inferencing digests document: {digests_url}")

                insecure = cleanroom_settings.use_http
                client = oras.client.OrasClient(insecure=insecure)
                client.pull(
                    target=digests_url,
                    outdir=temp_dir,
                )

    with open(digests_path) as f:
        return yaml.safe_load(f)
