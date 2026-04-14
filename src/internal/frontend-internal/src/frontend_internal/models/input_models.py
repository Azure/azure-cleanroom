import os
from enum import StrEnum
from typing import List, Optional

from pydantic import BaseModel, Field

DEFAULT_MCR_URL = "mcr.microsoft.com/azurecleanroom"
DEFAULT_MCR_VERSION = "7.0.0"


class AttestationType(StrEnum):
    """The type of attestation sidecar to use for governance."""

    SKR = "skr"
    CVM = "cvm"


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


class TelemetrySettings(BaseModel):
    telemetry_collection_enabled: bool = Field(
        alias="telemetryCollectionEnabled", default=False
    )
    prometheus_endpoint: Optional[str] = Field(alias="prometheusEndpoint", default="")
    loki_endpoint: Optional[str] = Field(alias="lokiEndpoint", default="")
    tempo_endpoint: Optional[str] = Field(alias="tempoEndpoint", default="")
