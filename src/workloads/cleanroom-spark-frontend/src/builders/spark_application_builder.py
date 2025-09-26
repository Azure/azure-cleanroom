import base64
import hashlib
import json
import logging
import math
import os
import re
import tempfile
from collections import namedtuple
from string import Template
from typing import List, Tuple
from urllib.parse import urlparse

import oras.client
import yaml
from kubernetes.client import models as k8smodels
from opentelemetry import context
from opentelemetry.trace.propagation.tracecontext import TraceContextTextMapPropagator
from src.builders.i_spark_application_builder import (
    ISparkApplicationBuilder,
    ISparkApplicationBuilderWithApplication,
    ISparkApplicationBuilderWithDriver,
    ISparkApplicationBuilderWithExecutor,
    ISparkApplicationBuilderWithImage,
    ISparkApplicationBuilderWithMeta,
    ISparkApplicationBuilderWithName,
    ISparkApplicationBuilderWithSpec,
)
from src.config.configuration import (
    CleanroomSettings,
    DriverSettings,
    ExecutorSettings,
    TelemetrySettings,
)
from src.models.cleanroom_spark_application import CleanRoomSparkApplication, Policy
from src.models.input_models import *
from src.models.model import AccessPointType, Identity, ProtocolType, ResourceType
from src.models.spark_application_models import (
    DeployMode,
    Driver,
    Executor,
    SparkApplicationSpec,
)
from src.utilities.constants import Constants

VOLUMESTATUS_MOUNT_PATH = "/mnt/volumestatus"
TELEMETRY_MOUNT_PATH = "/mnt/telemetry"

g_sidecar_versions = {}
logger = logging.getLogger("spark_application_builder")


def to_spark_app_name(name: str) -> str:
    """
    Convert a name to a valid Spark application name.
    Spark application names must be lowercase and can only contain alphanumeric characters and hyphens.
    """
    name = "cl-spark-" + re.sub(r"[^a-z0-9-]", "-", name.lower())
    return name[:63]


class Sidecar:
    def __init__(self, container: k8smodels.V1Container, virtual_node_policy: str):
        self.container = container
        self.virtual_node_policy = virtual_node_policy


def replace_vars(content: str, vars: dict):
    spec = Template(content).substitute(vars)
    return json.loads(spec)


class SparkApplicationBuilder(
    ISparkApplicationBuilder,
    ISparkApplicationBuilderWithName,
    ISparkApplicationBuilderWithApplication,
    ISparkApplicationBuilderWithImage,
    ISparkApplicationBuilderWithMeta,
    ISparkApplicationBuilderWithSpec,
    ISparkApplicationBuilderWithDriver,
    ISparkApplicationBuilderWithExecutor,
):
    def __init__(
        self,
        cleanroom_settings: CleanroomSettings,
    ):
        self._app_name = None
        self._image = None
        self._contract_id = None
        self._cleanroom_settings = cleanroom_settings
        self._governance_settings: GovernanceSettings = None
        self._main_application_file = None
        self._policy_file = None
        self._driver: Driver = None
        self._driver_policy: dict = None
        self._sidecars: List[Sidecar] = []
        self._executor: Executor = None
        self._executor_policy: dict = None
        self._datasets: List[DatasetInfo] = []
        self._datasink: DatasetInfo = None
        self._telemetry: TelemetrySettings = TelemetrySettings()
        self._debug_mode: bool = False
        self._allow_all: bool = False
        self._governance_required = False

    def CreateBuilder(self):
        return self

    def WithName(self, name: str):
        if not name:
            raise ValueError("Application name cannot be empty")
        # Sanitize the name to support k8s standards
        self._app_name = name.lower().replace(" ", "-")
        return self

    def WithType(self, app_type: str):
        self._app_type = app_type
        return self

    def WithImage(self, image: str):
        self._image = image
        return self

    def WithPolicy(
        self, policy_file: str, debug_mode: bool = False, allow_all: bool = False
    ):
        self._policy_file = policy_file
        self._debug_mode = debug_mode
        self._allow_all = allow_all
        return self

    def WithMainApplicationFile(self, main_application_file: str):
        self._main_application_file = main_application_file
        return self

    def WithEnvVars(self, env_vars: list[EnvData]):
        self._env_vars = env_vars
        return self

    def WithArguments(self, arguments: List[str]):
        self._arguments = arguments
        return self

    def WithTelemetry(self, settings: TelemetrySettings):
        self._telemetry = settings
        return self

    def WithGovernance(self, contract_id: str, governance_settings: GovernanceSettings):
        if (
            governance_settings.cert_base64 is None
            and governance_settings.service_cert_discovery is None
        ):
            raise ValueError(
                "Either cert_base64 or service_cert_discovery must be provided in governance settings."
            )
        self._governance_required = True
        self._contract_id = contract_id
        self._governance_settings = governance_settings
        return self

    def AddDriver(self, settings: DriverSettings):
        self._driver, self._driver_policy = self._get_driver(settings)
        return self

    def AddExecutor(self, settings: ExecutorSettings):
        self._executor, self._executor_policy = self._get_executor(settings)
        return self

    def AddDataset(self, dataset: DatasetInfo):
        self._datasets.append(dataset)
        return self

    def AddDatasink(self, datasink: DatasetInfo):
        self._datasink = datasink
        return self

    def Build(self) -> CleanRoomSparkApplication:
        if not self._app_name or not self._image or not self._main_application_file:
            raise ValueError("Missing required fields to build SparkApplication")

        sidecars: List[Sidecar] = []
        blobfuse_sidecars: List[Sidecar] = []
        s3fs_sidecars: List[Sidecar] = []
        volumes: List[k8smodels.V1Volume] = []
        identities: List[Tuple[Identity, str]] = []

        # The spark-local-dir-1 volume is required to ensure that the Spark Operator does not create
        # a new volume with a random name for each Spark application. This ensures that we can measure
        # the mount point by keeping it consistent across jobs.
        volumes.append(k8smodels.V1Volume(name="spark-local-dir-1", empty_dir={}))
        volumes.append(k8smodels.V1Volume(name="remotemounts", empty_dir={}))
        volumes.append(k8smodels.V1Volume(name="telemetrymounts", empty_dir={}))
        volumes.append(k8smodels.V1Volume(name="volumestatusmounts", empty_dir={}))
        volumes.append(k8smodels.V1Volume(name="uds", empty_dir={}))

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
            k8smodels.V1VolumeMount(name="uds", mount_path="/mnt/uds", read_only=False),
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
        if self._telemetry.telemetry_collection_enabled:
            sidecars.append(
                self._get_otel_sidecar(
                    TELEMETRY_MOUNT_PATH,
                )
            )

        if self._governance_required:
            assert (
                self._governance_settings is not None
            ), "Governance settings are required."
            assert self._contract_id is not None, "Contract ID is required."
            sidecars.append(
                self._get_attestation_sidecar(
                    TELEMETRY_MOUNT_PATH,
                )
            )
            sidecars.append(
                self._get_ccr_governance_sidecar(
                    self._contract_id,
                    self._governance_settings,
                    TELEMETRY_MOUNT_PATH,
                )
            )

        if self._datasets:
            assert self._contract_id is not None, "Contract ID is required."
            for dataset in self._datasets:
                if dataset.accessPoint.store.type == ResourceType.Azure_BlobStorage:
                    identities.append(
                        (
                            dataset.accessPoint.identity,
                            self._contract_id + "-" + dataset.ownerId,
                        )
                    )
                    blobfuse_sidecars.append(
                        self._get_blobfuse_sidecar(
                            dataset.accessPoint,
                            "/mnt/remote",
                            dataset.name,
                            TELEMETRY_MOUNT_PATH,
                            VOLUMESTATUS_MOUNT_PATH,
                        )
                    )
                elif dataset.accessPoint.store.type == ResourceType.Aws_S3:
                    s3fs_sidecars.append(
                        self._get_s3fs_sidecar(
                            dataset.accessPoint,
                            "/mnt/remote",
                            dataset.name,
                            TELEMETRY_MOUNT_PATH,
                            VOLUMESTATUS_MOUNT_PATH,
                        )
                    )
                else:
                    raise ValueError(
                        f"Unsupported store type {dataset.accessPoint.store.type} for dataset {dataset.name}."
                    )

        if self._datasink:
            assert self._contract_id is not None, "Contract ID is required."
            if self._datasink.accessPoint.store.type == ResourceType.Azure_BlobStorage:
                identities.append(
                    (
                        self._datasink.accessPoint.identity,
                        self._contract_id + "-" + self._datasink.ownerId,
                    )
                )
                blobfuse_sidecars.append(
                    self._get_blobfuse_sidecar(
                        self._datasink.accessPoint,
                        "/mnt/remote",
                        self._datasink.name,
                        TELEMETRY_MOUNT_PATH,
                        VOLUMESTATUS_MOUNT_PATH,
                    )
                )
            elif self._datasink.accessPoint.store.type == ResourceType.Aws_S3:
                s3fs_sidecars.append(
                    self._get_s3fs_sidecar(
                        self._datasink.accessPoint,
                        "/mnt/remote",
                        self._datasink.name,
                        TELEMETRY_MOUNT_PATH,
                        VOLUMESTATUS_MOUNT_PATH,
                    )
                )
            else:
                raise ValueError(
                    f"Unsupported store type {self._datasink.accessPoint.store.type} for dataset {self._datasink.name}."
                )

        if identities:
            sidecars.append(
                self._get_identity_sidecar(
                    identities,
                    "api://AzureADTokenExchange",
                    TELEMETRY_MOUNT_PATH,
                )
            )

        # Blobfuse sidecars are added after the identity sidecar to ensure that
        # the identity sidecar is ready before the blobfuse sidecars start.
        if blobfuse_sidecars:
            sidecars.extend(blobfuse_sidecars)

        if s3fs_sidecars:
            sidecars.extend(s3fs_sidecars)

        # Calculate full CCE policy.
        spark_pod_policy = self._get_spark_pod_policy(sidecars)

        containers = [x.container for x in sidecars]
        self._driver.initContainers = containers
        self._executor.initContainers = containers
        return CleanRoomSparkApplication(
            SparkApplicationSpec(
                type=self._app_type,
                name=to_spark_app_name(self._app_name),
                mode=DeployMode.cluster,
                arguments=self._arguments if self._arguments else [],
                volumes=volumes,
                sparkVersion="3.5.5",
                image=self._image,
                mainApplicationFile=self._main_application_file,
                driver=self._driver,
                executor=self._executor,
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

    def _get_spark_pod_policy(self, sidecars: List[Sidecar]):
        sidecar_rego_policies = []
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

        for sidecar in sidecars:
            sidecar_rego_policies.append(sidecar.virtual_node_policy)
        driver_rego_policy = self._get_rego_policy(
            [self._driver_policy] + sidecar_rego_policies
        )
        executor_rego_policy = self._get_rego_policy(
            [self._executor_policy] + sidecar_rego_policies
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

    def _get_driver(self, driver_settings: DriverSettings) -> (Driver, dict):
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

        otel_trace_context = {}
        cur_context = context.get_current()
        TraceContextTextMapPropagator().inject(otel_trace_context, cur_context)

        if otel_trace_context:
            trace_context_json = json.dumps(otel_trace_context)
            driver.env.append(
                k8smodels.V1EnvVar(
                    name=Constants.OTEL_TRACE_CONTEXT_ENV_KEY,
                    value=base64.b64encode(trace_context_json.encode()).decode(),
                )
            )
        driver_policy_rego = None

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

        return (driver, driver_policy_rego)

    def _get_executor(self, executor_settings: ExecutorSettings) -> (Executor, dict):
        executor = Executor(
            cores=math.ceil(executor_settings.cores),
            instances=executor_settings.instances,
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

        if self._env_vars:
            executor.env.extend(
                [
                    k8smodels.V1EnvVar(name=env_iter.key, value=env_iter.value)
                    for env_iter in self._env_vars
                ]
            )

        otel_trace_context = {}
        cur_context = context.get_current()
        TraceContextTextMapPropagator().inject(otel_trace_context, cur_context)

        if otel_trace_context:
            trace_context_json = json.dumps(otel_trace_context)
            executor.env.append(
                k8smodels.V1EnvVar(
                    name=Constants.OTEL_TRACE_CONTEXT_ENV_KEY,
                    value=base64.b64encode(trace_context_json.encode()).decode(),
                )
            )

        executor_policy_rego = None
        # For the allow all (insecure) scenario there is no policy document to download and parse.
        if self._allow_all:
            return (executor, executor_policy_rego)

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

        return (executor, executor_policy_rego)

    def _get_container_registry_url(self) -> str:
        return self._cleanroom_settings.registry_url

    def _get_sidecars_version(self):
        import tempfile

        import oras.client
        import yaml

        # Download the sidecar versions document.
        temp_dir = tempfile.gettempdir()

        import threading

        lock = threading.Lock()
        if not os.path.exists(os.path.join(temp_dir, "sidecar-digests.yaml")):
            with lock:
                if not os.path.exists(os.path.join(temp_dir, "sidecar-digests.yaml")):
                    versions_registry_url = self._cleanroom_settings.versions_document
                    logger.warning(
                        f"Using cleanroom containers versions registry: {versions_registry_url}"
                    )

                    insecure = self._cleanroom_settings.use_http
                    client = oras.client.OrasClient(insecure=insecure)
                    client.pull(
                        target=versions_registry_url,
                        outdir=temp_dir,
                    )

        with open(os.path.join(temp_dir, "sidecar-digests.yaml")) as f:
            sidecars_version = yaml.safe_load(f)
        return sidecars_version

    def _get_s3fs_sidecar(
        self,
        access_point: AccessPoint,
        mount_path,
        access_name,
        telemetry_mount_path,
        volume_status_mount_path,
    ) -> Sidecar:
        # Sanitize access name to remove spaces and convert to lowercase and replace underscores with dashes.
        access_name = access_name.lower().replace(" ", "").replace("_", "-")
        assert (
            access_point.store.provider.configuration
        ), f"Store provider configuration is null for {access_name}."
        aws_config = json.loads(
            base64.b64decode(access_point.store.provider.configuration).decode()
        )
        aws_config_secret_id = aws_config["secretId"]
        awsUrl = access_point.store.provider.url
        awsBucketName = access_point.store.name

        otel_trace_context = {}
        TraceContextTextMapPropagator().inject(
            otel_trace_context, context.get_current()
        )

        if otel_trace_context:
            trace_context_json_b64 = base64.b64encode(
                (json.dumps(otel_trace_context).encode())
            ).decode()
        else:
            trace_context_json_b64 = ""

        assert (
            aws_config_secret_id is not None
        ), f"AWS config secret Id value is not set for {access_name}."

        s3fs_sidecar_replacement_vars = {
            "datasetName": access_name,
            "awsBucketName": awsBucketName,
            "readOnly": (
                "--read-only"
                if access_point.type == AccessPointType.Volume_ReadOnly
                else "--no-read-only"
            ),
            "mountPath": mount_path,
            "cgsAwsS3ConfigSecret": aws_config_secret_id,
            "awsUrl": awsUrl,
            "telemetryMountPath": telemetry_mount_path,
            "volumeStatusMountPath": volume_status_mount_path,
            "traceContextJsonBase64": trace_context_json_b64,
        }

        sidecar = self._get_sidecar(
            "s3fs-launcher",
            sidecar_replacement_vars=s3fs_sidecar_replacement_vars,
        )

        return sidecar

    def _get_blobfuse_sidecar(
        self,
        access_point: AccessPoint,
        mount_path,
        access_name,
        telemetry_mount_path,
        volume_status_mount_path,
    ) -> Sidecar:
        # Sanitize access name to remove spaces and convert to lowercase and replace underscores with dashes.
        access_name = access_name.lower().replace(" ", "").replace("_", "-")
        encryption_config = json.loads(
            base64.b64decode(access_point.protection.configuration).decode()
        )
        encryption_mode = encryption_config["EncryptionMode"]

        # TODO (HPrabh): Add support for CSE.
        assert (
            encryption_mode != "CSE"
        ), f"Encryption mode {encryption_mode} is not supported for {access_name}."
        if encryption_mode == "CPK":
            assert (
                access_point.protection.encryptionSecrets
            ), f"Encryption secrets is null for {access_name}."
            dek_entry = access_point.protection.encryptionSecrets.dek
            assert (
                dek_entry.secret.backingResource.type == ResourceType.Cgs
            ), f"Expecting DEK secret backing resource type as '{str(ResourceType.Cgs)}' but value is '{dek_entry.secret.backingResource.type}'."
        storage_account_name = urlparse(access_point.store.provider.url).hostname.split(
            "."
        )[0]
        subdirectory = ""
        storageBlobEndpoint = access_point.store.provider.url
        storageContainerName = access_point.store.name
        is_onelake = False
        if access_point.store.provider.protocol == ProtocolType.Azure_OneLake:
            is_onelake = True
            storage_account_name = "onelake"
            parsed_onelake_url = urlparse(access_point.store.provider.url)
            storageBlobEndpoint = parsed_onelake_url.hostname
            storageContainerName = parsed_onelake_url.path.split("/")[1]
            subdirectory = "/".join(parsed_onelake_url.path.split("/")[2:])

        otel_trace_context = {}
        TraceContextTextMapPropagator().inject(
            otel_trace_context, context.get_current()
        )

        if otel_trace_context:
            trace_context_json_b64 = base64.b64encode(
                (json.dumps(otel_trace_context).encode())
            ).decode()
        else:
            trace_context_json_b64 = ""

        blobfuse_sidecar_replacement_vars = {
            "datasetName": access_name,
            "storageContainerName": storageContainerName,
            "mountPath": mount_path,
            "maaUrl": "",
            "storageAccountName": storage_account_name,
            "storageBlobEndpoint": storageBlobEndpoint,
            "readOnly": (
                "--read-only"
                if access_point.type == AccessPointType.Volume_ReadOnly
                else "--no-read-only"
            ),
            "kekVaultUrl": "",
            "kekKid": "",
            "dekVaultUrl": "",
            "dekSecretName": "",
            "cgsDekSecretId": (
                f"{dek_entry.secret.backingResource.name}"
                if encryption_mode == "CPK"
                else ""
            ),
            "clientId": access_point.identity.clientId,
            "tenantId": access_point.identity.tenantId,
            "encryptionMode": encryption_mode,
            "useAdls": ("--use-adls" if is_onelake else "--no-use-adls"),
            "telemetryMountPath": telemetry_mount_path,
            "volumeStatusMountPath": volume_status_mount_path,
            "traceContextJsonBase64": trace_context_json_b64,
        }

        sidecar = self._get_sidecar(
            "blobfuse-launcher",
            sidecar_replacement_vars=blobfuse_sidecar_replacement_vars,
        )

        if subdirectory != "":
            sidecar.container.command.append("--sub-directory")
            sidecar.container.command.append(subdirectory)
            sidecar.virtual_node_policy["command"].extend(
                ["--sub-directory", subdirectory]
            )
        return sidecar

    def _get_identity_sidecar(
        self,
        identities: List[Tuple[Identity, str]],
        audience: str,
        telemetry_mount_path: str,
    ) -> Sidecar:
        identity_args = {
            "Identities": {"ManagedIdentities": [], "ApplicationIdentities": []}
        }

        for identity, subject in identities:
            if identity.tokenIssuer.issuerType == "AttestationBasedTokenIssuer":
                if (
                    identity.tokenIssuer.issuer.protocol
                    == ProtocolType.AzureAD_ManagedIdentity
                ):
                    if identity.clientId not in [
                        i["ClientId"]
                        for i in identity_args["Identities"]["ManagedIdentities"]
                    ]:
                        identity_args["Identities"]["ManagedIdentities"].append(
                            {"ClientId": identity.clientId}
                        )
            elif identity.tokenIssuer.issuerType == "FederatedIdentityBasedTokenIssuer":
                if identity.clientId not in [
                    i["ClientId"]
                    for i in identity_args["Identities"]["ApplicationIdentities"]
                ]:
                    identity_args["Identities"]["ApplicationIdentities"].append(
                        {
                            "ClientId": identity.clientId,
                            "Credential": {
                                "CredentialType": "FederatedCredential",
                                "FederationConfiguration": {
                                    "IdTokenEndpoint": "http://localhost:8300",
                                    "Subject": subject,
                                    "Audience": audience,
                                    "Issuer": identity.tokenIssuer.issuer.url,
                                },
                            },
                        }
                    )

        identity_args_base64 = base64.b64encode(
            bytes(json.dumps(identity_args), "utf-8")
        ).decode("utf-8")

        replace_vars = {
            "IdentitySidecarArgsBase64": identity_args_base64,
            "OtelMetricExportInterval": "5000",
            "telemetryMountPath": telemetry_mount_path,
        }
        return self._get_sidecar("identity", replace_vars)

    def _get_attestation_sidecar(
        self,
        telemetry_mount_path: str,
    ) -> Sidecar:

        return self._get_sidecar(
            "ccr-attestation", {"telemetryMountPath": telemetry_mount_path}
        )

    def _get_ccr_governance_sidecar(
        self,
        contract_id: str,
        governance_settings: GovernanceSettings,
        telemetry_mount_path: str,
    ) -> Sidecar:
        cgs_endpoint = governance_settings.service_url

        replace_vars = {
            "cgsEndpoint": cgs_endpoint,
            "contractId": contract_id,
            "telemetryMountPath": telemetry_mount_path,
        }
        if governance_settings.cert_base64:
            replace_vars["serviceCertBase64"] = governance_settings.cert_base64
        else:
            replace_vars["serviceCertBase64"] = ""

        if governance_settings.service_cert_discovery:
            replace_vars["serviceCertDiscoveryEndpoint"] = (
                governance_settings.service_cert_discovery.certificate_discovery_endpoint
            )
            replace_vars["serviceCertDiscoverySnpHostData"] = (
                governance_settings.service_cert_discovery.host_data[0]
            )
            replace_vars["serviceCertDiscoverySkipDigestCheck"] = (
                governance_settings.service_cert_discovery.skip_digest_check
            )
            replace_vars["serviceCertDiscoveryConstitutionDigest"] = (
                governance_settings.service_cert_discovery.constitution_digest
            )
            replace_vars["serviceCertDiscoveryJsappBundleDigest"] = (
                governance_settings.service_cert_discovery.js_app_bundle_digest
            )
        else:
            replace_vars["serviceCertDiscoveryEndpoint"] = ""
            replace_vars["serviceCertDiscoverySnpHostData"] = ""
            replace_vars["serviceCertDiscoverySkipDigestCheck"] = ""
            replace_vars["serviceCertDiscoveryConstitutionDigest"] = ""
            replace_vars["serviceCertDiscoveryJsappBundleDigest"] = ""

        return self._get_sidecar("ccr-governance", replace_vars)

    def _get_otel_sidecar(
        self,
        telemetry_mount_path: str,
    ) -> Sidecar:

        return self._get_sidecar(
            "otel-collector",
            {
                "telemetryCollectionEnabled": self._telemetry.telemetry_collection_enabled,
                "telemetryPath": "",
                "telemetryMountPath": telemetry_mount_path,
                "prometheusEndpoint": self._telemetry.prometheus_endpoint,
                "lokiEndpoint": self._telemetry.loki_endpoint,
                "tempoEndpoint": self._telemetry.tempo_endpoint,
            },
        )

    def _get_sidecar(
        self, sidecar_name: str, sidecar_replacement_vars: dict
    ) -> Sidecar:
        sidecar = [
            x for x in self._get_sidecars_version() if x["image"] == sidecar_name
        ][0]
        sidecar_replacement_vars["containerRegistryUrl"] = (
            self._cleanroom_settings.registry_url
        )
        sidecar_replacement_vars["digest"] = sidecar["digest"]

        sidecar_policy_document = self._get_sidecar_policy_document(sidecar_name)
        sidecar_yaml = replace_vars(
            json.dumps(sidecar_policy_document["virtualNodeYaml"]),
            sidecar_replacement_vars,
        )
        node = "virtual_node_rego"
        if self._debug_mode:
            logger.warning(
                f"Using debug policy for sidecar {sidecar_name}. This should only be used for development purposes."
            )
            node = "virtual_node_rego_debug"
        sidecar_policy_rego = replace_vars(
            json.dumps(sidecar_policy_document["policy"][node]),
            sidecar_replacement_vars,
        )
        sidecar_obj = sidecar_yaml["spec"]["containers"][0]
        logger.info("Using sidecar image: %s", sidecar_obj["image"])
        sidecar_container = k8smodels.V1Container(
            name=sidecar_obj["name"],
            image=sidecar_obj["image"],
            command=sidecar_obj["command"],
            env=[
                k8smodels.V1EnvVar(name=env["name"], value=env["value"])
                for env in sidecar_obj.get("env", [])
            ],
            resources=k8smodels.V1ResourceRequirements(
                requests=sidecar_obj.get("resources", {}).get("requests", {}),
            ),
            volume_mounts=[
                k8smodels.V1VolumeMount(
                    name=vm["name"],
                    mount_path=vm["mountPath"],
                    read_only=vm.get("readOnly", False),
                    mount_propagation=vm.get("mountPropagation", "None"),
                )
                for vm in sidecar_obj.get("volumeMounts", [])
            ],
            security_context=k8smodels.V1SecurityContext(
                privileged=sidecar_obj.get("securityContext", {}).get(
                    "privileged", False
                ),
            ),
            # https://kubernetes.io/docs/concepts/workloads/pods/sidecar-containers/
            restart_policy="Always",
        )
        return Sidecar(sidecar_container, sidecar_policy_rego)

    def _get_sidecar_policy_document(self, imageName: str):
        # Download the sidecar policy document.
        temp_dir = tempfile.gettempdir()

        base_url = self._cleanroom_settings.sidecars_policy_document_registry_url

        sidecar = [x for x in self._get_sidecars_version() if x["image"] == imageName][
            0
        ]
        insecure = self._cleanroom_settings.use_http
        import threading

        lock = threading.Lock()
        if not os.path.exists(
            os.path.join(temp_dir, sidecar["policyDocument"] + ".yaml")
        ):
            with lock:
                if not os.path.exists(
                    os.path.join(temp_dir, sidecar["policyDocument"] + ".yaml")
                ):
                    os.makedirs(temp_dir, exist_ok=True)
                    policy_document_url = (
                        f"{base_url}/policies/"
                        + f"{sidecar['policyDocument']}@{sidecar['policyDocumentDigest']}"
                    )
                    logger.info("Pulling policy document: %s", policy_document_url)
                    client = oras.client.OrasClient(insecure=insecure)
                    client.pull(
                        target=policy_document_url,
                        outdir=temp_dir,
                    )

        with open(os.path.join(temp_dir, sidecar["policyDocument"] + ".yaml")) as f:
            return yaml.safe_load(f)

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
