#!/usr/bin/env python3
"""
Test script for DataStore functionality in the Azure Cleanroom CLI extension.

Tests DataStore models, configuration management, environment variable handling,
and empty configuration file processing. This test file covers:

- DataStoreEntry and DataStoreSpecification model functionality
- Configuration file read/write operations via configuration helpers
- Empty file and comment-only configuration handling
- DataStoreConfiguration class functionality (when Azure CLI deps available)
- Environment variable override behavior (CLEANROOM_DATASTORE_CONFIG_FILE)
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
from cleanroom_common.azure_cleanroom_core.models.datastore import (
    DataStoreEntry,
    DataStoreSpecification,
)


def test_datastore_models():
    """Test DataStore model functionality"""
    print("Testing DataStore models...")

    # Test creating a datastore entry
    datastore = DataStoreEntry(
        name="test-datastore",
        secretstore_config="/path/to/secretstore.yaml",
        secretstore_name="test-secretstore",
        encryptionMode=DataStoreEntry.EncryptionMode.SSE_CPK,
        storeType=DataStoreEntry.StoreType.Azure_BlobStorage,
        storeProviderUrl="https://test.blob.core.windows.net/",
        storeName="test-container",
    )
    assert datastore.name == "test-datastore"
    assert datastore.encryptionMode == "CPK"
    assert datastore.storeType == "Azure_BlobStorage"
    print("✓ DataStoreEntry creation successful")

    # Test specification
    spec = DataStoreSpecification()
    assert spec.datastores is None or len(spec.datastores) == 0

    # Test adding entry
    spec.add_datastore_entry(datastore)
    assert spec.datastores is not None and len(spec.datastores) == 1
    print("✓ DataStore entry addition successful")

    # Test duplicate detection
    try:
        spec.add_datastore_entry(datastore)
        assert False, "Should have raised exception for duplicate"
    except CleanroomSpecificationError as e:
        assert e.code == ErrorCode.DataStoreAlreadyExists
        print("✓ Duplicate detection working")

    # Test retrieval
    exists, index, entry = spec.check_datastore_entry("test-datastore")
    assert (
        exists and index == 0 and entry is not None and entry.name == "test-datastore"
    )
    print("✓ DataStore retrieval successful")

    # Test missing entry
    try:
        spec.get_datastore_entry("nonexistent")
        assert False, "Should have raised exception for missing entry"
    except CleanroomSpecificationError as e:
        assert e.code == ErrorCode.DataStoreNotFound
        print("✓ Missing entry detection working")


def test_datastore_configuration_helpers():
    """Test DataStore configuration file operations"""
    print("\nTesting DataStore configuration helpers...")

    from cleanroom_common.azure_cleanroom_core.utilities.configuration_helpers import (
        read_datastore_config,
        write_datastore_config,
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

    # Test DataStore configuration file operations
    with tempfile.NamedTemporaryFile(mode="w", suffix=".yaml", delete=False) as f:
        temp_file = f.name

    try:
        # Create a datastore config
        datastore = DataStoreEntry(
            name="test-ds",
            secretstore_config="/path/to/secrets.yaml",
            secretstore_name="test-secrets",
            encryptionMode=DataStoreEntry.EncryptionMode.SSE_CPK,
            storeType=DataStoreEntry.StoreType.Azure_BlobStorage,
            storeProviderUrl="https://test.blob.core.windows.net/",
            storeName="test-container",
        )

        spec = DataStoreSpecification()
        spec.add_datastore_entry(datastore)

        # Write config
        write_datastore_config(temp_file, spec, logger)
        assert os.path.exists(temp_file)
        print("✓ DataStore config write successful")

        # Read config back
        loaded_spec = read_datastore_config(temp_file, logger)
        assert loaded_spec.datastores is not None
        assert len(loaded_spec.datastores) == 1
        assert loaded_spec.datastores[0].name == "test-ds"
        print("✓ DataStore config read successful")

    finally:
        if os.path.exists(temp_file):
            os.unlink(temp_file)


def test_datastore_empty_config_handling():
    """Test handling of empty DataStore config files"""
    print("\nTesting empty DataStore config handling...")

    from cleanroom_common.azure_cleanroom_core.utilities.configuration_helpers import (
        read_datastore_config,
    )

    class MockLogger:
        def info(self, msg):
            pass

    logger = MockLogger()

    # Test empty datastore config
    with tempfile.NamedTemporaryFile(mode="w", suffix=".yaml", delete=False) as f:
        f.write("")  # Empty file
        temp_file = f.name

    try:
        spec = read_datastore_config(temp_file, logger)
        assert spec.datastores is None or len(spec.datastores) == 0
        print("✓ Empty datastore config handled correctly")
    finally:
        os.unlink(temp_file)

    # Test missing fields in config
    with tempfile.NamedTemporaryFile(mode="w", suffix=".yaml", delete=False) as f:
        f.write("# Comment only")
        temp_file = f.name

    try:
        spec = read_datastore_config(temp_file, logger)
        assert spec.datastores is None or len(spec.datastores) == 0
        print("✓ DataStore config with missing fields handled correctly")
    finally:
        os.unlink(temp_file)


def test_datastore_configuration_classes():
    """Test DataStore configuration class functionality"""
    print("\nTesting DataStore configuration classes...")

    try:
        from azext_cleanroom.utilities._datastore_helpers import DataStoreConfiguration

        # Test default file paths
        default_datastore_path = DataStoreConfiguration.default_datastore_config_file()
        assert default_datastore_path.endswith("datastores.yaml")
        print("✓ DataStore default file path working")

        # Test with temporary files
        with tempfile.NamedTemporaryFile(mode="w", suffix=".yaml", delete=False) as f:
            f.write("datastores: []\n")
            temp_file = f.name

        try:
            # Test loading empty config
            config = DataStoreConfiguration.load(temp_file, create_if_not_existing=True)
            assert isinstance(config, DataStoreSpecification)
            assert config.datastores is None or len(config.datastores) == 0
            print("✓ DataStore configuration loading working")

        finally:
            os.unlink(temp_file)

    except ImportError as e:
        print(
            f"⚠ DataStore configuration class test skipped due to missing dependencies: {e}"
        )


def test_datastore_environment_variables():
    """Test DataStore environment variable override functionality"""
    print("\nTesting DataStore environment variable handling...")

    try:
        from azext_cleanroom.utilities._datastore_helpers import DataStoreConfiguration

        # Create test datastore config with content
        with tempfile.NamedTemporaryFile(mode="w", suffix=".yaml", delete=False) as f:
            f.write(
                """
datastores:
  - name: "env-test-datastore"
    secretstore_config: "/path/to/secrets.yaml"
    secretstore_name: "env-secrets"
    encryptionMode: "CPK"
    storeType: "Azure_BlobStorage"
    storeProviderUrl: "https://envtest.blob.core.windows.net/"
    storeName: "env-container"
"""
            )
            env_datastore_file = f.name

        try:
            # Store original env value
            original_datastore_env = os.environ.get("CLEANROOM_DATASTORE_CONFIG_FILE")

            # Test 1: No environment variable set (should use default path)
            if "CLEANROOM_DATASTORE_CONFIG_FILE" in os.environ:
                del os.environ["CLEANROOM_DATASTORE_CONFIG_FILE"]

            default_datastore_path = (
                DataStoreConfiguration.default_datastore_config_file()
            )
            assert default_datastore_path.endswith("datastores.yaml")
            print("✓ DataStore default path without environment variable")

            # Test 2: Environment variable set (should use env var path)
            os.environ["CLEANROOM_DATASTORE_CONFIG_FILE"] = env_datastore_file
            env_datastore_path = DataStoreConfiguration.default_datastore_config_file()
            assert env_datastore_path == env_datastore_file
            assert env_datastore_path != default_datastore_path
            print("✓ DataStore environment variable override working")

            # Test 3: Load configuration using environment variable
            config_from_env = DataStoreConfiguration.load(env_datastore_path)
            assert config_from_env.datastores is not None
            assert len(config_from_env.datastores) == 1
            assert config_from_env.datastores[0].name == "env-test-datastore"
            print("✓ DataStore configuration loaded from environment variable path")

        finally:
            # Cleanup temp files
            if os.path.exists(env_datastore_file):
                os.unlink(env_datastore_file)

            # Restore original env value
            if original_datastore_env is not None:
                os.environ["CLEANROOM_DATASTORE_CONFIG_FILE"] = original_datastore_env
            elif "CLEANROOM_DATASTORE_CONFIG_FILE" in os.environ:
                del os.environ["CLEANROOM_DATASTORE_CONFIG_FILE"]

    except ImportError as e:
        print(
            f"⚠ DataStore environment variable test skipped due to missing dependencies: {e}"
        )


def main():
    """Run all DataStore tests"""
    print("Running Azure Cleanroom DataStore Tests")
    print("=" * 50)

    try:
        test_datastore_models()
        test_datastore_configuration_helpers()
        test_datastore_empty_config_handling()
        test_datastore_configuration_classes()
        test_datastore_environment_variables()

        print("\n" + "=" * 50)
        print("✅ ALL DATASTORE TESTS PASSED!")
        print("DataStore functionality is working correctly.")

    except Exception as e:
        print(f"\n❌ TEST FAILED: {e}")
        import traceback

        traceback.print_exc()
        sys.exit(1)


if __name__ == "__main__":
    main()
