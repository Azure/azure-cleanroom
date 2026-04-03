# Hand-rolled Pydantic models mirroring the KServe InferenceService CRD
# (serving.kserve.io/v1beta1). Must stay in sync with the KServe Helm chart
# version pinned in src/cleanroom-cluster/cleanroom-cluster-provider-common/Helm.cs.
from datetime import datetime
from typing import Any, Dict, List, Optional

from kubernetes.client import models as k8smodels
from pydantic import BaseModel


# ---------------------------
# Kubernetes metadata (minimal)
# ---------------------------
class ObjectMeta(BaseModel):
    name: Optional[str] = None
    namespace: Optional[str] = None
    labels: Optional[Dict[str, str]] = None
    annotations: Optional[Dict[str, str]] = None
    additionalProperties: Optional[Dict[str, Any]] = None

    class Config:
        extra = "forbid"


class PodSpec(BaseModel):
    initContainers: Optional[List[k8smodels.V1Container]] = None
    containers: Optional[List[k8smodels.V1Container]] = None
    volumes: Optional[List[k8smodels.V1Volume]] = None
    hostNetwork: Optional[bool] = None
    nodeSelector: Optional[Dict[str, str]] = None
    tolerations: Optional[List[Dict[str, Any]]] = None
    affinity: Optional[Dict[str, Any]] = None
    serviceAccountName: Optional[str] = None

    class Config:
        arbitrary_types_allowed = True
        extra = "forbid"


# ---------------------------
# Component extension (deployment hints)
# ---------------------------
class LoggerSpec(BaseModel):
    url: Optional[str] = None
    mode: Optional[str] = None
    metadataHeaders: Optional[List[str]] = None

    class Config:
        extra = "forbid"


class Batcher(BaseModel):
    maxBatchSize: Optional[int] = None
    maxLatency: Optional[int] = None
    timeout: Optional[int] = None

    class Config:
        extra = "forbid"


class AutoScalingMetricSpec(BaseModel):
    type: str
    resource: Optional[Dict[str, Any]] = None
    external: Optional[Dict[str, Any]] = None
    podmetric: Optional[Dict[str, Any]] = None

    class Config:
        extra = "forbid"


class AutoScalingSpec(BaseModel):
    metrics: Optional[List[AutoScalingMetricSpec]] = None
    behavior: Optional[Dict[str, Any]] = None

    class Config:
        extra = "forbid"


class ComponentExtensionSpec(BaseModel):
    minReplicas: Optional[int] = None
    maxReplicas: Optional[int] = None
    canaryTrafficPercent: Optional[int] = None
    containerConcurrency: Optional[int] = None
    timeout: Optional[int] = None
    scaleTarget: Optional[int] = None
    scaleMetric: Optional[str] = None
    scaleMetricType: Optional[str] = None
    autoScaling: Optional[AutoScalingSpec] = None
    logger: Optional[LoggerSpec] = None
    batcher: Optional[Batcher] = None
    deploymentStrategy: Optional[Dict[str, Any]] = None
    annotations: Optional[Dict[str, str]] = None
    labels: Optional[Dict[str, str]] = None

    class Config:
        extra = "forbid"


# ---------------------------
# Predictor runtime / model specs
# ---------------------------
class ModelFormat(BaseModel):
    name: Optional[str] = None
    version: Optional[str] = None
    autoSelect: Optional[bool] = None

    class Config:
        extra = "forbid"


class GenericModelSpec(BaseModel):
    modelFormat: Optional[ModelFormat] = None
    storageUri: Optional[str] = None
    runtime: Optional[str] = None
    runtimeVersion: Optional[str] = None
    protocolVersion: Optional[str] = None
    storage: Optional[Dict[str, Any]] = None
    env: Optional[List[Dict[str, Any]]] = None
    volumeMounts: Optional[List[k8smodels.V1VolumeMount]] = None
    resources: Optional[Dict[str, Any]] = None
    securityContext: Optional[k8smodels.V1SecurityContext] = None
    args: Optional[List[str]] = None

    class Config:
        extra = "forbid"
        arbitrary_types_allowed = True


# PredictorSpec: uses containers or model spec (all optional).
# Inherits from PodSpec and ComponentExtensionSpec to embed their fields inline.
class PredictorSpec(PodSpec, ComponentExtensionSpec):
    model: Optional[GenericModelSpec] = None

    class Config:
        extra = "forbid"


# ---------------------------
# Explainer spec
# ---------------------------
class ARTExplainerSpec(BaseModel):
    type: Optional[str] = None
    runtimeVersion: Optional[str] = None
    storageUri: Optional[str] = None
    config: Optional[Dict[str, str]] = None

    class Config:
        extra = "forbid"


class ExplainerSpec(PodSpec, ComponentExtensionSpec):
    art: Optional[ARTExplainerSpec] = None

    class Config:
        extra = "forbid"


# ---------------------------
# Transformer spec
# ---------------------------
class TransformerSpec(PodSpec, ComponentExtensionSpec):
    transformerImpl: Optional[Dict[str, Any]] = None

    class Config:
        extra = "forbid"


# ---------------------------
# InferenceServiceSpec
# ---------------------------
class InferenceServiceSpec(BaseModel):
    predictor: PredictorSpec
    explainer: Optional[ExplainerSpec] = None
    transformer: Optional[TransformerSpec] = None

    class Config:
        extra = "forbid"


# ---------------------------
# Status models
# ---------------------------
class Condition(BaseModel):
    type: Optional[str] = None
    status: Optional[str] = None
    lastTransitionTime: Optional[datetime] = None
    reason: Optional[str] = None
    message: Optional[str] = None

    class Config:
        extra = "allow"


class Addressable(BaseModel):
    url: Optional[str] = None

    class Config:
        extra = "allow"


class TrafficTarget(BaseModel):
    tag: Optional[str] = None
    percent: Optional[int] = None
    revisionName: Optional[str] = None
    latestRevision: Optional[bool] = None

    class Config:
        extra = "allow"


class ComponentStatus(BaseModel):
    latestReadyRevision: Optional[str] = None
    latestCreatedRevision: Optional[str] = None
    previousRolledoutRevision: Optional[str] = None
    latestRolledoutRevision: Optional[str] = None
    traffic: Optional[List[TrafficTarget]] = None
    url: Optional[str] = None
    restUrl: Optional[str] = None
    grpcUrl: Optional[str] = None
    address: Optional[Addressable] = None

    class Config:
        extra = "allow"


class ComponentsSpec(BaseModel):
    predictor: Optional[ComponentStatus] = None
    transformer: Optional[ComponentStatus] = None
    explainer: Optional[ComponentStatus] = None

    class Config:
        extra = "allow"


class ModelCopies(BaseModel):
    failedCopies: Optional[int] = None
    totalCopies: Optional[int] = None

    class Config:
        extra = "allow"


class ModelRevisionStates(BaseModel):
    activeModelState: Optional[str] = None
    targetModelState: Optional[str] = None

    class Config:
        extra = "allow"


class FailureInfo(BaseModel):
    location: Optional[str] = None
    reason: Optional[str] = None
    message: Optional[str] = None
    modelRevisionName: Optional[str] = None
    time: Optional[datetime] = None
    exitCode: Optional[int] = None

    class Config:
        extra = "allow"


class ModelStatus(BaseModel):
    transitionStatus: Optional[str] = None
    modelRevisionStates: Optional[ModelRevisionStates] = None
    lastFailureInfo: Optional[FailureInfo] = None
    copies: Optional[ModelCopies] = None

    class Config:
        extra = "allow"


class InferenceServiceStatus(BaseModel):
    observedGeneration: Optional[int] = None
    conditions: Optional[List[Condition]] = None
    url: Optional[str] = None
    address: Optional[Addressable] = None
    annotations: Optional[Dict[str, str]] = None
    components: Optional[ComponentsSpec] = None
    modelStatus: Optional[ModelStatus] = None
    deploymentMode: Optional[str] = None
    servingRuntimeName: Optional[str] = None

    class Config:
        extra = "allow"


# ---------------------------
# Full InferenceService CR
# ---------------------------
class InferenceService(BaseModel):
    apiVersion: Optional[str] = None
    kind: Optional[str] = None
    metadata: Optional[k8smodels.V1ObjectMeta] = None
    spec: Optional[InferenceServiceSpec] = None
    status: Optional[InferenceServiceStatus] = None

    class Config:
        arbitrary_types_allowed = True
        extra = "allow"
