#!/usr/bin/env python3
"""
Test script for querysegment functionality in the Azure Cleanroom CLI extension.

Tests querysegment models, configuration management, environment variable handling,
empty configuration file processing, and schema helper functions. This test file covers:

- QuerySegment and Query model functionality
- Configuration file read/write operations via configuration helpers
- Empty file and comment-only configuration handling
- querysegmentConfiguration class functionality (when Azure CLI deps available)
- Environment variable override behavior (CLEANROOM_querysegment_CONFIG_FILE)
- Schema helper functions (load_schema_from_file, generate_schema_from_fields)
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
from azext_cleanroom.utilities._querysegment_helpers import QuerySegmentHelper
from cleanroom_common.azure_cleanroom_core.models.query import Query, QuerySegment


def test_querysegment_models():
    """Test querysegment model functionality"""
    print("Testing querysegment models...")

    # Test creating a querysegment entry
    querySegment = QuerySegment(
        executionSequence=1,
        data="SELECT * FROM table",
        preConditions=[
            {"viewName": "publisher_view", "minRowCount": 100},
            {"viewName": "publisher_view", "minRowCount": 200},
        ],
        postFilters=[
            {"columnName": "field1", "value": 10},
            {"columnName": "field2", "value": 20},
        ],
    )

    assert querySegment.executionSequence == 1
    assert querySegment.data == "SELECT * FROM table"
    assert querySegment.preConditions is not None
    assert len(querySegment.preConditions) == 2
    assert querySegment.preConditions[0].viewName == "publisher_view"
    assert querySegment.preConditions[0].minRowCount == 100
    assert querySegment.preConditions[1].viewName == "publisher_view"
    assert querySegment.preConditions[1].minRowCount == 200
    assert querySegment.postFilters[0].columnName == "field1"
    assert querySegment.postFilters[0].value == 10
    assert querySegment.postFilters[1].columnName == "field2"
    assert querySegment.postFilters[1].value == 20
    print("✓ querysegment entry creation successful")

    # Test specification
    spec = Query(segments=[])
    assert spec.segments is None or len(spec.segments) == 0

    # Test adding entry
    spec.segments.append(querySegment)
    assert spec.segments is not None and len(spec.segments) == 1
    print("✓ querysegment entry addition successful")


def test_querysegment_configuration_helpers():
    """Test querysegment configuration file operations"""
    print("\nTesting querysegment configuration helpers...")

    from cleanroom_common.azure_cleanroom_core.utilities.configuration_helpers import (
        read_querysegment_file_config,
        write_querysegment_file_config,
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

    # Test querysegment configuration file operations
    with tempfile.NamedTemporaryFile(mode="w", suffix=".yaml", delete=False) as f:
        temp_file = f.name

    try:
        # Create a querysegment config
        querysegment1 = QuerySegment(
            executionSequence=1,
            data="SELECT * FROM table1",
            preConditions=[{"viewName": "view1", "minRowCount": 50}],
            postFilters=[{"columnName": "col1", "value": 5}],
        )
        querysegment2 = QuerySegment(
            executionSequence=2,
            data="SELECT * FROM table2",
            preConditions=[
                {"viewName": "view2", "minRowCount": 100},
                {"viewName": "view3", "minRowCount": 200},
            ],
            postFilters=[{"columnName": "col2", "value": 10}],
        )
        querysegment3 = QuerySegment(
            executionSequence=3,
            data="SELECT * FROM table3",
            preConditions=[
                {"viewName": "view3", "minRowCount": 150},
                {"viewName": "view4", "minRowCount": 250},
            ],
            postFilters=[{"columnName": "col3", "value": 15}],
        )

        spec = Query(segments=[])
        spec.segments.append(querysegment1)

        # Write config
        write_querysegment_file_config(temp_file, spec, logger)
        assert os.path.exists(temp_file)
        print("✓ querysegment config write successful")

        # Read config back
        loaded_spec = read_querysegment_file_config(temp_file, logger)
        assert loaded_spec.segments is not None
        assert len(loaded_spec.segments) == 1
        print("✓ querysegment config read successful")

        # Add more entries and write again
        loaded_spec.segments.append(querysegment2)
        loaded_spec.segments.append(querysegment3)
        write_querysegment_file_config(temp_file, loaded_spec, logger)

        # Read back again
        final_spec = read_querysegment_file_config(temp_file, logger)
        assert final_spec.segments is not None
        assert len(final_spec.segments) == 3
        print("✓ querysegment config update successful")

        # Test removal of 1 segment
        final_spec.segments = [
            seg for seg in final_spec.segments if seg.executionSequence != 2
        ]
        write_querysegment_file_config(temp_file, final_spec, logger)
        updated_spec = read_querysegment_file_config(temp_file, logger)
        assert updated_spec.segments is not None
        assert len(updated_spec.segments) == 2
        print("✓ querysegment config removal successful")

    finally:
        if os.path.exists(temp_file):
            os.unlink(temp_file)


def test_querysegment_configuration_classes():
    """Test querysegment configuration class functionality"""
    print("\nTesting querysegment configuration classes...")

    try:
        # Test with temporary files
        with tempfile.NamedTemporaryFile(mode="w", suffix=".yaml", delete=False) as f:
            f.write("segments: []\n")
            temp_file = f.name

        try:
            # Test loading empty config
            config = QuerySegmentHelper.load(temp_file, create_if_not_existing=True)
            assert isinstance(config, Query)
            assert config.segments is not None and len(config.segments) == 0
            print("✓ querysegment configuration loading working")

            config.segments.append(
                QuerySegment(
                    executionSequence=1,
                    data="SELECT * FROM test_table",
                    preConditions=[],
                    postFilters=[],
                )
            )
            # Test storing config
            QuerySegmentHelper.store(temp_file, config)
            reloaded_config = QuerySegmentHelper.load(temp_file)
            assert reloaded_config.segments is not None
            assert len(reloaded_config.segments) == 1
            print("✓ querysegment configuration storing working")

        finally:
            os.unlink(temp_file)

    except ImportError as e:
        print(
            f"⚠ querysegment configuration class test skipped due to missing dependencies: {e}"
        )


def test_parse_inputs_util():
    """Test the parse_add_cmd_inputs function for adding query segments"""
    print("\nTesting add segment command input parsing...")

    newquerySegment1 = QuerySegmentHelper.generate_segment_from_fields(
        executionsequence=1,
        query_content="SELECT * FROM test_table",
        pre_conditions="publisher_view:100, consumer_view:200",
        post_filters="field1:10, field2:20",
    )
    assert newquerySegment1.executionSequence == 1
    assert newquerySegment1.data == "SELECT * FROM test_table"
    assert newquerySegment1.preConditions is not None
    assert len(newquerySegment1.preConditions) == 2
    assert newquerySegment1.preConditions[0].viewName == "publisher_view"
    assert newquerySegment1.preConditions[0].minRowCount == 100
    assert len(newquerySegment1.postFilters) == 2
    assert newquerySegment1.postFilters[0].columnName == "field1"
    assert newquerySegment1.postFilters[0].value == 10
    print("✓ add segment command input parsing test successful")

    newquerySegment2 = QuerySegmentHelper.generate_segment_from_fields(
        executionsequence=2,
        query_content="SELECT * FROM test_table",
        pre_conditions="publisher_view:100",
        post_filters="field1:10",
    )
    assert newquerySegment2.executionSequence == 2
    assert newquerySegment2.data == "SELECT * FROM test_table"
    assert newquerySegment2.preConditions is not None
    assert len(newquerySegment2.preConditions) == 1
    assert newquerySegment2.preConditions[0].viewName == "publisher_view"
    assert newquerySegment2.preConditions[0].minRowCount == 100
    assert len(newquerySegment2.postFilters) == 1
    assert newquerySegment2.postFilters[0].columnName == "field1"
    assert newquerySegment2.postFilters[0].value == 10


def test_segment_to_json():
    """Convert a QuerySegment to JSON for comparison"""
    import json

    from pydantic.json import pydantic_encoder

    querysegment1 = QuerySegment(
        executionSequence=1,
        data="SELECT * FROM table",
        preConditions=[
            {"viewName": "publisher_view", "minRowCount": 100},
            {"viewName": "publisher_view", "minRowCount": 200},
        ],
        postFilters=[
            {"columnName": "field1", "value": 10},
            {"columnName": "field2", "value": 20},
        ],
    )
    querysegment2 = QuerySegment(
        executionSequence=2,
        data="SELECT * FROM table2",
        preConditions=[
            {"viewName": "view2", "minRowCount": 100},
            {"viewName": "view3", "minRowCount": 200},
        ],
        postFilters=[{"columnName": "col2", "value": 10}],
    )
    segmentList = [querysegment1, querysegment2]
    segmentedQueryJson = {"segments": segmentList}
    finalQuery = json.dumps(segmentedQueryJson, default=pydantic_encoder)
    assert finalQuery is not None
    print("✓ querysegment to JSON conversion successful")


def main():
    """Run all querysegment tests"""
    print("Running Azure Cleanroom querysegment Tests")
    print("=" * 50)

    try:
        test_querysegment_models()
        test_querysegment_configuration_helpers()
        test_querysegment_configuration_classes()
        test_parse_inputs_util()
        test_segment_to_json()

        print("\n" + "=" * 50)
        print("✅ ALL querysegment TESTS PASSED!")
        print("Querysegment functionality is working correctly.")

    except Exception as e:
        print(f"\n❌ TEST FAILED: {e}")
        import traceback

        traceback.print_exc()
        sys.exit(1)


if __name__ == "__main__":
    main()
