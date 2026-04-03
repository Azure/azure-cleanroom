from enum import Enum
from typing import Dict, List, Optional

from cleanroom_sdk.models.cleanroom import DatasetFormat
from pydantic import BaseModel, Field
from pydantic_settings import BaseSettings


class DatasetInfo(BaseModel):
    name: str
    view_name: str = Field(alias="viewName")
    path: str
    format: DatasetFormat
    schema_: Optional[Dict[str, Dict[str, str]]] = Field(None, alias="schema")
    allowedFields: List[str] = Field(alias="allowedFields")


class QueryConfiguration(BaseModel):
    query: str = Field(alias="query")
    datasets: List[DatasetInfo] = Field(alias="datasets")
    datasink: DatasetInfo = Field(alias="datasink")


class ProviderType(Enum):
    Http = "http"
    Mock = "mock"


class EventRecorderConfiguration(BaseSettings):
    provider_type: ProviderType = Field(alias="providerType", default=ProviderType.Mock)
    statistics_recorder_endpoint: Optional[str] = Field(
        alias="statisticsRecorderEndpoint", default=None
    )
    event_emitter_endpoint: Optional[str] = Field(
        alias="eventEmitterEndpoint", default=None
    )
    audit_record_logger_endpoint: Optional[str] = Field(
        alias="auditRecordLoggerEndpoint", default=None
    )


class ApplicationConfiguration(BaseSettings):
    event_recorder_configuration: EventRecorderConfiguration = Field(
        alias="eventRecorderConfiguration",
        default=EventRecorderConfiguration(),
    )
