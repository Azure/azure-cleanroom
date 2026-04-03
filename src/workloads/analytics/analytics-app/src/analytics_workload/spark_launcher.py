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

from analytics_workload.config.configuration import QueryConfiguration
from analytics_workload.utilities import utilities
from cleanroom_internal.utilities import otel_utilities
from cleanroom_internal.utilities.otel_setup_utilities import TelemetryConfig
from opentelemetry import context, trace

config: QueryConfiguration


def wait_for_mounts():
    for dataset in config.datasets:
        utilities.wait_for_mount_point(dataset.name)

    utilities.wait_for_mount_point(config.datasink.name)


def main():
    global config

    job_id = os.environ.get("JOB_ID") or ""

    if not job_id:
        raise ValueError("Environment variable JOB_ID is not set")

    # Setup telemetry early in the process
    pod_name = os.getenv("POD_NAME", f"{job_id}-spark-launcher")
    namespace = os.getenv("POD_NAMESPACE", "default")

    telemetry_config = TelemetryConfig(
        service_name=f"{job_id}-spark-launcher",
        is_otel_enabled=True,  # Always enable for Spark jobs
        pod_name=pod_name,
        namespace=namespace,
    )
    telemetry_config.setup_telemetry()

    logger = logging.getLogger("spark-launcher")
    trace_context_b64 = os.environ.get("OTEL_TRACE_CONTEXT_BASE64")
    extracted_context = None
    if trace_context_b64:
        logger.info(
            "Found OTEL_TRACE_CONTEXT_BASE64 environment variable, setting trace context"
        )
        trace_context_json = base64.b64decode(trace_context_b64).decode()
        trace_context_carrier = json.loads(trace_context_json)
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
    logger.info(f"Loaded configuration: {config}")
    wait_for_mounts()

    # NOTE: This is the entrypoint script of spark: 4.0.0. Any change in the spark version might affect
    # this script.
    # TODO (HPrabh): Check whether this can be replaced with subprocess.run.
    # That will enable the launcher to track the Spark job, report its status and export logs and other telemetry once complete.
    os.execv("/opt/entrypoint.sh", ["/opt/entrypoint.sh"] + sys.argv[1:])


if __name__ == "__main__":
    main()
