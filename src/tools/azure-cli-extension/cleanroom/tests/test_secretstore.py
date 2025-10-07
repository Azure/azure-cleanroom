#!/usr/bin/env python3
"""
Test script for SecretStore functionality in the Azure Cleanroom CLI extension.

Tests SecretStore models, configuration management, environment variable handling,
and empty configuration file processing. This test file covers:

- SecretStoreEntry and SecretStoreSpecification model functionality
- Configuration file read/write operations via configuration helpers
- Empty file and comment-only configuration handling
- SecretStoreConfiguration class functionality (when Azure CLI deps available)
- Environment variable override behavior (CLEANROOM_SECRETSTORE_CONFIG_FILE)
- Error handling, validation, and edge cases

All tests are designed to work without Azure CLI dependencies where possible,
with graceful fallback for tests requiring those dependencies.
"""

import os
import sys
import tempfile
from pathlib import Path

# Add the extension to Python path - dynamically find the cleanroom directory
current_dir = Path(__file__).parent
cleanroom_dir = current_dir.parent
sys.path.insert(0, str(cleanroom_dir))

from cleanroom_common.azure_cleanroom_core.exceptions.exception import (
    CleanroomSpecificationError,
    ErrorCode,
)
from cleanroom_common.azure_cleanroom_core.models.secretstore import (
    SecretStoreEntry,
    SecretStoreSpecification,
)


def test_secretstore_models():
    """Test SecretStore model functionality"""
    print("Testing SecretStore models...")

    # Test creating a secretstore entry
    secretstore = SecretStoreEntry(
        name="test-secretstore",
        secretStoreType=SecretStoreEntry.SecretStoreType.Local_File,
        storeProviderUrl="/tmp/secrets",
        configuration="",
        supportedSecretTypes=[SecretStoreEntry.SupportedSecretTypes.Secret],
    )
    assert secretstore.name == "test-secretstore"
    assert secretstore.is_secret_supported()
    assert not secretstore.is_key_release_supported()
    print("✓ SecretStoreEntry creation successful")

    # Test specification
    spec = SecretStoreSpecification()
    assert spec.secretstores is None or len(spec.secretstores) == 0

    # Test adding entry
    spec.add_secretstore_entry(secretstore)
    assert spec.secretstores is not None and len(spec.secretstores) == 1
    print("✓ SecretStore entry addition successful")

    # Test duplicate detection
    try:
        spec.add_secretstore_entry(secretstore)
        assert False, "Should have raised exception for duplicate"
    except CleanroomSpecificationError as e:
        assert e.code == ErrorCode.SecretStoreAlreadyExists
        print("✓ Duplicate detection working")

    # Test retrieval
    exists, index, entry = spec.check_secretstore_entry("test-secretstore")
    assert (
        exists and index == 0 and entry is not None and entry.name == "test-secretstore"
    )
    print("✓ SecretStore retrieval successful")

    # Test missing entry
    try:
        spec.get_secretstore_entry("nonexistent")
        assert False, "Should have raised exception for missing entry"
    except CleanroomSpecificationError as e:
        assert e.code == ErrorCode.SecretStoreNotFound
        print("✓ Missing entry detection working")


def test_secretstore_configuration_helpers():
    """Test SecretStore configuration file operations"""
    print("\nTesting SecretStore configuration helpers...")

    try:
        from cleanroom_common.azure_cleanroom_core.utilities.configuration_helpers import (
            read_secretstore_config,
            write_secretstore_config,
        )

        # Create a simple logger mock
        class MockLogger:
            def info(self, msg):
                pass

            def warning(self, msg):
                pass

            def error(self, msg):
                pass

        logger = MockLogger()

        # Test SecretStore configuration
        with tempfile.NamedTemporaryFile(mode="w", suffix=".yaml", delete=False) as f:
            temp_file = f.name

        try:
            # Create a secretstore config
            secretstore = SecretStoreEntry(
                name="test-ss",
                secretStoreType=SecretStoreEntry.SecretStoreType.Local_File,
                storeProviderUrl="/tmp/test-secrets",
                configuration="test-config",
                supportedSecretTypes=[SecretStoreEntry.SupportedSecretTypes.Secret],
            )

            spec = SecretStoreSpecification()
            spec.add_secretstore_entry(secretstore)

            # Write and read back
            write_secretstore_config(temp_file, spec, logger)
            read_spec = read_secretstore_config(temp_file, logger)

            assert read_spec.secretstores is not None
            assert len(read_spec.secretstores) == 1
            assert read_spec.secretstores[0].name == "test-ss"
            assert read_spec.secretstores[0].secretStoreType == "Local_File"
            print("✓ SecretStore config write/read successful")

        finally:
            if os.path.exists(temp_file):
                os.unlink(temp_file)

    except ImportError as e:
        print(
            f"⚠ SecretStore configuration helpers test skipped due to missing dependencies: {e}"
        )


def test_secretstore_empty_config_handling():
    """Test handling of empty SecretStore config files"""
    print("\nTesting empty SecretStore config handling...")

    try:
        from cleanroom_common.azure_cleanroom_core.utilities.configuration_helpers import (
            read_secretstore_config,
        )

        class MockLogger:
            def info(self, msg):
                pass

        logger = MockLogger()

        # Test empty secretstore config
        with tempfile.NamedTemporaryFile(mode="w", suffix=".yaml", delete=False) as f:
            f.write("")  # Empty file
            temp_file = f.name

        try:
            spec = read_secretstore_config(temp_file, logger)
            assert spec.secretstores is None or len(spec.secretstores) == 0
            print("✓ Empty secretstore config handled correctly")
        finally:
            os.unlink(temp_file)

        # Test missing fields in config
        with tempfile.NamedTemporaryFile(mode="w", suffix=".yaml", delete=False) as f:
            f.write("# Comment only")
            temp_file = f.name

        try:
            spec = read_secretstore_config(temp_file, logger)
            assert spec.secretstores is None or len(spec.secretstores) == 0
            print("✓ SecretStore config with missing fields handled correctly")
        finally:
            os.unlink(temp_file)

    except ImportError as e:
        print(
            f"⚠ SecretStore empty config handling test skipped due to missing dependencies: {e}"
        )


def test_secretstore_configuration_classes():
    """Test SecretStore Configuration class functionality"""
    print("\nTesting SecretStore Configuration classes...")

    try:
        from azext_cleanroom.utilities._secretstore_helpers import (
            SecretStoreConfiguration,
        )

        # Test creating configuration with temp file
        with tempfile.NamedTemporaryFile(mode="w", suffix=".yaml", delete=False) as f:
            temp_secretstore_file = f.name

        try:
            # Test loading empty secretstore config
            secret_config = SecretStoreConfiguration.load(
                temp_secretstore_file, create_if_not_existing=True
            )
            assert isinstance(secret_config, SecretStoreSpecification)
            assert (
                secret_config.secretstores is None
                or len(secret_config.secretstores) == 0
            )
            print("✓ SecretStore configuration loading working")

        finally:
            os.unlink(temp_secretstore_file)

    except ImportError as e:
        print(
            f"⚠ SecretStore configuration classes test skipped due to missing dependencies: {e}"
        )


def test_secretstore_environment_variables():
    """Test SecretStore environment variable configuration override"""
    print("\nTesting SecretStore environment variable handling...")

    try:
        from azext_cleanroom.utilities._secretstore_helpers import (
            SecretStoreConfiguration,
        )

        # Create test secretstore config with content
        with tempfile.NamedTemporaryFile(mode="w", suffix=".yaml", delete=False) as f:
            f.write(
                """
secretstores:
  - name: "env-test-secretstore"
    secretStoreType: "Local_File"
    storeProviderUrl: "/tmp/env-secrets"
    configuration: "env-config"
    supportedSecretTypes: ["Secret"]
"""
            )
            env_secretstore_file = f.name

        try:
            # Store original env values
            original_secretstore_env = os.environ.get(
                "CLEANROOM_SECRETSTORE_CONFIG_FILE"
            )

            # Test 1: No environment variable set (should use default path)
            if "CLEANROOM_SECRETSTORE_CONFIG_FILE" in os.environ:
                del os.environ["CLEANROOM_SECRETSTORE_CONFIG_FILE"]

            default_secretstore_path = (
                SecretStoreConfiguration.default_secretstore_config_file()
            )
            assert default_secretstore_path.endswith("secretstores.yaml")
            print("✓ SecretStore default path without environment variable")

            # Test 2: Environment variable set (should use env var path)
            os.environ["CLEANROOM_SECRETSTORE_CONFIG_FILE"] = env_secretstore_file
            env_secretstore_path = (
                SecretStoreConfiguration.default_secretstore_config_file()
            )
            assert env_secretstore_path == env_secretstore_file
            assert env_secretstore_path != default_secretstore_path
            print("✓ SecretStore environment variable override working")

            # Test 3: Load configuration using environment variable
            secret_config_from_env = SecretStoreConfiguration.load(env_secretstore_path)
            assert secret_config_from_env.secretstores is not None
            assert len(secret_config_from_env.secretstores) == 1
            assert secret_config_from_env.secretstores[0].name == "env-test-secretstore"
            print("✓ SecretStore configuration loaded from environment variable path")

        finally:
            # Cleanup temp files
            if os.path.exists(env_secretstore_file):
                os.unlink(env_secretstore_file)

            # Restore original env values
            if original_secretstore_env is not None:
                os.environ["CLEANROOM_SECRETSTORE_CONFIG_FILE"] = (
                    original_secretstore_env
                )
            elif "CLEANROOM_SECRETSTORE_CONFIG_FILE" in os.environ:
                del os.environ["CLEANROOM_SECRETSTORE_CONFIG_FILE"]

    except ImportError as e:
        print(
            f"⚠ SecretStore environment variable test skipped due to missing dependencies: {e}"
        )


def main():
    """Run all SecretStore tests"""
    print("Running SecretStore Tests")
    print("=" * 40)

    try:
        test_secretstore_models()
        test_secretstore_configuration_helpers()
        test_secretstore_empty_config_handling()
        test_secretstore_configuration_classes()
        test_secretstore_environment_variables()

        print("\n" + "=" * 40)
        print("✅ ALL SECRETSTORE TESTS PASSED!")
        print("SecretStore functionality is working correctly.")

    except Exception as e:
        print(f"\n❌ SECRETSTORE TEST FAILED: {e}")
        import traceback

        traceback.print_exc()
        sys.exit(1)


if __name__ == "__main__":
    main()
