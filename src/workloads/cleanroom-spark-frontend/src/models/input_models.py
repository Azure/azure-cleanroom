import datetime
from enum import StrEnum
from typing import List, Optional

from fastapi.params import Header
from pydantic import BaseModel, ConfigDict, Field

from .model import AccessPoint, DatasetInfo


class Format(StrEnum):
    CSV = "csv"
    JSON = "json"
    PARQUET = "parquet"


class CcfServiceCertDiscoveryModel(BaseModel):
    certificate_discovery_endpoint: str = Field(
        ..., alias="certificateDiscoveryEndpoint"
    )
    host_data: List[str] = Field(..., alias="hostData")
    skip_digest_check: bool = Field(..., alias="skipDigestCheck")
    constitution_digest: Optional[str] = Field(None, alias="constitutionDigest")
    js_app_bundle_digest: Optional[str] = Field(None, alias="jsAppBundleDigest")


class GovernanceSettings(BaseModel):
    service_url: str = Field(alias="serviceUrl")
    cert_base64: Optional[str] = Field(alias="certBase64")
    service_cert_discovery: Optional[CcfServiceCertDiscoveryModel] = Field(
        None, alias="serviceCertDiscovery"
    )


class JobInput(BaseModel):
    contract_id: str = Field(alias="contractId")
    datasets: List[DatasetInfo]
    datasink: Optional[DatasetInfo] = None
    governance: Optional[GovernanceSettings] = None


class SQLJobInput(JobInput):
    query: str
    start_date: Optional[datetime.datetime] = Field(default=None, alias="startDate")
    end_date: Optional[datetime.datetime] = Field(default=None, alias="endDate")


class EnvData(BaseModel):
    key: str
    value: str
    isMeasured: bool
