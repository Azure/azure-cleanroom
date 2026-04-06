from cleanroom_common.azure_cleanroom_core.models.spark import (
    SparkApplicationDatasetDescriptor,
    SparkApplicationType,
    SparkSQLApplication,
)


def load_spark_sql_application_specification_from_file(
    specification_file: str,
) -> SparkSQLApplication:
    import json
    import os

    import yaml
    from azure.cli.core.util import CLIError

    if not os.path.exists(specification_file):
        raise CLIError(
            f"Spark SQL application specification file '{specification_file}' not found."
        )

    try:
        with open(specification_file, "r") as f:
            if specification_file.endswith((".yaml", ".yml")):
                data = yaml.safe_load(f)
            else:
                data = json.load(f)

        # Convert the loaded data to SparkSQLApplication
        if isinstance(data, dict):
            # Handle direct policy format
            if "query" in data and "inputDataset" in data and "outputDataset" in data:
                return SparkSQLApplication(**data)
            else:
                # Handle wrapped policy format
                if "sparkSQLApplication" in data:
                    return SparkSQLApplication(**data["sparkSQLApplication"])
                else:
                    raise CLIError(
                        "Invalid Spark SQL application specification file format. Expected 'query', 'inputDataset', and 'outputDataset' properties."
                    )
        else:
            raise CLIError(
                "Spark SQL application specification file must contain a JSON object."
            )

    except yaml.YAMLError as e:
        raise CLIError(
            f"Failed to parse YAML Spark-SQL application specification file: {e}"
        )
    except json.JSONDecodeError as e:
        raise CLIError(
            f"Failed to parse JSON Spark-SQL application specification file: {e}"
        )
    except Exception as e:
        raise CLIError(
            f"Failed to load Spark-SQL application specification from file: {e}"
        )


def generate_spark_sql_application_specification_from_fields(
    querysegment_config_file: str,
    input_dataset: list[str],
    output_dataset: str,
) -> SparkSQLApplication:
    import json

    from azure.cli.core.util import CLIError
    from pydantic.json import pydantic_encoder

    from ._querysegment_helpers import QuerySegmentHelper

    if not querysegment_config_file:
        raise CLIError(
            "QuerySegment-Config-File must be specified when generating Spark-SQL application specification from fields."
        )

    if not input_dataset or not output_dataset:
        raise CLIError(
            "Input and output datasets must be specified when generating Spark-SQL application specification from fields."
        )

    from cleanroom_common.azure_cleanroom_core.models.spark import (
        SparkApplicationDatasetDescriptor,
    )

    # Parse and validate input dataset.
    input_map = list[SparkApplicationDatasetDescriptor]()
    for dataset_def in input_dataset:
        input_map.append(_extract_dataset_specification(dataset_def))

    # Parse and validate allowed fields
    output_map = _extract_dataset_specification(output_dataset)

    # Load query segments from the specified query segment configuration file.
    querysegments = QuerySegmentHelper.load(
        querysegment_config_file, create_if_not_existing=False
    )

    return SparkSQLApplication(
        applicationType=SparkApplicationType.Spark_SQL.value,
        query=json.dumps(querysegments, default=pydantic_encoder),
        inputDataset=input_map,
        outputDataset=output_map,
    )


def _extract_dataset_specification(
    dataset_def: str,
) -> SparkApplicationDatasetDescriptor:
    from azure.cli.core.util import CLIError

    try:
        if ":" not in dataset_def:
            raise ValueError(
                "Dataset definition must be in format 'viewName:datasetName'"
            )

        view_name, dataset_name = dataset_def.split(":", 1)
        view_name = view_name.strip()
        dataset_name = dataset_name.strip().lower()

        if not view_name or not dataset_name:
            raise ValueError("View name and dataset name cannot be empty")

        return SparkApplicationDatasetDescriptor(
            view=view_name, specification=dataset_name
        )

    except ValueError as e:
        raise CLIError(f"Invalid input dataset definition '{dataset_def}': {e}")
