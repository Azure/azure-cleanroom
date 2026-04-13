import logging
import os
from datetime import datetime, timedelta
from functools import reduce

from analytics_contracts.audit import AuditRecordFactory, IAuditRecordLogger
from analytics_contracts.events import IEventEmitter, OperationalEventFactory
from opentelemetry import trace
from pyspark.sql import DataFrame, SparkSession
from pyspark.sql.types import (
    ArrayType,
    BooleanType,
    DateType,
    DoubleType,
    IntegerType,
    LongType,
    StringType,
    StructField,
    StructType,
    TimestampType,
)

from ..config.configuration import DatasetInfo


async def load_dataset_async(
    spark: SparkSession,
    dataset: DatasetInfo,
    start_date: str,
    end_date: str,
    job_id: str,
    event_emitter: IEventEmitter,
    audit_logger: IAuditRecordLogger,
) -> int:
    tracer = trace.get_tracer("load_dataset")
    logger = logging.getLogger("load_dataset")
    await event_emitter.log_operational_event(
        OperationalEventFactory.DatasetLoadStarted(
            dataset_name=dataset.name,
            dataset_path=dataset.path,
            start_date=start_date if start_date != "" else "NA",
            end_date=end_date if end_date != "" else "NA",
        )
    )

    with tracer.start_as_current_span(f"load_dataset-{dataset.name}") as span:
        try:
            consolidated_df = None

            # If start_date and end_date are provided, load data files for each date in the range
            # Increment start_date 1 day at a time until it reaches end_date
            # For each date call load_dataset_folder to load all files for that date
            # Date is assumed to be specified as YYYY-MM-DD
            # If any date folder is missing, that will be logged as a warning and the loop continues
            if start_date != "" and end_date != "":
                logger.info(
                    f"Loading dataset {dataset.name} for date range {start_date} to {end_date}"
                )
                all_dfs = []
                start = datetime.strptime(start_date, "%Y-%m-%d")
                end = datetime.strptime(end_date, "%Y-%m-%d")
                current = start
                while current <= end:
                    try:
                        date_str = current.strftime("%Y-%m-%d")
                        data_path = os.path.join(dataset.path, date_str)
                        df = load_dataset_folder(spark, dataset, data_path)
                        all_dfs.append(df)
                        current += timedelta(days=1)
                    except Exception as e:
                        if "Path does not exist" in str(e):
                            logger.warning(
                                f"Path not found for date {date_str}: {data_path}, skipping."
                            )
                            current += timedelta(days=1)
                        else:
                            raise e

                # Combine all DataFrames with the same schema (column names and types), even if the order of columns differs.
                if not all_dfs:
                    # This will enable scenarios to execute queries where one collaborator doesn't
                    # share the data partitioned as dates, but other collaborator does.
                    logger.warning(
                        f"No data found for dataset {dataset.name} in the date range. Will fallback to read all data files."
                    )
                else:
                    logger.info(
                        f"Consolidating {len(all_dfs)} dataframes for dataset {dataset.name} in the date range."
                    )
                    consolidated_df = reduce(
                        lambda df_a, df_b: df_a.unionByName(df_b), all_dfs
                    )

            # Load all data files in the dataset folder and all sub-directories. The default behavior
            if not consolidated_df:
                consolidated_df = load_dataset_folder(spark, dataset, dataset.path)

            logger.info(
                f"Filtering dataset {dataset.name} to allowed fields: {dataset.allowedFields}."
                + f" Original columns: {consolidated_df.columns}."
            )
            consolidated_df = consolidated_df.select(
                [
                    field
                    for field in dataset.allowedFields
                    if field in consolidated_df.columns
                ]
            )

            consolidated_df.cache()
            consolidated_df.createOrReplaceTempView(dataset.view_name)
            row_count = consolidated_df.count()
            logger.info(
                f"Loaded {row_count} rows for dataset {dataset.name} from {dataset.path}."
            )
            await event_emitter.log_operational_event(
                OperationalEventFactory.DatasetLoadCompleted(
                    dataset_name=dataset.name,
                    row_count=str(row_count),
                    dataset_path=dataset.path,
                )
            )

            await audit_logger.log_audit_record(
                AuditRecordFactory.DatasetLoadCompleted(
                    source="dataset_loader",
                    dataset_name=dataset.name,
                    job_id=job_id,
                )
            )

            return row_count
        except Exception as e:
            span.record_exception(e)
            logger.error(
                f"Failed to load dataset {dataset.name} from {dataset.path} with error: {e}"
            )
            await event_emitter.log_operational_event(
                OperationalEventFactory.DatasetLoadFailed(
                    dataset_name=dataset.name,
                    dataset_path=dataset.path,
                    error=str(e),
                )
            )
            await audit_logger.log_audit_record(
                AuditRecordFactory.DatasetLoadFailed(
                    source="dataset_loader",
                    dataset_name=dataset.name,
                    job_id=job_id,
                )
            )
            raise e


def load_dataset_folder(spark: SparkSession, dataset: DatasetInfo, data_path: str):
    tracer = trace.get_tracer("load_dataset_folder")
    logger = logging.getLogger("load_dataset_folder")
    dataset_format = str(dataset.format.value)
    with tracer.start_as_current_span(f"load_dataset_folder-{data_path}") as span:
        try:
            if dataset_format == "csv":
                schema = parse_json_schema(dataset.schema_)
                df = spark.read.csv(
                    data_path,
                    pathGlobFilter="*.csv",
                    schema=schema,
                    header=False,
                    recursiveFileLookup=True,
                )
            elif dataset_format == "json":
                schema = parse_json_schema(dataset.schema_)
                df = spark.read.json(
                    data_path,
                    pathGlobFilter="*.json",
                    schema=schema,
                    recursiveFileLookup=True,
                )
            elif dataset_format == "parquet":
                schema = parse_json_schema(dataset.schema_)
                df = spark.read.schema(schema).parquet(
                    data_path, pathGlobFilter="*.parquet", recursiveFileLookup=True
                )
            else:
                raise ValueError(f"Unsupported dataset format: {dataset_format}")
            return df
        except Exception as e:
            span.record_exception(e)
            logger.error(
                f"Failed to load dataset {dataset.name} using the path {data_path}: {e}"
            )
            raise


def parse_json_schema(schema):
    def parse_field(prop_schema):
        type_ = prop_schema.get("type")

        if type_ == "string":
            return StringType()
        elif type_ == "integer":
            return IntegerType()
        elif type_ == "long":
            return LongType()
        elif type_ == "timestamp":
            return TimestampType()
        elif type_ == "number":
            return DoubleType()
        elif type_ == "boolean":
            return BooleanType()
        elif type_ == "date":
            return DateType()
        elif type_ == "array":
            item_schema = prop_schema.get("items", {})
            return ArrayType(parse_field(item_schema))
        elif type_ == "object":
            return parse_json_schema(prop_schema)
        else:
            return StringType()  # fallback

    fields = []
    for prop, prop_schema in schema.items():
        spark_type = parse_field(prop_schema)
        nullable = prop not in schema.get("required", [])
        fields.append(StructField(prop, spark_type, nullable))

    return StructType(fields)


def is_schema_compatible(df_schema, expected_schema):
    logger = logging.getLogger("is_schema_compatible")
    expected_fields = {f.name: f.dataType for f in expected_schema.fields}
    df_fields = {f.name: f.dataType for f in df_schema.fields}

    for df_field_name, df_field_type in df_fields.items():
        if df_field_name not in expected_fields:
            logger.error(f"Missing field: {df_field_name}")
            return False
        if expected_fields[df_field_name] != df_field_type:
            logger.error(
                f"Type mismatch for field '{df_field_name}': expected {expected_fields[df_field_name]}, got {df_field_type}"
            )
            return False
    return True


async def write_dataset_async(
    job_id: str,
    spark: SparkSession,
    df: DataFrame,
    dataset: DatasetInfo,
    event_emitter: IEventEmitter,
    audit_logger: IAuditRecordLogger,
) -> int:
    await event_emitter.log_operational_event(
        OperationalEventFactory.DatasetWriteStarted(
            dataset_name=dataset.name,
            output_path=dataset.path,
        )
    )

    # Disabling both these Spark configurations to avoid creation of the _SUCCESS and .crc files.
    spark.conf.set("mapreduce.fileoutputcommitter.marksuccessfuljobs", "false")
    spark.conf.set("dfs.client.write.checksum", "false")

    # Avoids creating temporary directories and avoids permission changes. Better for cloud storage
    # and FUSE based file systems.
    spark.conf.set("mapreduce.fileoutputcommitter.algorithm.version", "2")
    tracer = trace.get_tracer("write_dataset")
    logger = logging.getLogger("write_dataset")
    dataset_format = str(dataset.format.value)
    current_date = datetime.now().strftime("%Y-%m-%d")
    output_path = f"{dataset.path}/{current_date}/{job_id}"
    with tracer.start_as_current_span(f"write_dataset-{dataset.name}") as span:
        try:
            logger.info(
                f"Filtering datasink {dataset.name} to allowed fields: {dataset.allowedFields}."
                + f" Original columns: {df.columns}."
            )
            df = df.select(
                [field for field in dataset.allowedFields if field in df.columns]
            )

            dataset_schema = parse_json_schema(dataset.schema_)
            if not is_schema_compatible(df.schema, dataset_schema):
                raise ValueError(
                    "DataFrame schema does not match expected dataset schema."
                )

            row_count = df.count()
            os.makedirs(output_path, exist_ok=True)
            df.write.option("header", True).format(dataset_format).mode(
                "overwrite"
            ).save(output_path)
            row_count = df.count()
            logger.info(
                f"Dataset {dataset.name} with row count {row_count} written successfully to {output_path}"
            )

            await event_emitter.log_operational_event(
                OperationalEventFactory.DatasetWriteCompleted(
                    dataset_name=dataset.name,
                    output_path=output_path,
                    row_count=str(row_count),
                )
            )

            await audit_logger.log_audit_record(
                AuditRecordFactory.DatasetWriteCompleted(
                    source="dataset_loader",
                    dataset_name=dataset.name,
                    destination=output_path,
                    row_count=str(row_count),
                    job_id=job_id,
                )
            )

            return row_count
        except Exception as e:
            span.record_exception(e)
            logger.error(
                f"Failed to write dataset {dataset.name} to {output_path} with error: {e}"
            )
            await event_emitter.log_operational_event(
                OperationalEventFactory.DatasetWriteFailed(
                    dataset_name=dataset.name,
                    output_path=output_path,
                    error=str(e),
                )
            )
            await audit_logger.log_audit_record(
                AuditRecordFactory.DatasetWriteFailed(
                    source="dataset_loader",
                    dataset_name=dataset.name,
                    destination=output_path,
                    job_id=job_id,
                    reason=str(e),
                )
            )
            raise
