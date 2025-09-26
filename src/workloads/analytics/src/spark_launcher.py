# This script waits for blobfuse to make the mount points available. Once they are available, it
# will launch the original entrypoint script of the Spark image.
# There is no way currently to make Spark wait for the mount points to be available. Even if the
# same code is called from within run_query.py, when the driver translates it into byte-code,
# it only considers transformations and actions to create the plans. To work around that, we will wait
# for the mount points to be available before relinquishing control to the spark entrypoint.

import base64
import json
import logging
import os
import sys
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
            "service.name": f"{logger_name}-spark-launcher",
        }
    ),
)
tracer_provider.add_span_processor(BatchSpanProcessor(OTLPSpanExporter()))

# Sets the global default tracer provider.
trace.set_tracer_provider(tracer_provider)

# Creates a tracer from the global tracer provider.
tracer = trace.get_tracer("spark-launcher")

# Initialize logging
logger_provider = LoggerProvider(
    resource=Resource.create(
        {
            "service.name": f"{logger_name}-spark-launcher",
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
logger = logging.getLogger("spark-launcher")
logger.addHandler(handler)


LoggingInstrumentor().instrument(
    set_logging_format=True,
    tracer_provider=tracer_provider,
)


config: Configuration


def wait_for_mounts():
    for dataset in config.datasets:
        utilities.wait_for_mount_point(dataset.name)

    utilities.wait_for_mount_point(config.datasink.name)


def main():
    global config
    extracted_context = utilities.extract_otel_trace_context()
    if extracted_context:
        context.attach(extracted_context)
        trace.set_span_in_context(trace.get_current_span())
    spark_job_config = os.environ.get("JOB_CONFIG")
    if not spark_job_config:
        raise ValueError("Environment variable JOB_CONFIG is not set")
    job_config = base64.b64decode(spark_job_config).decode("utf-8")
    config = Configuration.model_validate_json(job_config)
    logger.info(f"Loaded configuration: {config}")
    wait_for_mounts()

    # NOTE: This is the entrypoint script of spark: 3.5.5. Any change in the spark version might affect
    # this script.
    # TODO (HPrabh): Check whether this can be replaced with subprocess.run.
    # That will enable the launcher to track the Spark job, report its status and export logs and other telemetry once complete.
    os.execv("/opt/entrypoint.sh", ["/opt/entrypoint.sh"] + sys.argv[1:])


if __name__ == "__main__":
    main()
