# Azure Cleanroom CLI Extension Tests

This directory contains tests for the Azure Cleanroom CLI extension, covering models, configuration management, and error handling functionality.

## Test Dependencies

To run all tests without missing dependency warnings, install the test requirements:

```bash
pip install -r tests/requirements.txt
```

The tests are designed to work with or without Azure CLI dependencies. Tests requiring Azure CLI components will be skipped gracefully if dependencies are not available.

## Test Structure

The tests are organized by functional domain for better maintainability:

### `test_datastore.py`

Tests all DataStore-related functionality:

- **DataStore Models**: DataStoreEntry and DataStoreSpecification classes
- **Configuration Helpers**: File read/write operations for DataStore configurations
- **Empty Config Handling**: Empty files and comment-only configuration files
- **Configuration Classes**: DataStoreConfiguration class functionality
- **Environment Variables**: CLEANROOM_DATASTORE_CONFIG_FILE override behavior
- **Error Handling**: Validation, duplicate detection, and missing entry handling

### `test_secretstore.py`

Tests all SecretStore-related functionality:

- **SecretStore Models**: SecretStoreEntry and SecretStoreSpecification classes
- **Configuration Helpers**: File read/write operations for SecretStore configurations
- **Empty Config Handling**: Empty files and comment-only configuration files
- **Configuration Classes**: SecretStoreConfiguration class functionality
- **Environment Variables**: CLEANROOM_SECRETSTORE_CONFIG_FILE override behavior
- **Error Handling**: Validation, duplicate detection, and missing entry handling

### `test_errorcode.py`

Tests error code infrastructure and exception handling:

- **Error Code Completeness**: Verification that all expected error codes exist
- **Enum Properties**: ErrorCode enum functionality and string values
- **Exception Creation**: CleanroomSpecificationError instantiation and properties
- **Exception Inheritance**: Proper inheritance from Exception base class
- **Naming Consistency**: Error code naming patterns and paired codes

### `run_tests.py`

Test runner that executes all test files and provides a summary report.

## Running Tests

### Prerequisites

For complete test coverage without missing dependency warnings, install the test requirements:

```bash
# From the tests directory
pip install -r requirements.txt
```

Or install individual packages:

```bash
pip install azure-cli-core azure-core azure-cli-telemetry azure-common pycryptodome cryptography
```

**Note**: Tests are designed to work with or without Azure CLI dependencies. Missing dependencies will result in graceful test skipping with warning messages.

### Run All Tests

```bash
python tests/run_tests.py
```

### Run Individual Test Files

```bash
# DataStore tests
python tests/test_datastore.py

# SecretStore tests
python tests/test_secretstore.py

# Error code tests
python tests/test_errorcode.py
```

## Test Coverage

### Core Functionality Tested

- ✅ Model creation and validation
- ✅ Enum handling and string representation
- ✅ Configuration file operations (read/write)
- ✅ Environment variable configuration overrides
- ✅ Error handling and exception throwing
- ✅ Duplicate detection and validation
- ✅ Entry retrieval and existence checking
- ✅ Empty file and edge case handling

### Dependencies

- Some tests require Azure CLI dependencies and will be skipped if not available
- Core model and configuration helper tests run without external dependencies
- Environment variable tests work without Azure CLI dependencies

### Known Limitations

- CLI command tests are not included due to Azure CLI dependency requirements
- Integration tests with actual Azure services are not included
- Tests focus on the core models and configuration logic

## Test Strategy

The tests follow a layered approach:

1. **Unit Tests**: Individual model and helper function testing
2. **Integration Tests**: Configuration file operations and environment handling
3. **Edge Case Tests**: Empty files, missing entries, and error conditions

Each test file is self-contained and can be run independently, making debugging and maintenance easier.

## Test Method Structure

The test files follow consistent structure patterns:

**Domain Test Files** (`test_datastore.py`, `test_secretstore.py`) have 5 test methods each:

1. **`test_*_models()`** - Core model functionality and validation
2. **`test_*_configuration_helpers()`** - Configuration file read/write operations
3. **`test_*_empty_config_handling()`** - Empty files and comment-only configurations
4. **`test_*_configuration_classes()`** - Configuration class functionality
5. **`test_*_environment_variables()`** - Environment variable override behavior

**Infrastructure Test Files** (`test_errorcode.py`) focus on shared functionality:

1. **`test_error_code_completeness()`** - Error code presence validation
2. **`test_error_code_enum_properties()`** - Enum functionality testing
3. **`test_exception_creation()`** - Exception instantiation testing
4. **`test_exception_inheritance()`** - Exception class hierarchy testing
5. **`test_error_code_consistency()`** - Naming pattern validation

All tests pass successfully, confirming the functionality works correctly and is ready for production use.
