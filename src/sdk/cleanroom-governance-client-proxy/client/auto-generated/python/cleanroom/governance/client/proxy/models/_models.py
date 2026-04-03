# coding=utf-8
# pylint: disable=useless-super-delegation

from typing import TYPE_CHECKING, Any, Mapping, Optional, overload

from .._utils.model_base import Model as _Model
from .._utils.model_base import rest_field

if TYPE_CHECKING:
    from .. import models as _models


class CcfServiceCertDiscoveryModel(_Model):
    """CCF service certificate discovery configuration.

    :ivar certificate_discovery_endpoint: Certificate discovery endpoint URL. Required.
    :vartype certificate_discovery_endpoint: str
    :ivar host_data: Expected host data values for attestation. Required.
    :vartype host_data: list[str]
    :ivar constitution_digest: Expected constitution digest.
    :vartype constitution_digest: str
    :ivar js_app_bundle_digest: Expected JavaScript app bundle digest.
    :vartype js_app_bundle_digest: str
    :ivar skip_digest_check: Skip digest verification (for testing only).
    :vartype skip_digest_check: bool
    """

    certificate_discovery_endpoint: str = rest_field(
        name="certificateDiscoveryEndpoint",
        visibility=["read", "create", "update", "delete", "query"],
    )
    """Certificate discovery endpoint URL. Required."""
    host_data: list[str] = rest_field(
        name="hostData", visibility=["read", "create", "update", "delete", "query"]
    )
    """Expected host data values for attestation. Required."""
    constitution_digest: Optional[str] = rest_field(
        name="constitutionDigest",
        visibility=["read", "create", "update", "delete", "query"],
    )
    """Expected constitution digest."""
    js_app_bundle_digest: Optional[str] = rest_field(
        name="jsAppBundleDigest",
        visibility=["read", "create", "update", "delete", "query"],
    )
    """Expected JavaScript app bundle digest."""
    skip_digest_check: Optional[bool] = rest_field(
        name="skipDigestCheck",
        visibility=["read", "create", "update", "delete", "query"],
    )
    """Skip digest verification (for testing only)."""

    @overload
    def __init__(
        self,
        *,
        certificate_discovery_endpoint: str,
        host_data: list[str],
        constitution_digest: Optional[str] = None,
        js_app_bundle_digest: Optional[str] = None,
        skip_digest_check: Optional[bool] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class SidecarWorkspaceConfigurationModel(_Model):
    """Sidecar workspace configuration.

    :ivar ccrgov_endpoint: CCR Governance endpoint URL. Required.
    :vartype ccrgov_endpoint: str
    :ivar service_cert: PEM encoded service certificate.
    :vartype service_cert: str
    :ivar service_cert_discovery: Service certificate discovery configuration.
    :vartype service_cert_discovery:
     ~cleanroom.governance.client.proxy.models.CcfServiceCertDiscoveryModel
    """

    ccrgov_endpoint: str = rest_field(
        name="ccrgovEndpoint",
        visibility=["read", "create", "update", "delete", "query"],
    )
    """CCR Governance endpoint URL. Required."""
    service_cert: Optional[str] = rest_field(
        name="serviceCert", visibility=["read", "create", "update", "delete", "query"]
    )
    """PEM encoded service certificate."""
    service_cert_discovery: Optional["_models.CcfServiceCertDiscoveryModel"] = (
        rest_field(
            name="serviceCertDiscovery",
            visibility=["read", "create", "update", "delete", "query"],
        )
    )
    """Service certificate discovery configuration."""

    @overload
    def __init__(
        self,
        *,
        ccrgov_endpoint: str,
        service_cert: Optional[str] = None,
        service_cert_discovery: Optional["_models.CcfServiceCertDiscoveryModel"] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)
