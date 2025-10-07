import os
from enum import StrEnum
from typing import Optional

from pydantic import BaseModel, Field

DEFAULT_MCR_URL = "mcr.microsoft.com/azurecleanroom"
DEFAULT_MCR_VERSION = "3.0.0"


class SparkComputeProvider(StrEnum):
    ConfidentialVirtualNode = "confidential-virtual-node"
    Kubernetes = "kubernetes"


class DriverSettings(BaseModel):
    cores: float = Field(alias="cores")
    memory: str = Field(alias="memory")
    service_account: str = Field(alias="serviceAccount", default="spark-operator-spark")


class ExecutorSettings(BaseModel):
    cores: float = Field(alias="cores")
    instances: int = Field(alias="instances")
    memory: str = Field(alias="memory")
    delete_on_termination: bool = Field(alias="deleteOnTermination", default=True)


class CleanroomSettings(BaseModel):
    registry_url: str = Field(
        alias="registryUrl",
        default=os.environ.get("CLEANROOM_CONTAINER_REGISTRY_URL") or DEFAULT_MCR_URL,
    )
    sidecars_policy_document_registry_url: str = Field(
        alias="sidecarsPolicyDocumentRegistryUrl",
        default=os.environ.get("CLEANROOM_SIDECARS_POLICY_DOCUMENT_REGISTRY_URL")
        or os.environ.get("CLEANROOM_CONTAINER_REGISTRY_URL")
        or DEFAULT_MCR_URL,
    )
    versions_document: str = Field(
        alias="versionsDocument",
        default=os.environ.get("CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL")
        or f"{DEFAULT_MCR_URL}/sidecar-digests:{DEFAULT_MCR_VERSION}",
    )
    use_http: bool = Field(
        alias="useHttp",
        default=(
            os.environ.get("CLEANROOM_CONTAINER_REGISTRY_USE_HTTP") or "false"
        ).lower()
        == "true",
    )


class SkuSettings(BaseModel):
    driver: DriverSettings = Field(alias="driver")
    executor: ExecutorSettings = Field(alias="executor")


class AnalyticsSettings(BaseModel):
    namespace: str = Field(alias="namespace", default="analytics")
    image: str = Field(
        alias="image",
        default=os.environ.get(
            "CLEANROOM_SPARK_ANALYTICS_IMAGE_URL",
            f"{DEFAULT_MCR_URL}/workloads/cleanroom-spark-analytics-app:{DEFAULT_MCR_VERSION}",
        ),
    )
    policy_file: str = Field(
        alias="policyFile",
        default=os.environ.get(
            "CLEANROOM_SPARK_ANALYTICS_POLICY_FILE",
            f"{DEFAULT_MCR_URL}/policies/cleanroom-spark-analytics-app-policy:{DEFAULT_MCR_VERSION}",
        ),
    )
    debug_mode: bool = Field(alias="debugMode", default=False)
    allow_all: bool = Field(alias="allowAll", default=False)
    application_file: str = Field(
        alias="applicationFile", default="local:///app/src/main.py"
    )
    sql: SkuSettings = Field(alias="sql")


class ExamplesSettings(BaseModel):
    namespace: str = Field(alias="namespace", default="analytics")
    image: str = Field(
        alias="image",
        default="spark:3.5.5",
    )
    allow_all: bool = Field(alias="allowAll", default=True)
    application_file: str = Field(
        alias="applicationFile",
        default="local:///opt/spark/examples/src/main/python/pi.py",
    )
    pi: SkuSettings = Field(
        alias="pi",
        default=SkuSettings(
            driver=DriverSettings(
                cores=1.0, memory="512m", serviceAccount="spark-operator-spark"
            ),
            executor=ExecutorSettings(cores=1.0, instances=1, memory="512m"),
        ),
    )


class ApplicationSettings(BaseModel):
    analytics: AnalyticsSettings = Field(alias="analytics")
    examples: ExamplesSettings = Field(
        alias="examples", default_factory=ExamplesSettings
    )


class SparkResourceSettings(BaseModel):
    group: str = Field(alias="group", default="sparkoperator.k8s.io")
    version: str = Field(alias="version", default="v1beta2")
    kind: str = Field(alias="kind", default="sparkapplication")
    plural: str = Field(alias="plural", default="sparkapplications")


class TelemetrySettings(BaseModel):
    telemetry_collection_enabled: bool = Field(
        alias="telemetryCollectionEnabled", default=False
    )
    prometheus_endpoint: Optional[str] = Field(alias="prometheusEndpoint", default="")
    loki_endpoint: Optional[str] = Field(alias="lokiEndpoint", default="")
    tempo_endpoint: Optional[str] = Field(alias="tempoEndpoint", default="")


class ServiceSettings(BaseModel):
    name: str = Field(alias="name", default="cleanroom-spark-frontend")
    namespace: str = Field(alias="namespace", default="cleanroom-spark-frontend")
    telemetry: TelemetrySettings = Field(
        alias="telemetry", default_factory=TelemetrySettings
    )


class SparkSettings(BaseModel):
    compute_provider: SparkComputeProvider = Field(
        alias="computeProvider", default=SparkComputeProvider.ConfidentialVirtualNode
    )
    resource: SparkResourceSettings = Field(
        alias="resource", default_factory=SparkResourceSettings
    )


class Configuration(BaseModel):
    cleanroom: CleanroomSettings = Field(
        alias="cleanroom", default_factory=CleanroomSettings
    )
    spark: SparkSettings = Field(alias="spark", default_factory=SparkSettings)
    applications: ApplicationSettings = Field(alias="applications")
    service: ServiceSettings = Field(alias="service", default_factory=ServiceSettings)
