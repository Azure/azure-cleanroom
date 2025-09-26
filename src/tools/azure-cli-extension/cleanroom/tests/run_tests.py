#!/usr/bin/env python3
"""
Test runner for the Azure Cleanroom CLI Extension.

Executes all test files in the tests directory and provides a comprehensive
summary report. The test suite is organized by functional domains:

- test_datastore.py: DataStore functionality tests
- test_secretstore.py: SecretStore functionality tests
- test_errorcode.py: Error code and exception handling tests

Each test file can also be run independently for focused testing.
"""

import os
import subprocess
import sys
from pathlib import Path


def run_test_file(test_file, python_cmd):
    """Run a specific test file"""
    print(f"🧪 Running {test_file}...")
    try:
        result = subprocess.run(
            [python_cmd, test_file],
            capture_output=True,
            text=True,
            timeout=60,
            cwd=Path(__file__).parent,
        )
        if result.returncode == 0:
            print(f"✅ {test_file} PASSED")
            print(
                "   Output:",
                (
                    result.stdout.strip().split("\n")[-1]
                    if result.stdout.strip()
                    else "No output"
                ),
            )
            return True
        else:
            print(f"❌ {test_file} FAILED")
            print(f"   Error: {result.stderr}")
            return False
    except subprocess.TimeoutExpired:
        print(f"⏰ {test_file} TIMED OUT")
        return False
    except Exception as e:
        print(f"❌ {test_file} ERROR: {e}")
        return False


def main():
    """Run all tests in the tests directory"""
    print("Azure Cleanroom CLI Extension - Test Runner")
    print("=" * 50)

    # Find Python executable
    venv_python = (
        Path(__file__).parent.parent.parent.parent.parent.parent
        / ".venv"
        / "bin"
        / "python"
    )
    if venv_python.exists():
        python_cmd = str(venv_python)
    else:
        python_cmd = "python3"

    print(f"Using Python: {python_cmd}")
    print()

    # Find all test files
    test_files = ["test_datastore.py", "test_secretstore.py", "test_errorcode.py"]

    passed = 0
    total = len(test_files)

    for test_file in test_files:
        if run_test_file(test_file, python_cmd):
            passed += 1
        print()

    print("=" * 50)
    print(f"SUMMARY: {passed}/{total} tests passed")

    if passed == total:
        print("🎉 ALL TESTS PASSED!")
        return 0
    else:
        print("❌ Some tests failed")
        return 1


if __name__ == "__main__":
    sys.exit(main())
