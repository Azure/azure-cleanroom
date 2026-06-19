# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Unit tests for blobfuse subdirectory propagation in the cleanroom
application builder.  Validates that:

  1. An explicit subdirectory value is passed through to the sidecar
     replacement vars for Azure Blob Storage.
  2. An empty / omitted subdirectory leaves the replacement var empty.
  3. An explicit subdirectory overrides the OneLake URL-derived path.
"""

import base64
import json
import unittest
from unittest.mock import MagicMock, patch

from cleanroom_sdk.models.cleanroom import (
    AccessPoint,
    AccessPointType,
    CleanroomSecret,
    EncryptionSecret,
    EncryptionSecrets,
    PrivacyProxySettings,
    ProtocolType,
    ProxyMode,
    ProxyType,
    Resource,
    ResourceType,
    SecretType,
    ServiceEndpoint,
)
from frontend_internal.cleanroom_application_builder import (
    CleanroomApplicationBuilder,
)
from frontend_internal.models.cleanroom_application import Sidecar
from frontend_internal.models.input_models import CleanroomSettings
from kubernetes.client import models as k8smodels


def _make_protection(encryption_mode="SSE"):
    """Build a minimal PrivacyProxySettings with the given encryption mode."""
    config = json.dumps({"EncryptionMode": encryption_mode})
    protection = PrivacyProxySettings(
        proxyType=ProxyType.SecureVolume__ReadOnly__Azure__BlobStorage,
        proxyMode=ProxyMode.Secure,
        configuration=base64.b64encode(config.encode()).decode(),
    )
    if encryption_mode in ("CPK", "CSE"):
        dek_backing_resource = Resource(
            name="test-dek-secret",
            type=ResourceType.Cgs,
            id="test-dek-secret",
            provider=ServiceEndpoint(
                protocol=ProtocolType.Cgs_Secret,
                url="https://cgs.example.com",
            ),
        )
        dek = EncryptionSecret(
            name="dek",
            secret=CleanroomSecret(
                secretType=SecretType.Secret,
                backingResource=dek_backing_resource,
            ),
        )
        protection.encryptionSecrets = EncryptionSecrets(dek=dek)
    return protection


def _make_identity():
    mock_id = MagicMock()
    mock_id.clientId = "test-client-id"
    mock_id.tenantId = "test-tenant-id"
    return mock_id


def _make_access_point(
    *,
    protocol=ProtocolType.Azure_BlobStorage,
    storage_url="https://teststorage.blob.core.windows.net",
    container_name="testcontainer",
    subdirectory="",
    resource_type=ResourceType.Azure_BlobStorage,
    encryption_mode="SSE",
):
    """Build an AccessPoint with the given store settings.

    Uses model_construct to bypass pydantic validation for the identity
    field (which requires a complex discriminated union).
    """
    store = Resource(
        name=container_name,
        type=resource_type,
        id=container_name,
        provider=ServiceEndpoint(
            protocol=protocol,
            url=storage_url,
        ),
    )
    return AccessPoint.model_construct(
        name="test-dataset",
        type=AccessPointType.Volume_ReadOnly,
        path="",
        store=store,
        identity=_make_identity(),
        protection=_make_protection(encryption_mode),
        subdirectory=subdirectory,
    )


def _stub_sidecar(*args, **kwargs):
    """Return a minimal Sidecar stub from _get_sidecar."""
    container = k8smodels.V1Container(
        name="stub",
        image="stub:latest",
        command=["blobfuse_launcher"],
    )
    return Sidecar(container, {"command": ["blobfuse_launcher"]})


class TestBlobfuseSubdirectory(unittest.TestCase):
    """Tests for _get_blobfuse_sidecar subdirectory logic."""

    def _build(self):
        settings = CleanroomSettings(
            registryUrl="localhost:5000",
            sidecarsPolicyDocumentRegistryUrl="localhost:5000",
            versionsDocument="localhost:5000/sidecar-digests:latest",
            useHttp="true",
        )
        return CleanroomApplicationBuilder(settings)

    def _call_get_blobfuse_sidecar(self, access_point):
        """
        Call _get_blobfuse_sidecar with mocked _get_sidecar, returning
        a tuple of (captured replacement vars dict, returned Sidecar).
        """
        builder = self._build()
        captured_vars = {}

        def capture_get_sidecar(sidecar_name, sidecar_replacement_vars):
            captured_vars.update(sidecar_replacement_vars)
            return _stub_sidecar()

        with patch.object(builder, "_get_sidecar", side_effect=capture_get_sidecar):
            sidecar = builder._get_blobfuse_sidecar(
                access_point=access_point,
                mount_path="/mnt/remote",
                access_name="test-dataset",
                telemetry_mount_path="/mnt/telemetry",
                volume_status_mount_path="/mnt/volumestatus",
                readiness_port=6300,
            )
        return captured_vars, sidecar

    def _get_subdirectory_from_sidecar(self, sidecar):
        """Extract the subdirectory value from the sidecar command list."""
        cmd = sidecar.container.command
        if "--sub-directory" in cmd:
            idx = cmd.index("--sub-directory")
            return cmd[idx + 1]
        return ""

    # ------------------------------------------------------------------ #
    #  Case 1: explicit subdirectory on Azure Blob
    # ------------------------------------------------------------------ #
    def test_explicit_subdirectory_azure_blob(self):
        ap = _make_access_point(subdirectory="folder/a")
        vars, sidecar = self._call_get_blobfuse_sidecar(ap)
        self.assertEqual(self._get_subdirectory_from_sidecar(sidecar), "folder/a")

    # ------------------------------------------------------------------ #
    #  Case 2: omitted / empty subdirectory on Azure Blob
    # ------------------------------------------------------------------ #
    def test_empty_subdirectory_azure_blob(self):
        ap = _make_access_point(subdirectory="")
        vars, sidecar = self._call_get_blobfuse_sidecar(ap)
        self.assertEqual(self._get_subdirectory_from_sidecar(sidecar), "")

    def test_none_subdirectory_azure_blob(self):
        ap = _make_access_point(subdirectory=None)
        vars, sidecar = self._call_get_blobfuse_sidecar(ap)
        self.assertEqual(self._get_subdirectory_from_sidecar(sidecar), "")

    # ------------------------------------------------------------------ #
    #  Case 3: explicit subdirectory overrides OneLake URL-derived path
    # ------------------------------------------------------------------ #
    def test_explicit_subdirectory_overrides_onelake_url(self):
        ap = _make_access_point(
            protocol=ProtocolType.Azure_OneLake,
            storage_url="https://onelake.dfs.fabric.microsoft.com/workspace1/lakehouse1/Tables/default",
            container_name="testcontainer",
            subdirectory="my/override/path",
            resource_type=ResourceType.Azure_BlobStorage_DataLakeGen2,
        )
        vars, sidecar = self._call_get_blobfuse_sidecar(ap)
        # The explicit value should win over the URL-derived path
        # (which would be "lakehouse1/Tables/default").
        self.assertEqual(
            self._get_subdirectory_from_sidecar(sidecar), "my/override/path"
        )

    # ------------------------------------------------------------------ #
    #  Case 4: OneLake fallback when subdirectory is empty
    # ------------------------------------------------------------------ #
    def test_onelake_url_fallback_when_subdirectory_empty(self):
        ap = _make_access_point(
            protocol=ProtocolType.Azure_OneLake,
            storage_url="https://onelake.dfs.fabric.microsoft.com/workspace1/lakehouse1/Tables/default",
            container_name="testcontainer",
            subdirectory="",
            resource_type=ResourceType.Azure_BlobStorage_DataLakeGen2,
        )
        vars, sidecar = self._call_get_blobfuse_sidecar(ap)
        # URL-derived path: path is "/workspace1/lakehouse1/Tables/default"
        # After split("/")[2:] => ["lakehouse1", "Tables", "default"]
        self.assertEqual(
            self._get_subdirectory_from_sidecar(sidecar),
            "lakehouse1/Tables/default",
        )

    # ------------------------------------------------------------------ #
    #  Case 5: ADLS Gen2 with explicit subdirectory
    # ------------------------------------------------------------------ #
    def test_explicit_subdirectory_adls_gen2(self):
        ap = _make_access_point(
            protocol=ProtocolType.Azure_BlobStorage_DataLakeGen2,
            storage_url="https://teststorage.dfs.core.windows.net",
            subdirectory="data/year=2026",
            resource_type=ResourceType.Azure_BlobStorage_DataLakeGen2,
        )
        vars, sidecar = self._call_get_blobfuse_sidecar(ap)
        self.assertEqual(self._get_subdirectory_from_sidecar(sidecar), "data/year=2026")
        self.assertEqual(vars["useAdls"], "--use-adls")

    # ------------------------------------------------------------------ #
    #  Case 6: CPK with explicit subdirectory on Azure Blob
    #  Subdirectory must be propagated alongside the CPK encryption mode.
    # ------------------------------------------------------------------ #
    def test_explicit_subdirectory_cpk_azure_blob(self):
        ap = _make_access_point(
            subdirectory="2025-09-01",
            encryption_mode="CPK",
        )
        vars, sidecar = self._call_get_blobfuse_sidecar(ap)
        self.assertEqual(self._get_subdirectory_from_sidecar(sidecar), "2025-09-01")
        self.assertEqual(vars["encryptionMode"], "CPK")
        self.assertEqual(vars["cgsDekSecretId"], "test-dek-secret")

    # ------------------------------------------------------------------ #
    #  Case 7: CPK with empty subdirectory on Azure Blob
    # ------------------------------------------------------------------ #
    def test_empty_subdirectory_cpk_azure_blob(self):
        ap = _make_access_point(
            subdirectory="",
            encryption_mode="CPK",
        )
        vars, sidecar = self._call_get_blobfuse_sidecar(ap)
        self.assertEqual(self._get_subdirectory_from_sidecar(sidecar), "")
        self.assertEqual(vars["encryptionMode"], "CPK")
        self.assertEqual(vars["cgsDekSecretId"], "test-dek-secret")

    # ------------------------------------------------------------------ #
    #  Case 8: CPK with nested subdirectory on Azure Blob
    #  Subdirectory must be propagated alongside the CPK encryption mode.
    # ------------------------------------------------------------------ #
    def test_nested_subdirectory_cpk_azure_blob(self):
        ap = _make_access_point(
            subdirectory="data/partition=2026/region=us",
            encryption_mode="CPK",
        )
        vars, sidecar = self._call_get_blobfuse_sidecar(ap)
        self.assertEqual(
            self._get_subdirectory_from_sidecar(sidecar),
            "data/partition=2026/region=us",
        )
        self.assertEqual(vars["encryptionMode"], "CPK")

    # ------------------------------------------------------------------ #
    #  Case 9: CPK with ADLS Gen2 and explicit subdirectory
    #  Subdirectory must be propagated alongside the CPK encryption mode.
    # ------------------------------------------------------------------ #
    def test_explicit_subdirectory_cpk_adls_gen2(self):
        ap = _make_access_point(
            protocol=ProtocolType.Azure_BlobStorage_DataLakeGen2,
            storage_url="https://teststorage.dfs.core.windows.net",
            subdirectory="data/year=2026",
            resource_type=ResourceType.Azure_BlobStorage_DataLakeGen2,
            encryption_mode="CPK",
        )
        vars, sidecar = self._call_get_blobfuse_sidecar(ap)
        self.assertEqual(self._get_subdirectory_from_sidecar(sidecar), "data/year=2026")
        self.assertEqual(vars["useAdls"], "--use-adls")
        self.assertEqual(vars["encryptionMode"], "CPK")

    # ------------------------------------------------------------------ #
    #  Regression: virtual_node_policy must stay a dict (not a JSON string)
    #  so that downstream json.dumps in _get_rego_policy does not
    #  double-serialize the container policy.
    # ------------------------------------------------------------------ #
    def test_virtual_node_policy_remains_dict_after_subdirectory(self):
        ap = _make_access_point(subdirectory="folder/a")
        _, sidecar = self._call_get_blobfuse_sidecar(ap)
        self.assertIsInstance(sidecar.virtual_node_policy, dict)
        self.assertIn("--sub-directory", sidecar.virtual_node_policy["command"])
        self.assertIn("folder/a", sidecar.virtual_node_policy["command"])

    def test_virtual_node_policy_no_subdirectory_remains_dict(self):
        ap = _make_access_point(subdirectory="")
        _, sidecar = self._call_get_blobfuse_sidecar(ap)
        self.assertIsInstance(sidecar.virtual_node_policy, dict)
        self.assertNotIn(
            "--sub-directory", sidecar.virtual_node_policy.get("command", [])
        )

    # ------------------------------------------------------------------ #
    #  Case 10: CSE with explicit subdirectory on Azure Blob
    # ------------------------------------------------------------------ #
    @unittest.skip(
        "CSE not yet supported on cluster path (cleanroom_application_builder)"
    )
    def test_explicit_subdirectory_cse_azure_blob(self):
        ap = _make_access_point(
            subdirectory="cse-folder/data",
            encryption_mode="CSE",
        )
        vars, sidecar = self._call_get_blobfuse_sidecar(ap)
        self.assertEqual(
            self._get_subdirectory_from_sidecar(sidecar), "cse-folder/data"
        )
        self.assertEqual(vars["encryptionMode"], "CSE")

    # ------------------------------------------------------------------ #
    #  Case 11: CSE with empty subdirectory on Azure Blob
    # ------------------------------------------------------------------ #
    @unittest.skip(
        "CSE not yet supported on cluster path (cleanroom_application_builder)"
    )
    def test_empty_subdirectory_cse_azure_blob(self):
        ap = _make_access_point(
            subdirectory="",
            encryption_mode="CSE",
        )
        vars, sidecar = self._call_get_blobfuse_sidecar(ap)
        self.assertEqual(self._get_subdirectory_from_sidecar(sidecar), "")
        self.assertEqual(vars["encryptionMode"], "CSE")

    # ------------------------------------------------------------------ #
    #  Case 12: CSE with nested subdirectory on Azure Blob
    # ------------------------------------------------------------------ #
    @unittest.skip(
        "CSE not yet supported on cluster path (cleanroom_application_builder)"
    )
    def test_nested_subdirectory_cse_azure_blob(self):
        ap = _make_access_point(
            subdirectory="data/partition=2026/region=us",
            encryption_mode="CSE",
        )
        vars, sidecar = self._call_get_blobfuse_sidecar(ap)
        self.assertEqual(
            self._get_subdirectory_from_sidecar(sidecar),
            "data/partition=2026/region=us",
        )
        self.assertEqual(vars["encryptionMode"], "CSE")


if __name__ == "__main__":
    unittest.main()
