#!/usr/bin/env python3
"""
Test script for Collaboration functionality in the Azure Cleanroom CLI extension.

Tests Collaboration models, configuration management, environment variable handling,
empty configuration file processing, and basic identity helper integration. This test file covers:

- CollaborationContext and CollaborationSpecification model functionality
- Configuration file read/write operations via configuration helpers
- Empty file and comment-only configuration handling
- Collaboratio               # Test basic error handling with collaboration.identities (detailed tests are in test_identity_manager.py)
        mock_logger = MockLogger()

        # Test error handling for missing backing identitytityHelper methods exist")

        # Test basic error handling with collaboration.identities (detailed tests are in test_identity_manager.py)
        mock_logger = MockLogger()

        # Test error handling for missing backing identitylass functionality (when Azure CLI deps available)
- Environment variable override behavior (CLEANROOM_COLLABORATION_CONFIG_FILE)
- Basic identity helper integration (comprehensive tests in test_identity_manager.py)
- Error handling, validation, and edge cases

All tests are designed to work without Azure CLI dependencies where possible,
with graceful fallback for tests requiring those dependencies.
"""

import os
import sys
import tempfile
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
from cleanroom_common.azure_cleanroom_core.models.cleanroom import Identity
from cleanroom_common.azure_cleanroom_core.models.collaboration import (
    CollaborationContext,
    CollaborationSpecification,
)


# Shared mock logger for testing
class MockLogger(Logger):
    """Mock logger that can be used throughout tests"""

    def __init__(self, name="test"):
        super().__init__(name)

    def info(self, msg):
        pass

    def warning(self, msg):
        pass


def test_collaboration_models():
    """Test Collaboration model functionality"""
    print("Testing Collaboration models...")

    # Test creating a collaboration entry
    collaboration = CollaborationContext(
        name="test-collaboration",
        collaborator_id="test-user",
        governance_client_name="https://test-cgs.example.com",
        identities=[],
    )

    assert collaboration.name == "test-collaboration"
    assert collaboration.governance_client_name == "https://test-cgs.example.com"
    assert collaboration.identities == []
    print("✓ CollaborationContext creation successful")

    # Test creating a collaboration specification
    spec = CollaborationSpecification(collaborations=[collaboration])
    assert spec.collaborations is not None
    assert len(spec.collaborations) == 1
    assert spec.collaborations[0].name == "test-collaboration"
    print("✓ CollaborationSpecification creation successful")

    # Test empty collaboration specification
    empty_spec = CollaborationSpecification()
    assert empty_spec.collaborations == []
    print("✓ Empty CollaborationSpecification creation successful")


def test_collaboration_specification_methods():
    """Test CollaborationSpecification methods"""
    print("Testing CollaborationSpecification methods...")

    collaboration1 = CollaborationContext(
        name="collab1",
        collaborator_id="user1",
        governance_client_name="https://cgs1.example.com",
        identities=[],
    )
    collaboration2 = CollaborationContext(
        name="collab2",
        collaborator_id="user2",
        governance_client_name="https://cgs2.example.com",
        identities=[],
    )

    spec = CollaborationSpecification(collaborations=[collaboration1, collaboration2])

    # Test check_collaboration_context
    exists, index, entry = spec.check_collaboration_context("collab1")
    assert exists == True
    assert index == 0
    assert entry is not None
    assert entry.name == "collab1"
    print("✓ check_collaboration_context for existing collaboration successful")

    exists, index, entry = spec.check_collaboration_context("nonexistent")
    assert exists == False
    assert index == None
    assert entry == None
    print("✓ check_collaboration_context for non-existing collaboration successful")

    # Test get_collaboration_context
    entry = spec.get_collaboration_context("collab2")
    assert entry.name == "collab2"
    print("✓ get_collaboration_context for existing collaboration successful")

    # Test get_collaboration_context with non-existing collaboration
    try:
        spec.get_collaboration_context("nonexistent")
        assert False, "Should have raised CleanroomSpecificationError"
    except CleanroomSpecificationError as e:
        assert e.code == ErrorCode.CollaborationNotFound
        print("✓ get_collaboration_context error handling successful")

    # Test add_collaboration_context
    new_collaboration = CollaborationContext(
        name="collab3",
        collaborator_id="user3",
        governance_client_name="https://cgs3.example.com",
        identities=[],
    )
    spec.add_collaboration_context(new_collaboration)
    assert spec.collaborations is not None
    assert len(spec.collaborations) == 3
    assert spec.collaborations[2].name == "collab3"
    print("✓ add_collaboration_context successful")

    # Test add_collaboration_context with duplicate name
    try:
        spec.add_collaboration_context(collaboration1)
        assert False, "Should have raised CleanroomSpecificationError"
    except CleanroomSpecificationError as e:
        assert e.code == ErrorCode.CollaborationAlreadyExists
        print("✓ add_collaboration_context duplicate error handling successful")


def test_configuration_helpers():
    """Test configuration file read/write operations with identity persistence"""
    print("Testing configuration helpers...")

    try:
        from logging import Logger

        from cleanroom_common.azure_cleanroom_core.utilities.configuration_helpers import (
            read_collaboration_config,
            write_collaboration_config,
        )
        from cleanroom_common.azure_cleanroom_core.utilities.identity_manager import (
            IdentityManager,
        )

        # Mock logger for testing
        logger = MockLogger()

        # Test collaboration configuration with identities
        with tempfile.NamedTemporaryFile(mode="w", suffix=".yaml", delete=False) as f:
            temp_file = f.name

        try:
            # Create a test collaboration specification with identities
            collaboration = CollaborationContext(
                name="test-config-collaboration",
                collaborator_id="test-user",
                governance_client_name="https://test-config.example.com",
                identities=[],
            )

            # Add some identities to test persistence
            IdentityManager(
                collaboration.identities, logger
            ).add_identity_oidc_attested(
                name="base-identity",
                client_id="base-client-id",
                tenant_id="base-tenant-id",
                issuer_url="https://test-oidc-issuer.com",
            )

            IdentityManager(collaboration.identities, logger).add_identity_az_secret(
                name="secret-identity",
                client_id="secret-client-id",
                tenant_id="secret-tenant-id",
                secret_name="test-secret",
                secret_store_url="https://test-kv.vault.azure.net/",
                backing_identity_name="base-identity",
            )

            spec = CollaborationSpecification(collaborations=[collaboration])

            # Write config with identities
            write_collaboration_config(temp_file, spec, logger)
            assert os.path.exists(temp_file)
            print("✓ Collaboration config with identities write successful")

            # Read config back and verify identities are persisted
            loaded_spec = read_collaboration_config(temp_file, logger)
            assert loaded_spec.collaborations is not None
            assert len(loaded_spec.collaborations) == 1
            loaded_collaboration = loaded_spec.collaborations[0]
            assert loaded_collaboration.name == "test-config-collaboration"
            assert (
                loaded_collaboration.governance_client_name
                == "https://test-config.example.com"
            )

            # Verify identities were persisted correctly
            assert loaded_collaboration.identities is not None
            assert len(loaded_collaboration.identities) == 3

            # Check base identity
            base_identity = next(
                (
                    i
                    for i in loaded_collaboration.identities
                    if i.name == "base-identity"
                ),
                None,
            )
            assert base_identity is not None
            assert base_identity.clientId == "base-client-id"
            assert base_identity.tenantId == "base-tenant-id"
            print("✓ Base OIDC identity correctly persisted and loaded")

            # Check secret identity
            secret_identity = next(
                (
                    i
                    for i in loaded_collaboration.identities
                    if i.name == "secret-identity"
                ),
                None,
            )
            assert secret_identity is not None
            assert secret_identity.clientId == "secret-client-id"
            assert secret_identity.tenantId == "secret-tenant-id"
            assert hasattr(secret_identity.tokenIssuer, "secretAccessIdentity")
            print("✓ Secret-based identity correctly persisted and loaded")

            print("✓ Collaboration config read with identity persistence successful")

        finally:
            if os.path.exists(temp_file):
                os.unlink(temp_file)

    except ImportError as e:
        print(f"⚠️  Configuration helpers test skipped (missing dependencies): {e}")


def test_collaboration_empty_config_handling():
    """Test handling of empty Collaboration config files"""
    print("\nTesting empty Collaboration config handling...")

    try:
        from cleanroom_common.azure_cleanroom_core.utilities.configuration_helpers import (
            read_collaboration_config,
        )

        logger = MockLogger()

        # Test empty collaboration config
        with tempfile.NamedTemporaryFile(mode="w", suffix=".yaml", delete=False) as f:
            f.write("")  # Empty file
            temp_file = f.name

        try:
            spec = read_collaboration_config(temp_file, logger)
            assert spec.collaborations is None or len(spec.collaborations) == 0
            print("✓ Empty collaboration config handled correctly")
        finally:
            os.unlink(temp_file)

        # Test missing fields in config
        with tempfile.NamedTemporaryFile(mode="w", suffix=".yaml", delete=False) as f:
            f.write("# Comment only")
            temp_file = f.name

        try:
            spec = read_collaboration_config(temp_file, logger)
            assert spec.collaborations is None or len(spec.collaborations) == 0
            print("✓ Collaboration config with missing fields handled correctly")
        finally:
            os.unlink(temp_file)

    except ImportError as e:
        print(
            f"⚠️  Collaboration empty config handling test skipped (missing dependencies): {e}"
        )


def test_collaboration_configuration_classes():
    """Test Collaboration Configuration class functionality with identity persistence"""
    print("\nTesting Collaboration Configuration classes...")

    try:
        from logging import Logger

        from azext_cleanroom.utilities.collaboration_helper import (
            CollaborationConfiguration,
        )
        from cleanroom_common.azure_cleanroom_core.utilities.identity_manager import (
            IdentityManager,
        )

        # Test default file paths
        default_collaboration_path = (
            CollaborationConfiguration.default_collaboration_config_file()
        )
        assert default_collaboration_path.endswith("collaborations.yaml")
        print("✓ Collaboration default file path working")

        # Use shared mock logger for testing
        logger = MockLogger()

        # Test with temporary files including identity persistence
        with tempfile.NamedTemporaryFile(mode="w", suffix=".yaml", delete=False) as f:
            # Create a collaboration spec with identities for testing persistence
            collaboration = CollaborationContext(
                name="config-class-test",
                collaborator_id="test-user",
                governance_client_name="https://config-class-test.example.com",
                identities=[],
            )

            # Add identities to test persistence through CollaborationConfiguration
            IdentityManager(
                collaboration.identities, logger
            ).add_identity_oidc_attested(
                name="config-test-identity",
                client_id="config-client-id",
                tenant_id="config-tenant-id",
                issuer_url="https://config-oidc-issuer.com",
            )

            spec = CollaborationSpecification(collaborations=[collaboration])

            # Write initial spec with identities to file
            from cleanroom_common.azure_cleanroom_core.utilities.configuration_helpers import (
                write_collaboration_config,
            )

            write_collaboration_config(f.name, spec, logger)
            temp_file = f.name

        try:
            # Test loading config with identities
            config = CollaborationConfiguration.load(
                temp_file, create_if_not_existing=True, require_current_context=False
            )
            assert isinstance(config, CollaborationSpecification)
            assert config.collaborations is not None
            assert len(config.collaborations) == 1

            loaded_collaboration = config.collaborations[0]
            assert loaded_collaboration.name == "config-class-test"
            assert loaded_collaboration.identities is not None
            assert len(loaded_collaboration.identities) == 2
            assert loaded_collaboration.identities[1].name == "config-test-identity"
            print(
                "✓ Collaboration configuration loading with identity persistence working"
            )

        finally:
            os.unlink(temp_file)

    except ImportError as e:
        print(
            f"⚠️  Collaboration configuration class test skipped due to missing dependencies: {e}"
        )


def test_collaboration_environment_variables():
    """Test Collaboration environment variable override functionality with identity persistence"""
    print("\nTesting Collaboration environment variable handling...")

    try:
        from logging import Logger

        from azext_cleanroom.utilities.collaboration_helper import (
            CollaborationConfiguration,
        )
        from cleanroom_common.azure_cleanroom_core.utilities.identity_manager import (
            IdentityManager,
        )

        # Use shared mock logger for testing
        logger = MockLogger()

        # Create test collaboration config with content including identities
        with tempfile.NamedTemporaryFile(mode="w", suffix=".yaml", delete=False) as f:
            # Create collaboration with identities
            collaboration = CollaborationContext(
                name="env-test-collaboration",
                collaborator_id="test-user",
                governance_client_name="https://env-test-cgs.example.com",
                identities=[],
            )

            # Add identities to test environment variable handling with persistence
            IdentityManager(
                collaboration.identities, logger
            ).add_identity_oidc_attested(
                name="env-test-identity",
                client_id="env-client-id",
                tenant_id="env-tenant-id",
                issuer_url="https://env-oidc-issuer.com",
            )

            spec = CollaborationSpecification(collaborations=[collaboration])

            # Write config with identities
            from cleanroom_common.azure_cleanroom_core.utilities.configuration_helpers import (
                write_collaboration_config,
            )

            write_collaboration_config(f.name, spec, logger)
            env_collaboration_file = f.name

        try:
            # Store original env value
            original_collaboration_env = os.environ.get(
                "CLEANROOM_COLLABORATION_CONFIG_FILE"
            )

            # Test 1: No environment variable set (should use default path)
            if "CLEANROOM_COLLABORATION_CONFIG_FILE" in os.environ:
                del os.environ["CLEANROOM_COLLABORATION_CONFIG_FILE"]

            default_collaboration_path = (
                CollaborationConfiguration.default_collaboration_config_file()
            )
            assert default_collaboration_path.endswith("collaborations.yaml")
            print("✓ Collaboration default path without environment variable")

            # Test 2: Environment variable set (should use env var path)
            os.environ["CLEANROOM_COLLABORATION_CONFIG_FILE"] = env_collaboration_file
            env_collaboration_path = (
                CollaborationConfiguration.default_collaboration_config_file()
            )
            assert env_collaboration_path == env_collaboration_file
            assert env_collaboration_path != default_collaboration_path
            print("✓ Collaboration environment variable override working")

            # Test 3: Load configuration using environment variable with identity persistence
            config_from_env = CollaborationConfiguration.load(
                env_collaboration_path, require_current_context=False
            )
            assert config_from_env.collaborations is not None
            assert len(config_from_env.collaborations) == 1

            loaded_collaboration = config_from_env.collaborations[0]
            assert loaded_collaboration.name == "env-test-collaboration"
            assert loaded_collaboration.identities is not None
            assert len(loaded_collaboration.identities) == 2
            assert loaded_collaboration.identities[1].name == "env-test-identity"
            assert loaded_collaboration.identities[1].clientId == "env-client-id"
            print(
                "✓ Collaboration configuration with identities loaded from environment variable path"
            )

        finally:
            # Cleanup temp files
            if os.path.exists(env_collaboration_file):
                os.unlink(env_collaboration_file)

            # Restore original env value
            if original_collaboration_env is not None:
                os.environ["CLEANROOM_COLLABORATION_CONFIG_FILE"] = (
                    original_collaboration_env
                )
            elif "CLEANROOM_COLLABORATION_CONFIG_FILE" in os.environ:
                del os.environ["CLEANROOM_COLLABORATION_CONFIG_FILE"]

    except ImportError as e:
        print(
            f"⚠️  Collaboration environment variable test skipped due to missing dependencies: {e}"
        )


def test_identity_manager_integration():
    """Test IdentityManager integration with collaboration models and identity persistence"""
    print("\nTesting IdentityManager integration...")

    try:
        from logging import Logger

        from cleanroom_common.azure_cleanroom_core.exceptions.exception import (
            CleanroomSpecificationError,
            ErrorCode,
        )
        from cleanroom_common.azure_cleanroom_core.models.cleanroom import (
            FederatedIdentityBasedTokenIssuer,
            SecretBasedTokenIssuer,
        )
        from cleanroom_common.azure_cleanroom_core.utilities.identity_manager import (
            IdentityManager,
        )

        print("✓ IdentityManager import successful")

        # Create a collaboration with identities to work with
        collaboration = CollaborationContext(
            name="test-identity-collaboration",
            collaborator_id="test-user",
            governance_client_name="https://test-identity.example.com",
            identities=[],
        )

        # Check that identity helper methods exist
        assert hasattr(IdentityManager, "add_identity_az_federated")
        assert hasattr(IdentityManager, "add_identity_az_secret")
        assert hasattr(IdentityManager, "add_identity_oidc_attested")
        print("✓ IdentityManager methods exist")

        # Test basic error handling with collaboration.identities (detailed tests are in test_identity_manager.py)
        class MockLogger(Logger):
            def __init__(self):
                super().__init__("test")

            def info(self, msg):
                pass

            def warning(self, msg):
                pass

        mock_logger = MockLogger()

        # Test error handling when backing identity doesn't exist
        try:
            IdentityManager(
                collaboration.identities, mock_logger
            ).add_identity_az_federated(
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
                "✓ IdentityManager error handling with collaboration.identities works"
            )

        # Test successful identity addition to collaboration
        # First add a base OIDC attested identity (doesn't need backing identity)
        IdentityManager(
            collaboration.identities, mock_logger
        ).add_identity_oidc_attested(
            name="base-oidc",
            client_id="base-client-id",
            tenant_id="base-tenant-id",
            issuer_url="https://test-oidc-issuer.com",
        )
        assert len(collaboration.identities) == 2
        assert collaboration.identities[1].name == "base-oidc"
        print("✓ Successfully added base OIDC identity to collaboration")

        # Now add a secret-based identity that uses the base identity as backing
        IdentityManager(collaboration.identities, mock_logger).add_identity_az_secret(
            name="secret-identity",
            client_id="secret-client-id",
            tenant_id="secret-tenant-id",
            secret_name="test-secret",
            secret_store_url="https://test-kv.vault.azure.net/",
            backing_identity_name="base-oidc",
        )
        assert len(collaboration.identities) == 3
        assert collaboration.identities[2].name == "secret-identity"
        print(
            "✓ Successfully added secret-based identity with backing identity to collaboration"
        )

        # Now add a federated identity that uses the base identity as backing
        IdentityManager(
            collaboration.identities, mock_logger
        ).add_identity_az_federated(
            name="federated-identity",
            client_id="fed-client-id",
            tenant_id="fed-tenant-id",
            token_issuer_url="https://fed-issuer.com",
            backing_identity_name="base-oidc",
        )
        assert len(collaboration.identities) == 4
        assert collaboration.identities[3].name == "federated-identity"
        print(
            "✓ Successfully added federated identity with backing identity to collaboration"
        )

        # Verify the identity references are properly set up
        secret_identity = next(
            (i for i in collaboration.identities if i.name == "secret-identity"), None
        )
        assert secret_identity is not None
        assert isinstance(secret_identity.tokenIssuer, SecretBasedTokenIssuer)
        assert secret_identity.tokenIssuer.secretAccessIdentity.name == "base-oidc"

        print("✓ Secret identity has proper backing identity reference")

        federated_identity = next(
            (i for i in collaboration.identities if i.name == "federated-identity"),
            None,
        )
        assert federated_identity is not None
        assert isinstance(
            federated_identity.tokenIssuer, FederatedIdentityBasedTokenIssuer
        )
        assert federated_identity.tokenIssuer.federatedIdentity.name == "base-oidc"

        print("✓ Federated identity has proper backing identity reference")

        print(
            "✓ IdentityManager integrates correctly with collaboration models and identities"
        )

    except ImportError as e:
        print(
            f"⚠️  IdentityManager integration test skipped (missing dependencies): {e}"
        )


def test_collaboration_cmd_imports():
    """Test that collaboration_cmd can be imported"""
    print("\nTesting collaboration_cmd imports...")

    try:
        # Try to import the collaboration command functions
        sys.path.insert(0, str(cleanroom_dir / "azext_cleanroom"))

        # Test basic import structure
        from azext_cleanroom.utilities.collaboration_helper import (
            CollaborationConfiguration,
            CollaborationContext,
        )

        print("✓ Collaboration helper utilities import successful")

        # Test that the functions exist (we can't test execution without CLI dependencies)
        import azext_cleanroom.collaboration_cmd as collab_cmd

        assert hasattr(collab_cmd, "collaboration_context_add_cmd")
        assert hasattr(collab_cmd, "collaboration_identity_add_az_federated_cmd")
        assert hasattr(collab_cmd, "collaboration_dataset_publish_cmd")
        print("✓ Collaboration command functions exist")

    except ImportError as e:
        print(f"⚠️  Collaboration cmd test skipped (missing CLI dependencies): {e}")


def run_all_tests():
    """Run all collaboration tests"""
    print("🧪 Running Collaboration Tests")
    print("=" * 50)

    tests = [
        test_collaboration_models,
        test_collaboration_specification_methods,
        test_configuration_helpers,
        test_collaboration_empty_config_handling,
        test_collaboration_configuration_classes,
        test_collaboration_environment_variables,
        test_collaboration_cmd_imports,
        test_identity_manager_integration,
    ]

    passed = 0
    failed = 0

    for test in tests:
        try:
            print(f"\n📋 {test.__name__}")
            test()
            passed += 1
        except Exception as e:
            print(f"❌ {test.__name__} FAILED: {e}")
            failed += 1
            import traceback

            traceback.print_exc()

    print("\n" + "=" * 50)
    print(f"📊 Test Results: {passed} passed, {failed} failed")

    if failed == 0:
        print("🎉 All tests passed!")
        return True
    else:
        print("💥 Some tests failed!")
        return False


if __name__ == "__main__":
    success = run_all_tests()
    sys.exit(0 if success else 1)
