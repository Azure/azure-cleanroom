import asyncio
import base64
import concurrent.futures
import json
import logging
import os
import time
from collections import defaultdict
from typing import List

from analytics_contracts.audit import AuditRecordFactory, IAuditRecordLogger
from analytics_contracts.events import IEventEmitter, OperationalEventFactory
from analytics_contracts.statistics import IStatisticsRecorder, StatisticsEventFactory
from analytics_workload.adapters.adapters_factory.adapter_factory import AdapterFactory
from analytics_workload.config.configuration import (
    ApplicationConfiguration,
    QueryConfiguration,
)
from analytics_workload.config.query import (
    PostFilter,
    PreCondition,
    Query,
    QuerySegment,
)
from analytics_workload.servicelocators import ServiceLocator
from analytics_workload.utilities import dataset_loader
from cleanroom_internal.utilities import otel_utilities
from cleanroom_internal.utilities.otel_setup_utilities import TelemetryConfig
from dependency_injector.wiring import Provide, inject
from opentelemetry import context, trace
from pyspark.sql import DataFrame, SparkSession, functions

config: QueryConfiguration
job_id: str | None = None


def pre_segment_enforcement(spark: SparkSession, enforcements: List[PreCondition]):
    logger = logging.getLogger("pre_segment_enforcement")
    logger.info(f"Applying pre-segment enforcement")

    all_tables = set(t.name for t in spark.catalog.listTables())
    logger.info(f"All tables in Spark catalog: {list(all_tables)}")

    for enforcement in enforcements:
        viewname = enforcement.viewName
        if viewname not in all_tables:
            raise ValueError(
                f"View {viewname} does not exist in the Spark catalog. Exiting"
            )
        df = spark.table(viewname)
        row_count = df.count()
        if row_count < enforcement.minRowCount:
            raise ValueError(
                f"Pre-segment enforcement failed for view {viewname}: "
                f"row count {row_count} is less than minRowCount {enforcement.minRowCount}"
            )
        logger.info(
            f"Pre-segment enforcement passed for view {viewname}: "
            f"row count {row_count} meets minRowCount {enforcement.minRowCount}"
        )

    logger.info(f"All pre-segment enforcements passed successfully")


def post_segment_filtering(df: DataFrame, enforcements: List[PostFilter]):
    logger = logging.getLogger("post_segment_filtering")
    logger.info(f"Applying post-segment filtering enforcement")
    df_columns = set(df.columns)
    required_columns = set(e.columnName for e in enforcements)
    missing_columns = required_columns - df_columns
    if len(missing_columns) > 0:
        raise ValueError(
            f"Required columns: {missing_columns} missing in the output "
            f"for PostSegmentFilteringEnforcement"
        )

    filtered_df = df
    for enforcement in enforcements:
        # Include those rows where the column value is >= enforcement.value
        filtered_df = filtered_df.filter(
            functions.col(enforcement.columnName) >= enforcement.value
        )
        logger.info(
            f"Post-segment filter enforcement applied successfully for column "
            f"{enforcement.columnName} with value {enforcement.value}"
        )

    logger.info(f"All post-segment filter enforcements applied successfully")
    return filtered_df


async def run_query_segment(
    spark: SparkSession,
    segment: QuerySegment,
    event_emitter: IEventEmitter,
) -> DataFrame:
    logger = logging.getLogger(f"run_query_segment")
    tracer = trace.get_tracer(f"run_query_segment")
    with tracer.start_as_current_span(f"run_query_segment") as span:
        try:
            await event_emitter.log_operational_event(
                OperationalEventFactory.QuerySegmentExecutionStarted(
                    step_number=str(segment.executionSequence or 0),
                )
            )
            logger.info(f"Executing query segment")

            # Check for pre-segment enforcements and fail execution if not met
            if segment.preConditions:
                pre_segment_enforcement(spark, segment.preConditions)

            row_count = 0
            # Execute the SQL query for this Segment
            result = spark.sql(segment.data)
            row_count = result.count()
            logger.info(f"Result has {row_count} rows")

            # post-segment filtering enforcements
            if segment.postFilters:
                result = post_segment_filtering(result, segment.postFilters)
                row_count = result.count()
                logger.info(f"Filtered Result has {row_count} rows")

            logger.info(f"Query segment executed successfully")
            await event_emitter.log_operational_event(
                OperationalEventFactory.QuerySegmentExecutionCompleted(
                    row_count=str(row_count),
                    step_number=str(segment.executionSequence or 0),
                )
            )
            return result

        except Exception as e:
            span.record_exception(e)
            logger.error(f"Error executing query segment: {e}")
            await event_emitter.log_operational_event(
                OperationalEventFactory.QuerySegmentExecutionFailed(
                    step_number=str(segment.executionSequence or 0),
                    error=str(e),
                )
            )
            raise


async def run_query(
    spark: SparkSession,
    config: QueryConfiguration,
    query: Query,
    start_date: str,
    end_date: str,
    event_emitter: IEventEmitter,
    statistics_recorder: IStatisticsRecorder,
    audit_logger: IAuditRecordLogger,
):
    logger = logging.getLogger("run_query")
    tracer = trace.get_tracer("run_query")
    global job_id
    start_time = time.monotonic_ns()
    row_count_read = 0
    row_count_written = 0
    with tracer.start_as_current_span("run_query") as span:
        try:
            logger.info(f"Loading {len(config.datasets)} datasets in parallel")
            dataset_tasks = []
            for dataset in config.datasets:
                task = asyncio.create_task(
                    dataset_loader.load_dataset_async(
                        spark,
                        dataset,
                        start_date,
                        end_date,
                        job_id,
                        event_emitter,
                        audit_logger,
                    )
                )
                dataset_tasks.append(task)

            logger.info("Waiting for all datasets to complete loading")
            results = await asyncio.gather(*dataset_tasks)
            row_count_read = sum(results)
            logger.info(
                f"Datasets loaded successfully with start date:{start_date} and end date:{end_date}"
            )

            logger.info(f"Executing query")
            # Group segments by executionSequence
            segments_by_step = defaultdict(list)
            last_step = 0
            for segment in query.segments:
                step = segment.executionSequence or 0
                segments_by_step[step].append(segment)
                if step > last_step:
                    last_step = step

            # Check that the last step has just 1 segment
            if len(segments_by_step) > 0:
                last_segments = segments_by_step[last_step]
                if len(last_segments) != 1:
                    raise ValueError(
                        f"There should be just 1 last segment but there are {len(last_segments)}."
                    )

            output = None
            # Execute segments step by step, parallel within each step
            for step in sorted(segments_by_step.keys()):
                segments = segments_by_step[step]

                logger.info(
                    f"Executing Step {step} with {len(segments)} segments in parallel"
                )

                # Use asyncio.gather for parallel execution of async functions
                tasks = []
                for segment_index, segment in enumerate(segments):
                    tasks.append(
                        run_query_segment(
                            spark,
                            segment,
                            event_emitter,
                        )
                    )

                # Wait for all executions to complete
                logger.info(f"Waiting for all segments to complete execution")
                results = await asyncio.gather(*tasks, return_exceptions=True)

                # Check for exceptions in any of the results
                for result in results:
                    if isinstance(result, Exception):
                        logger.error(f"Exception caught in segment execution: {result}")
                        raise result

                # Take output only from the last segment
                if last_step == step:
                    output = results[0]

            logger.info(
                f"All query segments executed successfully. Writing results if any."
            )
            if output:
                row_count_written = await dataset_loader.write_dataset_async(
                    job_id, spark, output, config.datasink, event_emitter, audit_logger
                )
            end_time = time.monotonic_ns()
            duration_sec = (end_time - start_time) / 1e9

            logger.info(f"Query executed successfully in {duration_sec} seconds")
            await event_emitter.log_operational_event(
                OperationalEventFactory.QueryExecutionCompleted(
                    duration_sec=str(duration_sec),
                )
            )
            await audit_logger.log_audit_record(
                AuditRecordFactory.QueryCompleted(source="run_query", job_id=job_id)
            )
            await statistics_recorder.record_statistics(
                StatisticsEventFactory.QueryStatistics(
                    num_rows_read=row_count_read,
                    num_rows_written=row_count_written,
                    duration_sec=duration_sec,
                )
            )

        except Exception as e:
            span.record_exception(e)
            logger.error(f"Error executing query: {e} | job id: {job_id}")
            await event_emitter.log_operational_event(
                OperationalEventFactory.QueryExecutionFailed(error=str(e))
            )
            await audit_logger.log_audit_record(
                AuditRecordFactory.QueryFailed(
                    source="run_query", job_id=job_id, reason=str(e)
                )
            )
            raise e


@inject
def main(
    adapter_factory: AdapterFactory = Provide[ServiceLocator.adapter_factory],
):
    global config
    global job_id

    # Get pod information for telemetry
    pod_name = os.getenv("POD_NAME", "analytics-app-pod")
    namespace = os.getenv("POD_NAMESPACE", "default")

    job_id = os.environ.get("JOB_ID") or ""

    if not job_id:
        raise ValueError("Environment variable JOB_ID is not set")

    # Initialize TelemetryConfig
    telemetry_config = TelemetryConfig(
        service_name=f"{job_id}-analytics-app",
        is_otel_enabled=True,  # Always enable for analytics app
        pod_name=pod_name,
        namespace=namespace,
    )
    telemetry_config.setup_telemetry()

    # Get the tracer after telemetry setup
    tracer = trace.get_tracer("analytics-app")

    # Get the logger from the global logger provider
    logger = logging.getLogger("analytics-app")

    trace_context_b64 = os.environ.get("OTEL_TRACE_CONTEXT_BASE64")
    extracted_context = None
    trace_context_carrier = {}
    if trace_context_b64:
        logger.info(
            "Found OTEL_TRACE_CONTEXT_BASE64 environment variable, setting trace context"
        )
        trace_context_carrier = json.loads(base64.b64decode(trace_context_b64).decode())
        extracted_context = otel_utilities.extract_context_from_carrier(
            trace_context_carrier
        )
    else:
        logger.info("No OTEL_TRACE_CONTEXT_BASE64 environment variable found")
    if extracted_context:
        context.attach(extracted_context)
        trace.set_span_in_context(trace.get_current_span())

    spark_job_config = os.environ.get("JOB_CONFIG")
    if not spark_job_config:
        raise ValueError("Environment variable JOB_CONFIG is not set")
    job_config = base64.b64decode(spark_job_config).decode("utf-8")
    config = QueryConfiguration.model_validate_json(job_config)
    query_str = base64.b64decode(config.query).decode("utf-8")
    query = Query.model_validate_json(query_str)

    logger.info(f"Loaded configuration: {config}")
    start_date = os.environ.get("START_DATE") or ""
    end_date = os.environ.get("END_DATE") or ""

    spark = SparkSession.builder.appName(
        f"Clean Room Spark Analytics-{job_id}"
    ).getOrCreate()

    asyncio.run(
        run_query(
            spark,
            config,
            query,
            start_date,
            end_date,
            adapter_factory.event_emitter,
            adapter_factory.statistics_recorder,
            adapter_factory.audit_logger,
        )
    )


if __name__ == "__main__":
    application_configuration_json = os.environ.get("APPLICATION_CONFIGURATION")
    if not application_configuration_json:
        raise ValueError("Environment variable APPLICATION_CONFIGURATION is not set")
    application_configuration = ApplicationConfiguration.model_validate_json(
        application_configuration_json
    )

    # Initialize the dependency injection container
    container = ServiceLocator()
    container.config.from_pydantic(application_configuration)
    container.wire(modules=[__name__])

    main()
