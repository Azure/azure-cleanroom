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


class Executor(SparkPodSpec):
    instances: Optional[int] = None
    deleteOnTermination: Optional[bool] = None


class DeployMode(StrEnum):
    cluster = "cluster"
    client = "client"


class SparkApplicationSpec(BaseModel):
    type: str
    name: str
    sparkVersion: str
    mode: DeployMode
    image: str
    mainApplicationFile: str
    arguments: Optional[List[str]] = None
    volumes: Optional[List[k8smodels.V1Volume]] = None
    driver: Driver
    executor: Executor

    model_config = {"arbitrary_types_allowed": True}


class ApplicationStateEnum(StrEnum):
    Running = "RUNNING"
    Completed = "COMPLETED"
    Failed = "FAILED"
    Submitted = "SUBMITTED"


class SparkApplicationState(BaseModel):
    state: ApplicationStateEnum


class SparkDriverInfo(BaseModel):
    podName: str
    webUIAddress: str
    webUIPort: int
    webUIServiceName: str


class SparkApplicationStatus(BaseModel):
    applicationState: SparkApplicationState
    driverInfo: SparkDriverInfo
    executionAttempts: int
    executorState: Optional[dict[str, str]] = None
    submissionAttempts: int
    lastSubmissionAttemptTime: Optional[datetime.datetime] = None
    terminationTime: Optional[datetime.datetime] = None


class SparkApplication(BaseModel):
    metadata: k8smodels.V1ObjectMeta
    status: Optional[SparkApplicationStatus] = None

    model_config = {"arbitrary_types_allowed": True}
