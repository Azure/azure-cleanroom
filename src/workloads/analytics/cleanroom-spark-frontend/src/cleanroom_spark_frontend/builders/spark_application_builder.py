import base64
import hashlib
import json
import logging
import math
import os
import tempfile
from typing import List

import oras.client
import yaml
from cleanroom_internal.utilities import otel_utilities
from cleanroom_sdk.models.cleanroom import DatasetInfo
from cleanroom_spark_frontend.utilities.helpers import to_spark_app_name
from frontend_internal.cleanroom_application_builder import (
    CleanroomApplicationBuilder,
    replace_vars,
)
from frontend_internal.models.cleanroom_application import Sidecar
from kubernetes.client import models as k8smodels

from ..builders.i_spark_application_builder import (
    ISparkApplicationBuilder,
    ISparkApplicationBuilderWithDriver,
    ISparkApplicationBuilderWithExecutor,
    ISparkApplicationBuilderWithImage,
    ISparkApplicationBuilderWithMainAppFile,
    ISparkApplicationBuilderWithName,
    ISparkApplicationBuilderWithPolicy,
)
from ..config.configuration import (
    CleanroomSettings,
    DriverSettings,
    ExecutorSettings,
    TelemetrySettings,
)
from ..models.cleanroom_spark_application import CleanRoomSparkApplication, Policy
from ..models.input_models import *
from ..models.spark_application_models import (
    DeployMode,
    Driver,
    DynamicAllocationProfile,
    Executor,
    MonitoringSpec,
    SparkApplicationSpec,
)
from ..utilities.constants import Constants, SparkMonitoringConstants

VOLUMESTATUS_MOUNT_PATH = "/mnt/volumestatus"
TELEMETRY_MOUNT_PATH = "/mnt/telemetry"
BLOBFUSE_READINESS_PORT_START = 6300

logger = logging.getLogger("spark_application_builder")


class SparkApplicationBuilder(
    ISparkApplicationBuilder,
    ISparkApplicationBuilderWithName,
    ISparkApplicationBuilderWithImage,
    ISparkApplicationBuilderWithMainAppFile,
    ISparkApplicationBuilderWithPolicy,
    ISparkApplicationBuilderWithDriver,
    ISparkApplicationBuilderWithExecutor,
):
    def __init__(
        self,
        cleanroom_settings: CleanroomSettings,
        telemetry_settings: TelemetrySettings,
        governance_settings: GovernanceSettings,
    ):
        self._image = None
        self._app_name = None
        self._main_application_file = None
        self._policy_file = None
        self._contract_id = ""
        self._driver: Driver = None
        self._driver_policy: dict = None
        self._executor: Executor = None
        self._executor_policy: dict = None
        self._dynamic_allocation_profile = DynamicAllocationProfile()
        self._debug_mode: bool = False
        self._allow_all: bool = False
        self._arguments = []
        self._env_vars = []
        self._datasets: List[DatasetInfo] = []
        self._datasinks: List[DatasetInfo] = []
        self._cleanroom_settings = cleanroom_settings
        self._telemetry_settings = telemetry_settings
        self._governance_settings = governance_settings
        self._trace_context = otel_utilities.inject_current_context_into_carrier()

    def CreateBuilder(self, contract_id: str) -> "ISparkApplicationBuilder":
        self._contract_id = contract_id
        return self

    def WithName(self, name: str) -> "ISparkApplicationBuilderWithName":
        self._app_name = name
        return self

    def WithImage(self, image: str) -> "ISparkApplicationBuilderWithImage":
        self._image = image
        return self

    def WithMainApplicationFile(
        self, main_application_file: str
    ) -> "ISparkApplicationBuilderWithMainAppFile":
        self._main_application_file = main_application_file
        return self

    def WithPolicy(
        self, policy_file: str, debug_mode: bool = False, allow_all: bool = False
    ) -> "ISparkApplicationBuilderWithMainAppFile":
        self._policy_file = policy_file
        self._debug_mode = debug_mode
        self._allow_all = allow_all
        return self

    def WithEnvVars(
        self, env_vars: list[EnvData]
    ) -> "ISparkApplicationBuilderWithMainAppFile":
        self._env_vars = env_vars
        return self

    def WithArguments(
        self, arguments: List[str]
    ) -> "ISparkApplicationBuilderWithMainAppFile":
        self._arguments = arguments
        return self

    def AddDriver(
        self, settings: DriverSettings
    ) -> "ISparkApplicationBuilderWithDriver":
        self._driver, self._driver_policy = self._get_driver(settings)
        return self

    def AddExecutor(
        self, settings: ExecutorSettings
    ) -> "ISparkApplicationBuilderWithExecutor":
        self._executor, self._dynamic_allocation_profile, self._executor_policy = (
            self._get_executor(settings)
        )
        return self

    def AddDataset(
        self, dataset: DatasetInfo
    ) -> "ISparkApplicationBuilderWithExecutor":
        self._datasets.append(dataset)
        return self

    def AddDatasink(
        self, datasink: DatasetInfo
    ) -> "ISparkApplicationBuilderWithExecutor":
        self._datasinks.append(datasink)
        return self

    def Build(self) -> CleanRoomSparkApplication:
        if not self._app_name or not self._image or not self._main_application_file:
            raise ValueError("Missing required fields to build SparkApplication")

        spark_conf = {}
        volumes: List[k8smodels.V1Volume] = []

        metrics_endpoint = "http://localhost:4040"
        resource_attributes = otel_utilities.get_current_baggage()
        resource_attributes["service.name"] = self._app_name + "-driver"
        resource_attributes["spark.role"] = "driver"

        # Build the base cleanroom application to obtain governance and storage sidecars.
        cleanroom_app_builder = (
            CleanroomApplicationBuilder(self._cleanroom_settings)
            .CreateBuilder()
            .WithName(self._app_name)
            .WithContractId(self._contract_id)
        )
        if (
            self._telemetry_settings
            and self._telemetry_settings.telemetry_collection_enabled
        ):
            cleanroom_app_builder = cleanroom_app_builder.WithTelemetry(
                self._telemetry_settings,
                self._trace_context,
                {
                    "resourceAttributes": base64.b64encode(
                        json.dumps(resource_attributes).encode("utf-8")
                    ).decode("utf-8"),
                    "sparkMetricsEndpoint": metrics_endpoint,
                },
            )

        if self._governance_settings:
            cleanroom_app_builder = cleanroom_app_builder.WithGovernance(
                self._governance_settings,
            )

        for dataset in self._datasets + self._datasinks:
            cleanroom_app_builder = cleanroom_app_builder.AddStorage(
                dataset.accessPoint, dataset.ownerId
            )
        cleanroom_app = cleanroom_app_builder.Build()

        # The spark-local-dir-1 volume is required to ensure that the Spark Operator does not create
        # a new volume with a random name for each Spark application. This ensures that we can measure
        # the mount point by keeping it consistent across jobs.
        volumes.append(k8smodels.V1Volume(name="spark-local-dir-1", empty_dir={}))
        volumes.append(k8smodels.V1Volume(name="remotemounts", empty_dir={}))
        volumes.append(k8smodels.V1Volume(name="telemetrymounts", empty_dir={}))
        volumes.append(k8smodels.V1Volume(name="volumestatusmounts", empty_dir={}))

        volume_mounts = [
            k8smodels.V1VolumeMount(
                name="remotemounts", mount_path="/mnt/remote", read_only=True
            ),
            k8smodels.V1VolumeMount(
                name="telemetrymounts", mount_path="/mnt/telemetry", read_only=False
            ),
            k8smodels.V1VolumeMount(
                name="volumestatusmounts",
                mount_path="/mnt/volumestatus",
                read_only=False,
            ),
            k8smodels.V1VolumeMount(
                name="spark-local-dir-1",
                mount_path="/var/data/spark-local-dir",
                read_only=False,
            ),
        ]

        if self._driver.volumeMounts is None:
            self._driver.volumeMounts = []
        if self._executor.volumeMounts is None:
            self._executor.volumeMounts = []

        self._driver.volumeMounts.extend(volume_mounts)
        self._executor.volumeMounts.extend(volume_mounts)

        # Order of the containers in the sidecars list is important as that determines the order of
        # the startup sequence of the init containers in the Spark application.
        # https://kubernetes.io/docs/concepts/workloads/pods/sidecar-containers/#sidecar-containers-and-pod-lifecycle

        # If telemetry collection is disabled, do not add the OTEL sidecar. If this is done and the sidecar keeps crashing,
        # the spark executors will deemed to have failed by the driver and the job will fail.
        if (
            self._telemetry_settings
            and self._telemetry_settings.telemetry_collection_enabled
        ):
            self._configure_spark_monitoring_plugin(spark_conf)

            if self._trace_context.get("traceparent"):
                spark_conf[SparkMonitoringConstants.SPARK_OTEL_TRACEPARENT_KEY] = (
                    self._trace_context["traceparent"]
                )

        if self._governance_settings:
            # Governance port needs to be passed as an argument to the Spark application.
            self._arguments.extend(["--governance-port", str(8300)])

        sidecars = cleanroom_app.sidecars
        driver_sidecars = list(sidecars)
        executor_sidecars = list(sidecars)

        # OTEL sidecar should be the first init container to start as other sidecars depend on it
        # being available.
        # Calculate full CCE policy.
        spark_pod_policy = self._get_spark_pod_policy(
            driver_sidecars=driver_sidecars,
            executor_sidecars=executor_sidecars,
        )

        self._driver.initContainers.extend([x.container for x in driver_sidecars])
        self._executor.initContainers.extend([x.container for x in executor_sidecars])

        return CleanRoomSparkApplication(
            SparkApplicationSpec(
                type="Python",
                name=self._app_name,
                mode=DeployMode.cluster,
                arguments=self._arguments if self._arguments else [],
                sparkConf=spark_conf if spark_conf else {},
                volumes=volumes,
                sparkVersion="4.0.0",
                image=self._image,
                mainApplicationFile=self._main_application_file,
                driver=self._driver,
                executor=self._executor,
                dynamicAllocation=self._dynamic_allocation_profile,
                monitoring=MonitoringSpec(
                    exposeDriverMetrics=True,
                    exposeExecutorMetrics=True,
                ),
            ),
            spark_pod_policy["driver"],
            spark_pod_policy["executor"],
        )

    def _get_rego_policy(self, container_policy_rego: list) -> str:
        placeholder_rego_str = ""
        with open(
            f"{os.path.dirname(__file__)}/policy.rego", "r", encoding="utf-8"
        ) as file:
            placeholder_rego_str = file.read()
        container_regos = []
        for container_rego in container_policy_rego:
            container_regos.append(
                json.dumps(container_rego, separators=(",", ":"), sort_keys=True)
            )
        container_regos = ",".join(container_regos)
        return placeholder_rego_str % (container_regos)

    def _get_spark_pod_policy(
        self,
        driver_sidecars: List[Sidecar],
        executor_sidecars: List[Sidecar],
    ) -> dict:
        if self._allow_all:
            logger.warning(
                "Allow all mode is enabled. This should only be used for development purposes."
            )
            allow_all_rego_policy = base64.b64decode(
                Constants.ALLOW_ALL_POLICY_BASE64
            ).decode("utf-8")
            return {
                "driver": Policy(
                    rego=allow_all_rego_policy,
                    rego_base64=Constants.ALLOW_ALL_POLICY_BASE64,
                    host_data=Constants.ALLOW_ALL_POLICY_HASH,
                ),
                "executor": Policy(
                    rego=allow_all_rego_policy,
                    rego_base64=Constants.ALLOW_ALL_POLICY_BASE64,
                    host_data=Constants.ALLOW_ALL_POLICY_HASH,
                ),
            }

        driver_rego_policy = self._get_rego_policy(
            [self._driver_policy] + [x.virtual_node_policy for x in driver_sidecars]
        )
        executor_rego_policy = self._get_rego_policy(
            [self._executor_policy] + [x.virtual_node_policy for x in executor_sidecars]
        )
        driver_policy_hash = hashlib.sha256(
            bytes(driver_rego_policy, "utf-8")
        ).hexdigest()
        executor_policy_hash = hashlib.sha256(
            bytes(executor_rego_policy, "utf-8")
        ).hexdigest()

        return {
            "driver": Policy(
                rego=driver_rego_policy,
                rego_base64=base64.b64encode(bytes(driver_rego_policy, "utf-8")).decode(
                    "utf-8"
                ),
                host_data=driver_policy_hash,
            ),
            "executor": Policy(
                rego=executor_rego_policy,
                rego_base64=base64.b64encode(
                    bytes(executor_rego_policy, "utf-8")
                ).decode("utf-8"),
                host_data=executor_policy_hash,
            ),
        }

    def _get_driver(self, driver_settings: DriverSettings) -> tuple[Driver, dict]:
        driver = Driver(
            cores=math.ceil(driver_settings.cores),
            memory=driver_settings.memory,
            volumeMounts=[],
            sidecars=[],
            configMaps=[],
            labels={},
            annotations={},
            env=[],
            nodeSelector={},
            initContainers=[],
            serviceAccount=driver_settings.service_account,
            securityContext=k8smodels.V1SecurityContext(
                privileged=True,
            ),
        )

        if self._env_vars:
            driver.env.extend(
                [
                    k8smodels.V1EnvVar(name=env_iter.key, value=env_iter.value)
                    for env_iter in self._env_vars
                ]
            )
        trace_context_json = json.dumps(self._trace_context)
        driver.env.append(
            k8smodels.V1EnvVar(
                name=Constants.OTEL_TRACE_CONTEXT_ENV_KEY,
                value=base64.b64encode(trace_context_json.encode()).decode(),
            )
        )
        driver_policy_rego: dict = {}

        # For the allow all (insecure) scenario there is no policy document to download and parse.
        if self._allow_all:
            return (driver, driver_policy_rego)

        driver_policy_document = self._get_spark_app_policy_document()
        node = "policy"
        if self._debug_mode:
            node = "policyDebug"
        if driver_policy_document:
            driver_policy_rego = driver_policy_document["driver"][node]
            driver_policy_rego = replace_vars(
                json.dumps(driver_policy_rego),
                {
                    "containerRegistryUrl": self._image,
                    "jobId": self._app_name,
                },
            )

            for env_iter in self._env_vars:
                if env_iter.isMeasured:
                    driver_policy_rego["env_rules"].append(
                        {
                            "pattern": f"{env_iter.key}={env_iter.value}",
                            "required": False,
                            "strategy": "string",
                        }
                    )
                else:
                    driver_policy_rego["env_rules"].append(
                        {
                            "pattern": f"{env_iter.key}=.*",
                            "required": False,
                            "strategy": "re2",
                        }
                    )
            driver_policy_rego["env_rules"].append(
                {
                    "pattern": f"{Constants.OTEL_TRACE_CONTEXT_ENV_KEY}=.+",
                    "required": False,
                    "strategy": "re2",
                }
            )
            if self._governance_settings:
                driver_policy_rego["command"].extend(["--governance-port", "8300"])

        return (driver, driver_policy_rego)

    def _get_executor(
        self, executor_settings: ExecutorSettings
    ) -> tuple[Executor, DynamicAllocationProfile, dict]:
        # The documentation mentions that we can skip the instances in the Executor Settings, but
        # the spark runtime shows an error if this is not set.
        # Setting the value to that of the initial executor count to keep it consistent.
        executor = Executor(
            instances=executor_settings.instances.min,
            cores=math.ceil(executor_settings.cores),
            memory=executor_settings.memory,
            deleteOnTermination=executor_settings.delete_on_termination,
            volumeMounts=[],
            sidecars=[],
            configMaps=[],
            labels={},
            annotations={},
            env=[],
            nodeSelector={},
            initContainers=[],
            securityContext=k8smodels.V1SecurityContext(
                privileged=True,
            ),
        )
        allocation_profile = DynamicAllocationProfile(
            enabled=True,
            maxExecutors=executor_settings.instances.max,
            minExecutors=executor_settings.instances.min,
        )
        if self._env_vars:
            executor.env.extend(
                [
                    k8smodels.V1EnvVar(name=env_iter.key, value=env_iter.value)
                    for env_iter in self._env_vars
                ]
            )

        trace_context_json = json.dumps(self._trace_context)
        executor.env.append(
            k8smodels.V1EnvVar(
                name=Constants.OTEL_TRACE_CONTEXT_ENV_KEY,
                value=base64.b64encode(trace_context_json.encode()).decode(),
            )
        )

        executor_policy_rego: dict = {}
        # For the allow all (insecure) scenario there is no policy document to download and parse.
        if self._allow_all:
            return (executor, allocation_profile, executor_policy_rego)

        executor_policy_document = self._get_spark_app_policy_document()
        if executor_policy_document:
            node = "policy"
            if self._debug_mode:
                node = "policyDebug"
            executor_policy_rego = executor_policy_document["executor"][node]
            executor_policy_rego = replace_vars(
                json.dumps(executor_policy_rego),
                {
                    "imageUrl": self._image,
                    "jobId": self._app_name,
                },
            )

            for env_iter in self._env_vars:
                if env_iter.isMeasured:
                    executor_policy_rego["env_rules"].append(
                        {
                            "pattern": f"{env_iter.key}={env_iter.value}",
                            "required": False,
                            "strategy": "string",
                        }
                    )
                else:
                    executor_policy_rego["env_rules"].append(
                        {
                            "pattern": f"{env_iter.key}=.*",
                            "required": False,
                            "strategy": "re2",
                        }
                    )
            executor_policy_rego["env_rules"].append(
                {
                    "pattern": f"{Constants.OTEL_TRACE_CONTEXT_ENV_KEY}=.+",
                    "required": False,
                    "strategy": "re2",
                }
            )

        return (executor, allocation_profile, executor_policy_rego)

    def _configure_spark_monitoring_plugin(self, spark_conf: dict):
        spark_conf[SparkMonitoringConstants.SPARK_PLUGINS_KEY] = (
            SparkMonitoringConstants.SPARK_MONITORING_AGENT_CLASS_NAME
        )
        otel_jars = ":".join(
            [SparkMonitoringConstants.SPARK_MONITORING_PLUGIN_JAR_PATH]
            + SparkMonitoringConstants.SPARK_OPENTELEMETRY_JARS
        )
        spark_conf[SparkMonitoringConstants.SPARK_EXTRA_CLASSPATH_KEY_DRIVER] = (
            otel_jars
        )
        spark_conf[SparkMonitoringConstants.SPARK_EXTRA_CLASSPATH_KEY_EXECUTOR] = (
            otel_jars
        )

        self._driver.javaOptions = (
            f"-javaagent:{SparkMonitoringConstants.SPARK_JAVAAGENT_PATH}"
        )

        self._executor.javaOptions = (
            f"-javaagent:{SparkMonitoringConstants.SPARK_JAVAAGENT_PATH}"
        )

        cur_baggage = otel_utilities.get_current_baggage()
        baggage_str = ",".join([f"{key}={value}" for key, value in cur_baggage.items()])

        self._driver.env.extend(
            [
                k8smodels.V1EnvVar(
                    name="OTEL_EXPORTER_OTLP_ENDPOINT",
                    value="http://localhost:4317",
                ),
                k8smodels.V1EnvVar(
                    name="OTEL_EXPORTER_OTLP_PROTOCOL",
                    value="grpc",
                ),
                k8smodels.V1EnvVar(
                    name="OTEL_SERVICE_NAME",
                    value=self._app_name + "-driver",
                ),
                k8smodels.V1EnvVar(
                    name="OTEL_RESOURCE_ATTRIBUTES",
                    value=f"spark.role=driver,{baggage_str}",
                ),
            ]
        )
        self._executor.env.extend(
            [
                k8smodels.V1EnvVar(
                    name="OTEL_EXPORTER_OTLP_ENDPOINT",
                    value="http://localhost:4317",
                ),
                k8smodels.V1EnvVar(
                    name="OTEL_EXPORTER_OTLP_PROTOCOL",
                    value="grpc",
                ),
                k8smodels.V1EnvVar(
                    name="OTEL_SERVICE_NAME",
                    value=self._app_name + "-executor",
                ),
                k8smodels.V1EnvVar(
                    name="OTEL_RESOURCE_ATTRIBUTES",
                    value=f"spark.role=executor,{baggage_str}",
                ),
            ]
        )

    def _get_spark_app_policy_document(self):
        temp_dir = tempfile.gettempdir()

        if not self._policy_file:
            return

        policy_document_url = self._policy_file
        insecure = self._cleanroom_settings.use_http
        import threading

        lock = threading.Lock()
        if not os.path.exists(
            os.path.join(temp_dir, "cleanroom-spark-analytics-app-security-policy.yaml")
        ):
            with lock:
                if not os.path.exists(
                    os.path.join(
                        temp_dir, "cleanroom-spark-analytics-app-security-policy.yaml"
                    )
                ):
                    os.makedirs(temp_dir, exist_ok=True)
                    client = oras.client.OrasClient(insecure=insecure)
                    client.pull(
                        target=policy_document_url,
                        outdir=temp_dir,
                    )

        with open(
            os.path.join(temp_dir, "cleanroom-spark-analytics-app-security-policy.yaml")
        ) as f:
            return yaml.safe_load(f)
