import datetime
from enum import StrEnum
from typing import Dict, List, Optional

from kubernetes.client import models as k8smodels
from pydantic import BaseModel


class NamePath(BaseModel):
    name: str
    path: str


# https://github.com/kubeflow/spark-operator/blob/master/docs/api-docs.md#sparkoperator.k8s.io/v1beta2.SparkPodSpec
class SparkPodSpec(BaseModel):
    cores: int
    core_limit: Optional[str] = None
    memory: str
    configMaps: Optional[List[NamePath]] = None
    labels: Optional[Dict[str, str]] = None
    annotations: Optional[Dict[str, str]] = None
    env: Optional[List[k8smodels.V1EnvVar]] = None
    volumeMounts: Optional[List[k8smodels.V1VolumeMount]] = None
    sidecars: Optional[List[k8smodels.V1Container]] = None
    initContainers: Optional[List[k8smodels.V1Container]] = None
    tolerations: Optional[List[k8smodels.V1Toleration]] = None
    nodeSelector: Optional[Dict[str, str]] = None
    securityContext: Optional[k8smodels.V1SecurityContext] = None

    model_config = {"arbitrary_types_allowed": True}


class Driver(SparkPodSpec):
    serviceAccount: Optional[str] = None
    serviceLabels: Optional[Dict[str, str]] = None
    kubernetesMaster: Optional[str] = None
    javaOptions: Optional[str] = None


class Executor(SparkPodSpec):
    instances: Optional[int] = None
    deleteOnTermination: Optional[bool] = None
    javaOptions: Optional[str] = None


class DeployMode(StrEnum):
    cluster = "cluster"
    client = "client"


class DynamicAllocationProfile(BaseModel):
    enabled: Optional[bool] = None
    minExecutors: Optional[int] = None
    maxExecutors: Optional[int] = None


class MonitoringSpec(BaseModel):
    exposeDriverMetrics: Optional[bool] = None
    exposeExecutorMetrics: Optional[bool] = None


class SparkApplicationSpec(BaseModel):
    type: str
    name: str
    sparkVersion: str
    mode: DeployMode
    image: str
    mainApplicationFile: str
    arguments: Optional[List[str]] = None
    sparkConf: Optional[Dict[str, str]] = None
    volumes: Optional[List[k8smodels.V1Volume]] = None
    driver: Driver
    executor: Executor
    dynamicAllocation: DynamicAllocationProfile
    monitoring: MonitoringSpec

    model_config = {"arbitrary_types_allowed": True}


class SparkErrorCode(StrEnum):
    """Error codes for Spark job failures."""

    JOB_FAILED = "SPARK_JOB_FAILED"
    SUBMISSION_FAILED = "SPARK_SUBMISSION_FAILED"

    @staticmethod
    def from_state(state: "ApplicationStateEnum") -> "SparkErrorCode":
        """Get the error code for a given application state."""
        error_code_map = {
            "FAILED": SparkErrorCode.JOB_FAILED,
            "SUBMISSION_FAILED": SparkErrorCode.SUBMISSION_FAILED,
        }
        return error_code_map.get(state.value, SparkErrorCode.JOB_FAILED)


class ApplicationStateEnum(StrEnum):
    New = ""
    Submitted = "SUBMITTED"
    Running = "RUNNING"
    Completed = "COMPLETED"
    Failed = "FAILED"
    SubmissionFailed = "SUBMISSION_FAILED"
    PendingRerun = "PENDING_RERUN"
    Invalidating = "INVALIDATING"
    Succeeding = "SUCCEEDING"
    Failing = "FAILING"
    Suspending = "SUSPENDING"
    Suspended = "SUSPENDED"
    Resuming = "RESUMING"
    Unknown = "UNKNOWN"


class SparkApplicationState(BaseModel):
    state: ApplicationStateEnum
    errorMessage: Optional[str] = None


class SparkDriverInfo(BaseModel):
    podName: str
    webUIAddress: Optional[str] = None
    webUIPort: Optional[int] = None
    webUIServiceName: Optional[str] = None


class SparkApplicationStatus(BaseModel):
    applicationState: SparkApplicationState
    driverInfo: SparkDriverInfo
    executionAttempts: Optional[int] = None
    executorState: Optional[dict[str, str]] = None
    submissionAttempts: Optional[int] = None
    lastSubmissionAttemptTime: Optional[datetime.datetime] = None
    terminationTime: Optional[datetime.datetime] = None


class SparkApplication(BaseModel):
    metadata: k8smodels.V1ObjectMeta
    status: Optional[SparkApplicationStatus] = None

    model_config = {"arbitrary_types_allowed": True}
