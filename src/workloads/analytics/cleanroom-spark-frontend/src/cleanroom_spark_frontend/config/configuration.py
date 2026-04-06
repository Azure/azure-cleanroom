import os
from enum import StrEnum
from typing import List, Optional

from frontend_internal.models.input_models import (
    DEFAULT_MCR_URL,
    DEFAULT_MCR_VERSION,
    CleanroomSettings,
    TelemetrySettings,
)
from pydantic import BaseModel, Field


class SparkComputeProvider(StrEnum):
    ConfidentialVirtualNode = "confidential-virtual-node"
    Virtual = "virtual"


class DriverSettings(BaseModel):
    cores: float = Field(alias="cores")
    memory: str = Field(alias="memory")
    service_account: str = Field(alias="serviceAccount", default="spark-operator-spark")


class ExecutorInstanceSettings(BaseModel):
    min: int = Field(alias="min", default=1)
    max: int = Field(alias="max", default=1)


class ExecutorSettings(BaseModel):
    cores: float = Field(alias="cores")
    instances: ExecutorInstanceSettings = Field(
        alias="instances", default_factory=ExecutorInstanceSettings
    )
    memory: str = Field(alias="memory")
    delete_on_termination: bool = Field(alias="deleteOnTermination", default=True)


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
        default="spark:4.0.0",
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
            executor=ExecutorSettings(
                cores=1.0,
                instances=ExecutorInstanceSettings(min=1, max=1),
                memory="512m",
            ),
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


class OptimizerSettings(BaseModel):
    enabled: bool = Field(alias="enabled", default=False)
    endpoint: str = Field(
        alias="endpoint",
        default=os.environ.get(
            "AI_OPTIMIZER_ENDPOINT",
            "http://workspace-llama-3point1-8b-instruct.kaito-workspace.svc",
        ),
    )
    timeout: int = Field(alias="timeout", default=30)


class SchedulerSettings(BaseModel):
    node_selector: dict[str, str] = Field(
        alias="nodeSelector",
        default={"microsoft.containerinstance.virtualnode": "true"},
    )
    constraints: dict[str, str] = Field(
        alias="constraints", default={"POD_COUNT": "25"}
    )


class ServiceSettings(BaseModel):
    name: str = Field(alias="name", default="cleanroom-spark-frontend")
    namespace: str = Field(alias="namespace", default="cleanroom-spark-frontend")
    url: str = Field(alias="url", default="http://cleanroom-spark-frontend:8000")
    telemetry: TelemetrySettings = Field(
        alias="telemetry", default_factory=TelemetrySettings
    )
    optimizer: OptimizerSettings = Field(
        alias="optimizer", default_factory=OptimizerSettings
    )
    scheduler: SchedulerSettings = Field(
        alias="scheduler", default_factory=SchedulerSettings
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
