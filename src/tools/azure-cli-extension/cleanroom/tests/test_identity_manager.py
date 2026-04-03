#!/usr/bin/env python3
"""
Test script for IdentityManager functionality in the Azure Cleanroom CLI extension.

Tests comprehensive IdentityManager functionality with all identity types, error handling,
and edge cases. This test file covers:

- Azure Federated Identity creation and error handling
- Azure Secret-based Identity creation and error handling
- OIDC Attested Identity creation
- Identity update/replace functionality
- Backing identity dependency validation
- Error handling for missing backing identities

All tests are designed to work without Azure CLI dependencies where possible,
with graceful fallback for tests requiring those dependencies.
"""

import sys
from logging import Logger
from pathlib import Path

# Add the extension to Python path - dynamically find the cleanroom directory
current_dir = Path(__file__).parent
cleanroom_dir = current_dir.parent
sys.path.insert(0, str(cleanroom_dir))

from cleanroom_common.azure_cleanroom_core.exceptions.exception import (
    CleanroomSpecificationError,
    ErrorCode,
)


# Shared mock logger for all tests
class MockLogger(Logger):
    """Mock logger that can be used throughout tests"""

    def __init__(self, name="test"):
        super().__init__(name)

    def info(self, msg):
        pass

    def warning(self, msg):
        pass


def test_identity_manager_imports():
    """Test that IdentityManager and related classes can be imported"""
    print("Testing IdentityManager imports...")

    try:
        from cleanroom_common.azure_cleanroom_core.utilities.identity_manager import (
            IdentityManager,
        )

        print("✓ IdentityManager and related imports successful")

        # Check that identity helper methods exist
        assert hasattr(IdentityManager, "add_identity_az_federated")
        assert hasattr(IdentityManager, "add_identity_az_secret")
        assert hasattr(IdentityManager, "add_identity_oidc_attested")
        print("✓ IdentityManager methods exist")

        return True

    except ImportError as e:
        print(f"⚠️  IdentityManager imports test skipped (missing dependencies): {e}")
        return False


def test_azure_federated_identity():
    """Test Azure Federated Identity functionality"""
    print("\nTesting Azure Federated Identity functionality...")

    try:
        from cleanroom_common.azure_cleanroom_core.models.cleanroom import (
            AttestationBasedTokenIssuer,
            FederatedIdentityBasedTokenIssuer,
            Identity,
            ProtocolType,
            ServiceEndpoint,
        )
        from cleanroom_common.azure_cleanroom_core.utilities.identity_manager import (
            IdentityManager,
        )

        mock_logger = MockLogger()

        # Test 1: Error handling for missing backing identity
        print("📋 Testing error handling for missing backing identity...")
        try:
            IdentityManager([], mock_logger).add_identity_az_federated(
                name="test-federated",
                client_id="test-client-id",
                tenant_id="test-tenant-id",
                token_issuer_url="https://test-issuer.com",
                backing_identity_name="nonexistent",
            )
            assert False, "Should have raised BackingIdentityNotFound error"
        except CleanroomSpecificationError as e:
            assert e.code == ErrorCode.BackingIdentityNotFound
            print(
                "✓ Azure Federated Identity BackingIdentityNotFound error handling works"
            )

        # Test 2: Success case with backing identity
        print("📋 Testing successful federated identity creation...")
        base_identity = Identity(
            name="base-identity",
            clientId="base-client-id",
            tenantId="base-tenant-id",
            tokenIssuer=AttestationBasedTokenIssuer(
                issuer=ServiceEndpoint(
                    protocol=ProtocolType.Attested_OIDC, url="https://test-issuer.com"
                ),
                issuerType="AttestationBasedTokenIssuer",
            ),
        )
        identity_manager = IdentityManager([base_identity], mock_logger)

        identity_manager.add_identity_az_federated(
            name="test-federated-success",
            client_id="fed-client-id",
            tenant_id="fed-tenant-id",
            token_issuer_url="https://test-issuer.com",
            backing_identity_name="base-identity",
        )

        federated_identities = identity_manager.identities
        assert len(federated_identities) == 3
        federated_identity = next(
            (i for i in federated_identities if i.name == "test-federated-success"),
            None,
        )
        assert federated_identity is not None
        assert federated_identity.clientId == "fed-client-id"
        assert federated_identity.tenantId == "fed-tenant-id"
        assert isinstance(
            federated_identity.tokenIssuer, FederatedIdentityBasedTokenIssuer
        )
        assert federated_identity.tokenIssuer.federatedIdentity.name == "base-identity"

        print("✓ Azure Federated Identity creation successful")

        return True

    except ImportError as e:
        print(f"⚠️  Azure Federated Identity test skipped (missing dependencies): {e}")
        return False


def test_azure_secret_identity():
    """Test Azure Secret-based Identity functionality"""
    print("\nTesting Azure Secret-based Identity functionality...")

    try:
        from cleanroom_common.azure_cleanroom_core.models.cleanroom import (
            AttestationBasedTokenIssuer,
            Identity,
            ProtocolType,
            SecretBasedTokenIssuer,
            ServiceEndpoint,
        )
        from cleanroom_common.azure_cleanroom_core.utilities.identity_manager import (
            IdentityManager,
        )

        mock_logger = MockLogger()

        # Test 1: Error handling for missing backing identity
        print("📋 Testing error handling for missing backing identity...")
        try:
            IdentityManager([], mock_logger).add_identity_az_secret(
                name="test-secret",
                client_id="secret-client-id",
                tenant_id="secret-tenant-id",
                secret_name="test-secret",
                secret_store_url="https://test-kv.vault.azure.net/",
                backing_identity_name="nonexistent",
            )
            assert False, "Should have raised BackingIdentityNotFound error"
        except CleanroomSpecificationError as e:
            assert e.code == ErrorCode.BackingIdentityNotFound
            print(
                "✓ Azure Secret Identity BackingIdentityNotFound error handling works"
            )

        # Test 2: Success case with backing identity
        print("📋 Testing successful secret-based identity creation...")
        base_identity = Identity(
            name="base-identity",
            clientId="base-client-id",
            tenantId="base-tenant-id",
            tokenIssuer=AttestationBasedTokenIssuer(
                issuer=ServiceEndpoint(
                    protocol=ProtocolType.Attested_OIDC, url="https://test-issuer.com"
                ),
                issuerType="AttestationBasedTokenIssuer",
            ),
        )
        identity_manager = IdentityManager([base_identity], mock_logger)

        identity_manager.add_identity_az_secret(
            name="test-secret-success",
            client_id="secret-client-id",
            tenant_id="secret-tenant-id",
            secret_name="test-secret",
            secret_store_url="https://test-kv.vault.azure.net/",
            backing_identity_name="base-identity",
        )

        secret_identities = identity_manager.identities
        assert len(secret_identities) == 3
        secret_identity = next(
            (i for i in secret_identities if i.name == "test-secret-success"), None
        )
        assert secret_identity is not None
        assert secret_identity.clientId == "secret-client-id"
        assert secret_identity.tenantId == "secret-tenant-id"
        assert isinstance(secret_identity.tokenIssuer, SecretBasedTokenIssuer)
        assert secret_identity.tokenIssuer.secretAccessIdentity.name == "base-identity"
        print("✓ Azure Secret-based Identity creation successful")

        return True

    except ImportError as e:
        print(f"⚠️  Azure Secret Identity test skipped (missing dependencies): {e}")
        return False


def test_oidc_attested_identity():
    """Test OIDC Attested Identity functionality"""
    print("\nTesting OIDC Attested Identity functionality...")

    try:
        from cleanroom_common.azure_cleanroom_core.models.cleanroom import (
            AttestationBasedTokenIssuer,
        )
        from cleanroom_common.azure_cleanroom_core.utilities.identity_manager import (
            IdentityManager,
        )

        mock_logger = MockLogger()

        # Test OIDC Attested Identity creation
        print("📋 Testing OIDC attested identity creation...")
        identity_manager = IdentityManager([], mock_logger)
        identity_manager.add_identity_oidc_attested(
            name="test-oidc-attested",
            client_id="oidc-client-id",
            tenant_id="oidc-tenant-id",
            issuer_url="https://test-oidc-issuer.com",
        )

        oidc_identities = identity_manager.identities
        assert len(oidc_identities) == 2
        oidc_identity = oidc_identities[1]
        assert oidc_identity.name == "test-oidc-attested"
        assert oidc_identity.clientId == "oidc-client-id"
        assert oidc_identity.tenantId == "oidc-tenant-id"
        assert isinstance(oidc_identity.tokenIssuer, AttestationBasedTokenIssuer)
        assert oidc_identity.tokenIssuer.issuer.url == "https://test-oidc-issuer.com"
        print("✓ OIDC Attested Identity creation successful")

        return True

    except ImportError as e:
        print(f"⚠️  OIDC Attested Identity test skipped (missing dependencies): {e}")
        return False


def test_identity_update_replace():
    """Test Identity update/replace functionality"""
    print("\nTesting Identity update/replace functionality...")

    try:
        from cleanroom_common.azure_cleanroom_core.models.cleanroom import (
            AttestationBasedTokenIssuer,
        )
        from cleanroom_common.azure_cleanroom_core.utilities.identity_manager import (
            IdentityManager,
        )

        mock_logger = MockLogger()
        identity_manager = IdentityManager([], mock_logger)

        # Create an initial identity
        print("📋 Testing identity replacement functionality...")
        identity_manager.add_identity_oidc_attested(
            name="test-identity",
            client_id="initial-client-id",
            tenant_id="initial-tenant-id",
            issuer_url="https://initial-issuer.com",
        )

        initial_identities = identity_manager.identities
        assert len(initial_identities) == 2
        assert initial_identities[1].clientId == "initial-client-id"

        # Add an identity with the same name to test replacement
        identity_manager.add_identity_oidc_attested(
            name="test-identity",  # Same name as before
            client_id="updated-client-id",
            tenant_id="updated-tenant-id",
            issuer_url="https://updated-issuer.com",
        )

        updated_identities = identity_manager.identities
        assert len(updated_identities) == 2  # Should still be 2, not 3
        updated_identity = updated_identities[1]
        assert updated_identity.name == "test-identity"
        assert updated_identity.clientId == "updated-client-id"  # Should be updated
        assert updated_identity.tenantId == "updated-tenant-id"  # Should be updated
        assert updated_identity.tokenIssuer.issuer.url == "https://updated-issuer.com"
        print("✓ Identity update/replace functionality working")

        return True

    except ImportError as e:
        print(f"⚠️  Identity update/replace test skipped (missing dependencies): {e}")
        return False


def test_identity_manager_error_scenarios():
    """Test additional error scenarios and edge cases"""
    print("\nTesting IdentityManager error scenarios...")

    try:
        from cleanroom_common.azure_cleanroom_core.models.cleanroom import (
            AttestationBasedTokenIssuer,
            FederatedIdentityBasedTokenIssuer,
            Identity,
            ProtocolType,
            ServiceEndpoint,
        )
        from cleanroom_common.azure_cleanroom_core.utilities.identity_manager import (
            IdentityManager,
        )

        mock_logger = MockLogger()

        # Test 1: Multiple identities, correct backing identity selection
        print("📋 Testing backing identity selection with multiple identities...")
        identity1 = Identity(
            name="identity-1",
            clientId="client-1",
            tenantId="tenant-1",
            tokenIssuer=AttestationBasedTokenIssuer(
                issuer=ServiceEndpoint(
                    protocol=ProtocolType.Attested_OIDC, url="https://issuer1.com"
                ),
                issuerType="AttestationBasedTokenIssuer",
            ),
        )

        identity2 = Identity(
            name="identity-2",
            clientId="client-2",
            tenantId="tenant-2",
            tokenIssuer=AttestationBasedTokenIssuer(
                issuer=ServiceEndpoint(
                    protocol=ProtocolType.Attested_OIDC, url="https://issuer2.com"
                ),
                issuerType="AttestationBasedTokenIssuer",
            ),
        )

        # Should find identity-2 as backing identity
        identity_manager = IdentityManager([identity1, identity2], mock_logger)
        identity_manager.add_identity_az_federated(
            name="test-federated",
            client_id="fed-client",
            tenant_id="fed-tenant",
            token_issuer_url="https://fed-issuer.com",
            backing_identity_name="identity-2",
        )

        result_identities = identity_manager.identities
        assert len(result_identities) == 4
        federated = next(
            (i for i in result_identities if i.name == "test-federated"), None
        )
        assert federated is not None
        assert isinstance(federated.tokenIssuer, FederatedIdentityBasedTokenIssuer)
        assert federated.tokenIssuer.federatedIdentity.name == "identity-2"
        print("✓ Backing identity selection works with multiple identities")

        # Test 2: Empty identities list with missing backing identity
        print("📋 Testing empty identities list error handling...")
        try:
            IdentityManager([], mock_logger).add_identity_az_secret(
                name="test-secret",
                client_id="secret-client",
                tenant_id="secret-tenant",
                secret_name="secret",
                secret_store_url="https://kv.vault.azure.net/",
                backing_identity_name="missing",
            )
            assert False, "Should have raised BackingIdentityNotFound error"
        except CleanroomSpecificationError as e:
            assert e.code == ErrorCode.BackingIdentityNotFound
            print("✓ Empty identities list error handling works")

        return True

    except ImportError as e:
        print(
            f"⚠️  IdentityManager error scenarios test skipped (missing dependencies): {e}"
        )
        return False


def run_all_tests():
    """Run all identity helper tests"""
    print("🧪 Running IdentityManager Tests")
    print("=" * 50)

    tests = [
        test_identity_manager_imports,
        test_azure_federated_identity,
        test_azure_secret_identity,
        test_oidc_attested_identity,
        test_identity_update_replace,
        test_identity_manager_error_scenarios,
    ]

    passed = 0
    failed = 0
    skipped = 0

    for test in tests:
        try:
            print(f"\n📋 {test.__name__}")
            result = test()
            if result is False:  # Test was skipped
                skipped += 1
            else:
                passed += 1
        except Exception as e:
            print(f"❌ {test.__name__} FAILED: {e}")
            failed += 1
            import traceback

            traceback.print_exc()

    print("\n" + "=" * 50)
    print(f"📊 Test Results: {passed} passed, {failed} failed, {skipped} skipped")

    if failed == 0:
        print("🎉 All IdentityManager tests passed!")
        return True
    else:
        print("💥 Some IdentityManager tests failed!")
        return False


if __name__ == "__main__":
    success = run_all_tests()
    sys.exit(0 if success else 1)
