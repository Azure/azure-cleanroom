import logging
import os
from datetime import datetime, timedelta
from functools import reduce

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
from src.config.configuration import DatasetInfo


def load_dataset(
    spark: SparkSession, dataset: DatasetInfo, startdate: str, enddate: str
):
    tracer = trace.get_tracer("load_dataset")
    logger = logging.getLogger("load_dataset")
    with tracer.start_as_current_span(f"load_dataset_folder-{dataset.name}") as span:
        consolidated_df = None

        # If startdate and enddate are provided, load data files for each date in the range
        # Increment startdate 1 day at a time until it reaches enddate
        # For each date call load_dataset_folder to load all files for that date
        # Date is assumed to be specified as YYYY-MM-DD
        # If any date folder is missing, that will be logged as a warning and the loop continues
        if startdate != "" and enddate != "":
            logger.info(
                f"Loading dataset {dataset.name} for date range {startdate} to {enddate}"
            )
            all_dfs = []
            start = datetime.strptime(startdate, "%Y-%m-%d")
            end = datetime.strptime(enddate, "%Y-%m-%d")
            current = start
            while current <= end:
                try:
                    date_str = current.strftime("%Y-%m-%d")
                    datapath = os.path.join(dataset.path, date_str)
                    df = load_dataset_folder(spark, dataset, datapath)
                    all_dfs.append(df)
                    current += timedelta(days=1)
                except Exception as e:
                    if "Path does not exist" in str(e):
                        logger.warning(
                            f"Path not found for date {date_str}: {datapath}, skipping."
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

        consolidated_df.cache()
        consolidated_df.createOrReplaceTempView(dataset.view_name)


def load_dataset_folder(spark: SparkSession, dataset: DatasetInfo, datapath: str):
    tracer = trace.get_tracer("load_dataset_folder")
    logger = logging.getLogger("load_dataset_folder")
    dataset_format = str(dataset.format.value)
    with tracer.start_as_current_span(f"load_dataset_folder-{datapath}") as span:
        try:
            if dataset_format == "csv":
                schema = parse_json_schema(dataset.schema_)
                df = spark.read.csv(
                    datapath,
                    pathGlobFilter="*.csv",
                    schema=schema,
                    header=False,
                    recursiveFileLookup=True,
                )
            elif dataset_format == "json":
                schema = parse_json_schema(dataset.schema_)
                df = spark.read.json(
                    datapath,
                    pathGlobFilter="*.json",
                    schema=schema,
                    recursiveFileLookup=True,
                )
            elif dataset_format == "parquet":
                df = spark.read.parquet(
                    datapath, pathGlobFilter="*.parquet", recursiveFileLookup=True
                )
            else:
                raise ValueError(f"Unsupported dataset format: {dataset_format}")
            return df
        except Exception as e:
            span.record_exception(e)
            logger.error(
                f"Failed to load dataset {dataset.name} using the path {datapath}: {e}"
            )
            raise e


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


def write_dataset(
    job_name: str, spark: SparkSession, df: DataFrame, dataset: DatasetInfo
):
    # Disabling both these Spark configurations to avoid creation of the _SUCCESS and .crc files.
    spark.conf.set("mapreduce.fileoutputcommitter.marksuccessfuljobs", "false")
    spark.conf.set("dfs.client.write.checksum", "false")

    # Avoids creating temporary directories and avoids permission changes. Better for cloud storage
    # and FUSE based file systems.
    spark.conf.set("mapreduce.fileoutputcommitter.algorithm.version", "2")

    tracer = trace.get_tracer("write_dataset")
    logger = logging.getLogger("write_dataset")
    with tracer.start_as_current_span(f"write_dataset-{dataset.name}") as span:
        try:
            dataset_schema = parse_json_schema(dataset.schema_)
            if not is_schema_compatible(df.schema, dataset_schema):
                raise ValueError(
                    "DataFrame schema does not match expected dataset schema."
                )
            dataset_format = str(dataset.format.value)
            os.makedirs(f"{dataset.path}/{job_name}", exist_ok=True)
            df.write.option("header", True).format(dataset_format).mode(
                "overwrite"
            ).save(f"{dataset.path}/{job_name}")
            logger.info(
                f"Dataset {dataset.name} written successfully to {dataset.path}"
            )
        except Exception as e:
            span.record_exception(e)
            logger.error(f"Failed to write dataset {dataset.name}: {e}")
            raise e
