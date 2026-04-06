#!/usr/bin/env python3
"""
Test script for error code functionality in the Azure Cleanroom CLI extension.

Tests error code definitions, exception handling, and error code completeness.
This test file covers:

- ErrorCode enum completeness, properties, and automatic synchronization
- CleanroomSpecificationError exception creation and functionality
- Exception inheritance and error handling behavior
- Collaboration-related error codes (CollaborationNotFound, CollaborationAlreadyExists)
- Identity-related error codes (BackingIdentityNotFound)

The test_error_code_completeness function automatically detects when error codes
are added or removed and provides helpful messages for updating the test.

This test file focuses on the error handling infrastructure that is shared
across DataStore, SecretStore, Collaboration, and Identity functionality.
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
    """Test error code completeness, enum properties, and synchronization"""
    print("Testing error code completeness and enum properties...")

    # Get all actual error codes from the ErrorCode enum
    actual_codes = set(e.value for e in ErrorCode)

    # Test all expected error codes exist
    expected_codes = {
        "DataStoreNotFound",
        "SecretStoreNotFound",
        "IdentityConfigurationNotFound",
        "CollaborationNotFound",
        "UnsupportedDekSecretStore",
        "UnsupportedKekSecretStore",
        "MultipleApplicationEndpointsNotSupported",
        "DatasinkNotFound",
        "DuplicatePort",
        "DataStoreAlreadyExists",
        "SecretStoreAlreadyExists",
        "CollaborationAlreadyExists",
        "BackingIdentityNotFound",
        "CurrentCollaborationNotSet",
    }

    # Check for missing or extra error codes
    missing_from_expected = actual_codes - expected_codes
    extra_in_expected = expected_codes - actual_codes

    # Report any discrepancies with helpful messages
    if missing_from_expected:
        print(
            f"❌ New error codes found that need to be added to expected_codes: {sorted(missing_from_expected)}"
        )
        print(
            "   Please add these codes to the expected_codes set in test_error_code_completeness()"
        )

    if extra_in_expected:
        print(
            f"❌ Error codes in expected_codes that no longer exist: {sorted(extra_in_expected)}"
        )
        print(
            "   Please remove these codes from the expected_codes set in test_error_code_completeness()"
        )

    # Assert that the expected codes match actual codes exactly
    assert (
        not missing_from_expected
    ), f"New error codes need to be added to test: {missing_from_expected}"
    assert (
        not extra_in_expected
    ), f"Outdated error codes need to be removed from test: {extra_in_expected}"
    assert (
        expected_codes == actual_codes
    ), "Expected error codes must match actual error codes exactly"

    print("✓ All expected error codes present and synchronized")
    print(f"✓ Found {len(actual_codes)} total error codes")

    # Test enum properties for key error codes (now that we know they all exist)
    print("✓ Testing enum properties for key error codes...")

    # Test that ErrorCode enum has the expected attributes and values for all expected codes
    for code_name in expected_codes:
        # Test that the attribute exists
        assert hasattr(
            ErrorCode, code_name
        ), f"ErrorCode should have attribute {code_name}"

        # Test that the value matches the name
        enum_value = getattr(ErrorCode, code_name)
        assert (
            enum_value.value == code_name
        ), f"ErrorCode.{code_name}.value should be '{code_name}'"

    print("✓ Error code enum properties validated for all expected codes")


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

    # Test new collaboration error codes
    exc3 = CleanroomSpecificationError(
        ErrorCode.CollaborationNotFound, "Collaboration not found"
    )
    assert exc3.code == ErrorCode.CollaborationNotFound
    assert exc3.message == "Collaboration not found"
    print("✓ Exception creation with CollaborationNotFound working")

    exc4 = CleanroomSpecificationError(
        ErrorCode.BackingIdentityNotFound, "Backing identity not found"
    )
    assert exc4.code == ErrorCode.BackingIdentityNotFound
    assert exc4.message == "Backing identity not found"
    print("✓ Exception creation with BackingIdentityNotFound working")

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


def main():
    """Run all error code tests"""
    print("Running Azure Cleanroom Error Code Tests")
    print("=" * 45)

    try:
        test_error_code_completeness()
        test_exception_creation()
        test_exception_inheritance()

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
