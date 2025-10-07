#!/usr/bin/env python3
"""
Test script for error code functionality in the Azure Cleanroom CLI extension.

Tests error code definitions, exception handling, and error code completeness.
This test file covers:

- ErrorCode enum completeness and expected values
- CleanroomSpecificationError exception creation and functionality
- Error code consistency across the codebase
- Exception message handling and error reporting

This test file focuses on the error handling infrastructure that is shared
across both DataStore and SecretStore functionality.
"""

import sys
from pathlib import Path

# Add the extension to Python path - dynamically find the cleanroom directory
current_dir = Path(__file__).parent
cleanroom_dir = current_dir.parent
sys.path.insert(0, str(cleanroom_dir))

from cleanroom_common.azure_cleanroom_core.exceptions.exception import (
    CleanroomSpecificationError,
    ErrorCode,
)


def test_error_code_completeness():
    """Test that all expected error codes are present"""
    print("Testing error code completeness...")

    # Test all expected error codes exist
    expected_codes = [
        "DataStoreNotFound",
        "SecretStoreNotFound",
        "IdentityConfigurationNotFound",
        "UnsupportedDekSecretStore",
        "UnsupportedKekSecretStore",
        "MultipleApplicationEndpointsNotSupported",
        "DatasinkNotFound",
        "DuplicatePort",
        "DataStoreAlreadyExists",
        "SecretStoreAlreadyExists",
    ]

    actual_codes = [e.value for e in ErrorCode]
    for code in expected_codes:
        assert code in actual_codes, f"Missing error code: {code}"

    print("✓ All expected error codes present")
    print(f"✓ Found {len(actual_codes)} total error codes")


def test_error_code_enum_properties():
    """Test ErrorCode enum properties and values"""
    print("\nTesting error code enum properties...")

    # Test that ErrorCode is an enum with string values
    assert hasattr(ErrorCode, "DataStoreNotFound")
    assert hasattr(ErrorCode, "SecretStoreNotFound")
    assert hasattr(ErrorCode, "DataStoreAlreadyExists")
    assert hasattr(ErrorCode, "SecretStoreAlreadyExists")

    # Test specific error code values
    assert ErrorCode.DataStoreNotFound.value == "DataStoreNotFound"
    assert ErrorCode.SecretStoreNotFound.value == "SecretStoreNotFound"
    assert ErrorCode.DataStoreAlreadyExists.value == "DataStoreAlreadyExists"
    assert ErrorCode.SecretStoreAlreadyExists.value == "SecretStoreAlreadyExists"

    print("✓ Error code enum properties working correctly")


def test_exception_creation():
    """Test CleanroomSpecificationError exception creation and functionality"""
    print("\nTesting exception creation...")

    # Test basic exception creation
    exc = CleanroomSpecificationError(ErrorCode.DataStoreNotFound, "Test message")
    assert exc.code == ErrorCode.DataStoreNotFound
    assert exc.message == "Test message"
    print("✓ Basic exception creation working")

    # Test exception with different error codes
    exc2 = CleanroomSpecificationError(
        ErrorCode.SecretStoreAlreadyExists, "Duplicate secret store"
    )
    assert exc2.code == ErrorCode.SecretStoreAlreadyExists
    assert exc2.message == "Duplicate secret store"
    print("✓ Exception creation with different error codes working")

    # Test exception string representation
    exc_str = str(exc)
    assert "DataStoreNotFound" in exc_str
    assert "Test message" in exc_str
    print("✓ Exception string representation working")


def test_exception_inheritance():
    """Test that CleanroomSpecificationError inherits from Exception properly"""
    print("\nTesting exception inheritance...")

    exc = CleanroomSpecificationError(ErrorCode.DataStoreNotFound, "Test")

    # Test that it's an instance of Exception
    assert isinstance(exc, Exception)
    print("✓ Exception inherits from Exception class")

    # Test that it can be raised and caught
    try:
        raise exc
    except CleanroomSpecificationError as caught:
        assert caught.code == ErrorCode.DataStoreNotFound
        assert caught.message == "Test"
        print("✓ Exception can be raised and caught correctly")
    except Exception:
        assert False, "Exception was not caught as CleanroomSpecificationError"


def test_error_code_consistency():
    """Test error code naming consistency and patterns"""
    print("\nTesting error code naming consistency...")

    # Test that error codes follow naming patterns
    datastore_codes = [code for code in ErrorCode if "DataStore" in code.value]
    secretstore_codes = [code for code in ErrorCode if "SecretStore" in code.value]

    assert len(datastore_codes) >= 2, "Should have DataStore-related error codes"
    assert len(secretstore_codes) >= 2, "Should have SecretStore-related error codes"

    # Test that paired error codes exist
    assert ErrorCode.DataStoreNotFound.value == "DataStoreNotFound"
    assert ErrorCode.SecretStoreNotFound.value == "SecretStoreNotFound"
    assert ErrorCode.DataStoreAlreadyExists.value == "DataStoreAlreadyExists"
    assert ErrorCode.SecretStoreAlreadyExists.value == "SecretStoreAlreadyExists"

    print("✓ Error code naming consistency validated")
    print(f"✓ Found {len(datastore_codes)} DataStore error codes")
    print(f"✓ Found {len(secretstore_codes)} SecretStore error codes")


def main():
    """Run all error code tests"""
    print("Running Azure Cleanroom Error Code Tests")
    print("=" * 45)

    try:
        test_error_code_completeness()
        test_error_code_enum_properties()
        test_exception_creation()
        test_exception_inheritance()
        test_error_code_consistency()

        print("\n" + "=" * 45)
        print("✅ ALL ERROR CODE TESTS PASSED!")
        print("Error code functionality is working correctly.")

    except Exception as e:
        print(f"\n❌ ERROR CODE TEST FAILED: {e}")
        import traceback

        traceback.print_exc()
        sys.exit(1)


if __name__ == "__main__":
    main()
