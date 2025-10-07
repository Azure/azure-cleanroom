import base64
import logging
import os
import uuid

from opentelemetry import _logs, context, trace
from opentelemetry.exporter.otlp.proto.grpc._log_exporter import OTLPLogExporter
from opentelemetry.exporter.otlp.proto.grpc.trace_exporter import OTLPSpanExporter
from opentelemetry.instrumentation.logging import LoggingInstrumentor
from opentelemetry.sdk._logs import LoggerProvider, LoggingHandler
from opentelemetry.sdk._logs.export import BatchLogRecordProcessor
from opentelemetry.sdk.resources import Resource
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import BatchSpanProcessor
from pyspark.sql import SparkSession
from src.config.configuration import Configuration
from src.utilities import dataset_loader, utilities

application_name = os.environ.get("SPARK_APPLICATION_ID", "no-name")
logger_name = "-".join([application_name, str(uuid.uuid4())[:8]])

tracer_provider = TracerProvider(
    resource=Resource.create(
        {
            "service.name": f"{logger_name}-analytics-app",
        }
    ),
)
tracer_provider.add_span_processor(BatchSpanProcessor(OTLPSpanExporter()))

# Sets the global default tracer provider.
trace.set_tracer_provider(tracer_provider)

# Creates a tracer from the global tracer provider.
tracer = trace.get_tracer("analytics-app")

# Initialize logging
logger_provider = LoggerProvider(
    resource=Resource.create(
        {
            "service.name": f"{logger_name}-analytics-app",
        }
    ),
)
logger_provider.add_log_record_processor(
    BatchLogRecordProcessor(OTLPLogExporter(insecure=True))
)
_logs.set_logger_provider(logger_provider)

# Create a logger from the global logger provider.
logging.basicConfig(level=logging.INFO)
handler = LoggingHandler(logger_provider=logger_provider)
logger = logging.getLogger("analytics-app")
logger.addHandler(handler)


LoggingInstrumentor().instrument(
    set_logging_format=True,
    tracer_provider=tracer_provider,
)

config: Configuration
job_id: str


def run_query(
    spark: SparkSession, config: Configuration, start_date: str, end_date: str
):
    logger = logging.getLogger("run_query")
    tracer = trace.get_tracer("run_query")
    with tracer.start_as_current_span("run_query") as span:
        try:
            for dataset in config.datasets:
                dataset_loader.load_dataset(spark, dataset, start_date, end_date)
            logger.info(
                f"Datasets loaded successfully with startdate:{start_date} and enddate:{end_date}"
            )
            logger.info(f"Executing query: {config.query}")
            result = spark.sql(config.query)
            logger.info("Query executed successfully")
            dataset_loader.write_dataset(job_id, spark, result, config.datasink)
        except Exception as e:
            span.record_exception(e)
            logger.error(f"Error executing query: {e}")
            raise e


def main():
    global config
    global job_id
    extracted_context = utilities.extract_otel_trace_context()
    if extracted_context:
        context.attach(extracted_context)
        trace.set_span_in_context(trace.get_current_span())

    spark_job_config = os.environ.get("JOB_CONFIG")
    if not spark_job_config:
        raise ValueError("Environment variable JOB_CONFIG is not set")
    job_config = base64.b64decode(spark_job_config).decode("utf-8")
    config = Configuration.model_validate_json(job_config)
    start_date = os.environ.get("START_DATE") or ""
    end_date = os.environ.get("END_DATE") or ""
    job_id = os.environ.get("JOB_ID")
    if not job_id:
        raise ValueError("Environment variable JOB_ID is not set")
    spark = SparkSession.builder.appName(
        f"Clean Room Spark Analytics-{job_id}"
    ).getOrCreate()
    run_query(spark, config, start_date, end_date)


if __name__ == "__main__":
    # Initialize Spark session and load data
    main()
