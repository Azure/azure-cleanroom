import datetime
from enum import StrEnum
from typing import Any, Dict, List, Optional

from cleanroom_sdk.models.cleanroom import DatasetInfo
from frontend_internal.models.input_models import (
    GovernanceSettings,
)
from pydantic import BaseModel, ConfigDict, Field


class ModelFormatInput(BaseModel):
    name: str = Field(..., alias="name")
    version: Optional[str] = Field(None, alias="version")


class ResourceRequirementsInput(BaseModel):
    requests: Optional[Dict[str, str]] = Field(None, alias="requests")
    limits: Optional[Dict[str, str]] = Field(None, alias="limits")


class EnvVarInput(BaseModel):
    name: str = Field(..., alias="name")
    value: Optional[str] = Field(None, alias="value")


class ModelInput(BaseModel):
    model_format: ModelFormatInput = Field(..., alias="modelFormat")
    protocol_version: Optional[str] = Field(None, alias="protocolVersion")
    runtime: Optional[str] = Field(None, alias="runtime")
    args: Optional[List[str]] = Field(None, alias="args")
    resources: Optional[ResourceRequirementsInput] = Field(None, alias="resources")
    env: Optional[List[EnvVarInput]] = Field(None, alias="env")


class NodeType(StrEnum):
    flexnode = "flexnode"


class PlacementInput(BaseModel):
    host_network: Optional[bool] = Field(None, alias="hostNetwork")


class BatcherInput(BaseModel):
    max_batch_size: Optional[int] = Field(None, alias="maxBatchSize")
    max_latency: Optional[int] = Field(None, alias="maxLatency")
    timeout: Optional[int] = Field(None, alias="timeout")


class AutoScalingMetricInput(BaseModel):
    type: str = Field(..., alias="type")
    resource: Optional[Dict[str, Any]] = Field(None, alias="resource")
    external: Optional[Dict[str, Any]] = Field(None, alias="external")
    podmetric: Optional[Dict[str, Any]] = Field(None, alias="podmetric")


class AutoScalingInput(BaseModel):
    metrics: Optional[List[AutoScalingMetricInput]] = Field(None, alias="metrics")
    behavior: Optional[Dict[str, Any]] = Field(None, alias="behavior")


class PredictorInput(BaseModel):
    model: ModelInput = Field(..., alias="model")
    min_replicas: Optional[int] = Field(None, alias="minReplicas")
    max_replicas: Optional[int] = Field(None, alias="maxReplicas")
    timeout: Optional[int] = Field(None, alias="timeout")
    batcher: Optional[BatcherInput] = Field(None, alias="batcher")
    deployment_strategy: Optional[Dict[str, Any]] = Field(
        None, alias="deploymentStrategy"
    )
    scale_metric_type: Optional[str] = Field(None, alias="scaleMetricType")
    auto_scaling: Optional[AutoScalingInput] = Field(None, alias="autoScaling")


class JobInput(BaseModel):
    contract_id: str = Field(alias="contractId")
    model_name: str = Field(..., alias="modelName")
    predictor: PredictorInput = Field(..., alias="predictor")
    model_dir: Optional[str] = Field(None, alias="modelDir")
    datasets: List[DatasetInfo]
    governance: Optional[GovernanceSettings] = Field(None, alias="governance")
    placement: Optional[PlacementInput] = Field(None, alias="placement")


class EnvData(BaseModel):
    key: str
    value: str
    isMeasured: bool
