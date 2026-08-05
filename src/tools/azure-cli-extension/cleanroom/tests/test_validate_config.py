#!/usr/bin/env python3
"""
Test script for validate_config() in the Azure Cleanroom CLI extension.

Tests the CleanRoomSpecification validator for correct detection of:
- Empty applications
- Duplicate datasource/datasink/application/identity names
- Duplicate ports across applications
- Missing datasource/datasink references
- Unused datasource/datasink warnings
- Network inbound/outbound warnings
- Multiple applications warning
"""

import logging
import sys
from pathlib import Path

# Add the extension to Python path.
current_dir = Path(__file__).parent
cleanroom_dir = current_dir.parent
sys.path.insert(0, str(cleanroom_dir))

from cleanroom_common.azure_cleanroom_core.exceptions.exception import (
    ErrorCode,
)
from cleanroom_common.azure_cleanroom_core.models.cleanroom import (
    AccessPoint,
    AccessPointType,
    Application,
    ApplicationResource,
    ApplicationStartType,
    AttestationBasedTokenIssuer,
    CleanRoomSpecification,
    Document,
    HttpSettings,
    Identity,
    Image,
    Inbound,
    NetworkSettings,
    Outbound,
    Policy,
    PrivacyProxySettings,
    ProtocolType,
    ProxyMode,
    ProxyType,
    Requests,
    Resource,
    ResourceType,
    RuntimeSettings,
    ServiceEndpoint,
)
from cleanroom_common.azure_cleanroom_core.utilities.helpers import (
    validate_config,
)

logger = logging.getLogger("test_validate_config")


# ---- Helper factories ----


def _endpoint():
    return ServiceEndpoint(
        protocol=ProtocolType.AzureContainerRegistry,
        url="https://test.azurecr.io",
    )


def _resource(name="res"):
    return Resource(
        name=name,
        type=ResourceType.AzureContainerRegistry,
        id="id",
        provider=_endpoint(),
    )


def _identity(name="id1"):
    return Identity(
        name=name,
        clientId="cid",
        tenantId="tid",
        tokenIssuer=AttestationBasedTokenIssuer(
            issuer=ServiceEndpoint(
                protocol=ProtocolType.Attested_OIDC,
                url="https://issuer",
            ),
            issuerType="AttestationBasedTokenIssuer",
        ),
    )


def _protection():
    return PrivacyProxySettings(proxyType=ProxyType.API, proxyMode=ProxyMode.Open)


def _image():
    return Image(
        executable=Document(
            documentType="oci",
            authenticityReceipt="r",
            backingResource=_resource(),
        ),
        enforcementPolicy=Policy(),
    )


def _app(name="app1", ports=None, datasources=None, datasinks=None):
    return Application(
        name=name,
        image=_image(),
        startType=ApplicationStartType.Auto,
        command=["cmd"],
        environmentVariables={},
        datasources=datasources,
        datasinks=datasinks,
        runtimeSettings=RuntimeSettings(
            ports=ports if ports is not None else [8080],
            resource=ApplicationResource(requests=Requests(cpu=1.0, memoryInGB=2.0)),
        ),
    )


def _access_point(name="ds1"):
    return AccessPoint(
        name=name,
        type=AccessPointType.Volume_ReadOnly,
        path="/mnt",
        store=_resource(),
        protection=_protection(),
    )


def _inbound_network(with_policy=False):
    privacy = Policy() if with_policy else None
    return NetworkSettings(
        http=HttpSettings(
            inbound=Inbound(
                enabled=True,
                policy=PrivacyProxySettings(
                    proxyType=ProxyType.API,
                    proxyMode=ProxyMode.Open,
                    privacyPolicy=privacy,
                ),
            )
        )
    )


# ---- Tests ----


def test_empty_applications():
    spec = CleanRoomSpecification(
        identities=[], datasources=[], datasinks=[], applications=[]
    )
    issues, _ = validate_config(spec, logger)
    assert len(issues) == 1
    assert issues[0].code == ErrorCode.NoApplicationsDefined
    print("✓ Empty applications detected")


def test_duplicate_datasource_names():
    spec = CleanRoomSpecification(
        identities=[_identity()],
        datasources=[_access_point("ds"), _access_point("ds")],
        datasinks=[],
        applications=[_app()],
    )
    issues, _ = validate_config(spec, logger)
    matches = [
        i
        for i in issues
        if i.code == ErrorCode.DuplicateName and "datasource" in i.message
    ]
    assert len(matches) == 1
    print("✓ Duplicate datasource names detected")


def test_duplicate_datasink_names():
    spec = CleanRoomSpecification(
        identities=[_identity()],
        datasources=[],
        datasinks=[_access_point("sk"), _access_point("sk")],
        applications=[_app()],
    )
    issues, _ = validate_config(spec, logger)
    matches = [
        i
        for i in issues
        if i.code == ErrorCode.DuplicateName and "datasink" in i.message
    ]
    assert len(matches) == 1
    print("✓ Duplicate datasink names detected")


def test_duplicate_application_names():
    spec = CleanRoomSpecification(
        identities=[_identity()],
        datasources=[],
        datasinks=[],
        applications=[_app("a", ports=[80]), _app("a", ports=[81])],
    )
    issues, _ = validate_config(spec, logger)
    matches = [
        i
        for i in issues
        if i.code == ErrorCode.DuplicateName and "application" in i.message
    ]
    assert len(matches) == 1
    print("✓ Duplicate application names detected")


def test_duplicate_identity_names():
    spec = CleanRoomSpecification(
        identities=[_identity("x"), _identity("x")],
        datasources=[],
        datasinks=[],
        applications=[_app()],
    )
    issues, _ = validate_config(spec, logger)
    matches = [
        i
        for i in issues
        if i.code == ErrorCode.DuplicateName and "identity" in i.message
    ]
    assert len(matches) == 1
    print("✓ Duplicate identity names detected")


def test_duplicate_ports_across_apps():
    spec = CleanRoomSpecification(
        identities=[_identity()],
        datasources=[],
        datasinks=[],
        applications=[_app("a1", ports=[80]), _app("a2", ports=[80])],
    )
    issues, _ = validate_config(spec, logger)
    matches = [i for i in issues if i.code == ErrorCode.DuplicatePort]
    assert len(matches) == 1
    print("✓ Duplicate ports across apps detected")


def test_missing_datasource_reference():
    spec = CleanRoomSpecification(
        identities=[_identity()],
        datasources=[],
        datasinks=[],
        applications=[_app(datasources={"missing": "/mnt"})],
    )
    issues, _ = validate_config(spec, logger)
    matches = [i for i in issues if i.code == ErrorCode.DataStoreNotFound]
    assert len(matches) == 1
    print("✓ Missing datasource reference detected")


def test_missing_datasink_reference():
    spec = CleanRoomSpecification(
        identities=[_identity()],
        datasources=[],
        datasinks=[],
        applications=[_app(datasinks={"missing": "/mnt"})],
    )
    issues, _ = validate_config(spec, logger)
    matches = [i for i in issues if i.code == ErrorCode.DatasinkNotFound]
    assert len(matches) == 1
    print("✓ Missing datasink reference detected")


def test_unused_datasource_warning():
    spec = CleanRoomSpecification(
        identities=[_identity()],
        datasources=[_access_point("unused")],
        datasinks=[],
        applications=[_app()],
    )
    _, warnings = validate_config(spec, logger)
    matches = [w for w in warnings if w["code"] == "UnusedDatasource"]
    assert len(matches) == 1
    print("✓ Unused datasource warning")


def test_unused_datasink_warning():
    spec = CleanRoomSpecification(
        identities=[_identity()],
        datasources=[],
        datasinks=[_access_point("unused")],
        applications=[_app()],
    )
    _, warnings = validate_config(spec, logger)
    matches = [w for w in warnings if w["code"] == "UnusedDatasink"]
    assert len(matches) == 1
    print("✓ Unused datasink warning")


def test_ports_without_inbound_warns():
    spec = CleanRoomSpecification(
        identities=[_identity()],
        datasources=[],
        datasinks=[],
        applications=[_app(ports=[8080])],
    )
    _, warnings = validate_config(spec, logger)
    matches = [w for w in warnings if w["code"] == "InboundTrafficNotAllowed"]
    assert len(matches) == 1
    print("✓ Ports without inbound network warns")


def test_inbound_allow_all_warning():
    spec = CleanRoomSpecification(
        identities=[_identity()],
        datasources=[],
        datasinks=[],
        applications=[_app(ports=[8080])],
        network=_inbound_network(with_policy=False),
    )
    _, warnings = validate_config(spec, logger)
    matches = [w for w in warnings if w["code"] == "InboundAllowAll"]
    assert len(matches) == 1
    print("✓ Inbound allow-all warning")


def test_outbound_allow_all_warning():
    spec = CleanRoomSpecification(
        identities=[_identity()],
        datasources=[],
        datasinks=[],
        applications=[_app(ports=[])],
        network=NetworkSettings(
            http=HttpSettings(
                outbound=Outbound(
                    enabled=True,
                    policy=PrivacyProxySettings(
                        proxyType=ProxyType.API,
                        proxyMode=ProxyMode.Open,
                    ),
                )
            )
        ),
    )
    _, warnings = validate_config(spec, logger)
    matches = [w for w in warnings if w["code"] == "OutboundAllowAll"]
    assert len(matches) == 1
    print("✓ Outbound allow-all warning")


def test_multiple_applications_warning():
    spec = CleanRoomSpecification(
        identities=[_identity()],
        datasources=[],
        datasinks=[],
        applications=[_app("a1", ports=[80]), _app("a2", ports=[81])],
    )
    _, warnings = validate_config(spec, logger)
    matches = [w for w in warnings if w["code"] == "MultipleApplications"]
    assert len(matches) == 1
    print("✓ Multiple applications warning")


def test_clean_spec_no_issues():
    spec = CleanRoomSpecification(
        identities=[_identity()],
        datasources=[_access_point("ds1")],
        datasinks=[],
        applications=[_app(ports=[], datasources={"ds1": "/mnt"})],
    )
    issues, warnings = validate_config(spec, logger)
    assert len(issues) == 0
    print("✓ Clean spec passes with no issues")


def main():
    print("Running validate_config() Tests")
    print("=" * 40)

    try:
        test_empty_applications()
        test_duplicate_datasource_names()
        test_duplicate_datasink_names()
        test_duplicate_application_names()
        test_duplicate_identity_names()
        test_duplicate_ports_across_apps()
        test_missing_datasource_reference()
        test_missing_datasink_reference()
        test_unused_datasource_warning()
        test_unused_datasink_warning()
        test_ports_without_inbound_warns()
        test_inbound_allow_all_warning()
        test_outbound_allow_all_warning()
        test_multiple_applications_warning()
        test_clean_spec_no_issues()

        print("\n" + "=" * 40)
        print("✅ ALL VALIDATE_CONFIG TESTS PASSED!")

    except Exception as e:
        print(f"\n❌ TEST FAILED: {e}")
        import traceback

        traceback.print_exc()
        sys.exit(1)


if __name__ == "__main__":
    main()
