import datetime
from enum import StrEnum
from typing import List, Optional

from cleanroom_sdk.models.cleanroom import DatasetInfo
from fastapi.params import Header
from frontend_internal.models.input_models import (
    CcfServiceCertDiscoveryModel,
    GovernanceSettings,
)
from pydantic import BaseModel, ConfigDict, Field


class Format(StrEnum):
    CSV = "csv"
    JSON = "json"
    PARQUET = "parquet"


class JobInput(BaseModel):
    contract_id: str = Field(alias="contractId")
    datasets: List[DatasetInfo]
    datasink: Optional[DatasetInfo] = None
    governance: Optional[GovernanceSettings] = None


class SQLJobInput(JobInput):
    query: str
    start_date: Optional[datetime.datetime] = Field(default=None, alias="startDate")
    end_date: Optional[datetime.datetime] = Field(default=None, alias="endDate")
    use_optimizer: bool = Field(default=False, alias="useOptimizer")


class EnvData(BaseModel):
    key: str
    value: str
    isMeasured: bool
