import argparse
import base64
import json
import logging
import os
import signal
import sys
import time
import uuid

import utilities
from opentelemetry import _logs, context, metrics, trace
from opentelemetry.exporter.otlp.proto.grpc._log_exporter import OTLPLogExporter
from opentelemetry.exporter.otlp.proto.grpc.metric_exporter import OTLPMetricExporter
from opentelemetry.exporter.otlp.proto.grpc.trace_exporter import OTLPSpanExporter
from opentelemetry.instrumentation.logging import LoggingInstrumentor

# External instrumentors
from opentelemetry.instrumentation.requests import RequestsInstrumentor
from opentelemetry.sdk._logs import LoggerProvider, LoggingHandler
from opentelemetry.sdk._logs.export import BatchLogRecordProcessor
from opentelemetry.sdk.metrics import MeterProvider
from opentelemetry.sdk.metrics.export import PeriodicExportingMetricReader
from opentelemetry.sdk.resources import Resource
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import BatchSpanProcessor

S3FS_LAUNCHER_RETRIES = 5
S3FS_LAUNCHER_RETRY_DELAY = 10


bucket_name = os.environ.get("AWS_BUCKET_NAME")
access_name = os.environ.get("ACCESS_NAME")
volumestatus_path = os.environ.get("VOLUMESTATUS_MOUNT_PATH", "/mnt/volumestatus")
telemetry_path = os.environ.get("TELEMETRY_MOUNT_PATH", "/mnt/telemetry")

if not bucket_name:
    raise ValueError("AWS_BUCKET_NAME environment variable is not set")

if not access_name:
    raise ValueError("ACCESS_NAME environment variable is not set")

logger_name = "-".join([access_name, bucket_name, str(uuid.uuid4())[:8]])

# Initialize tracing.
tracer_provider = TracerProvider(
    resource=Resource.create(
        {
            "service.name": f"{logger_name}-s3fs-launcher",
        }
    ),
)
tracer_provider.add_span_processor(BatchSpanProcessor(OTLPSpanExporter(insecure=True)))

# Sets the global default tracer provider.
trace.set_tracer_provider(tracer_provider)

# Creates a tracer from the global tracer provider.
tracer = trace.get_tracer("s3fs-launcher")

# Initialize logging
logger_provider = LoggerProvider(
    resource=Resource.create(
        {
            "service.name": f"{logger_name}-s3fs-launcher",
        }
    ),
)
logger_provider.add_log_record_processor(
    BatchLogRecordProcessor(OTLPLogExporter(insecure=True))
)
_logs.set_logger_provider(logger_provider)

# Create a logger from the global logger provider.
logging.basicConfig(level=logging.INFO)
handler = LoggingHandler(level=logging.NOTSET, logger_provider=logger_provider)
logger = logging.getLogger("s3fs-launcher")
logger.addHandler(handler)

# Create a meter provider
exporter = OTLPMetricExporter(insecure=True)
reader = PeriodicExportingMetricReader(exporter, export_interval_millis=1000)
meter_provider = MeterProvider(
    metric_readers=[reader],
    resource=Resource.create(
        {
            "service.name": f"{logger_name}-s3fs-launcher",
        }
    ),
)
metrics.set_meter_provider(meter_provider)

# Add all the external instrumentors that are required.
RequestsInstrumentor().instrument(
    tracer_provider=tracer_provider, meter_provider=meter_provider
)
LoggingInstrumentor().instrument(
    set_logging_format=True,
    tracer_provider=tracer_provider,
    meter_provider=meter_provider,
)

args: argparse.Namespace


# Define the handler function
def handle_sigterm(signum, frame):
    global args
    logger.info("Received SIGTERM. Cleaning up...")
    # Run s3fs unmount command
    try:
        utilities.subprocess_launch(
            logger,
            tracer,
            "s3fs-umount",
            ["umount", args.mount_path],
            wait_for_completion=True,
        )
        logger.info(f"Successfully unmounted s3fs from {args.mount_path}")
    except Exception as e:
        logger.error(f"Failed to unmount s3fs: {e}")

    logger_provider.shutdown()
    meter_provider.shutdown()
    tracer_provider.shutdown()
    sys.exit(0)


# Register the handler
signal.signal(signal.SIGTERM, handle_sigterm)


def log_args(logger: logging.Logger, args: argparse.Namespace):
    logger.info("Arguments:")
    for arg in vars(args):
        logger.info(f"{arg}: {getattr(args, arg)}")


def parse_args():
    parser = argparse.ArgumentParser(
        prog="s3fs-launcher.py",
        description="Launch s3fs with secure key release from CGS",
    )
    parser.add_argument(
        "--governance-port",
        type=int,
        default=8300,
        help="The port for the governance sidecar",
    )
    parser.add_argument(
        "--otel-collector-port",
        type=int,
        default=4317,
        help="The port for the OTel collector",
    )
    parser.add_argument(
        "--mount-path",
        type=str,
        default="/mnt/bucket",
        help="The mount path for the bucket",
    )
    parser.add_argument(
        "--read-only",
        type=bool,
        action=argparse.BooleanOptionalAction,
        help="The mount container in read only or not",
    )
    parser.add_argument(
        "--aws-url",
        type=str,
        default=os.environ.get("AWS_URL") or "https://s3.amazonaws.com",
        help="The url to use to access Amazon S3",
    )
    parser.add_argument(
        "--use-path-request-style",
        type=bool,
        action=argparse.BooleanOptionalAction,
        help="use path request style for S3 requests",
    )
    parser.add_argument(
        "--cgs-aws-s3-config-secret",
        type=str,
        default=os.environ.get("CGS_AWS_S3_CONFIG_SECRET"),
        help="The CGS AWS S3 config secret",
    )

    return parser.parse_args()


@tracer.start_as_current_span("s3fs-launcher")
def main():
    global args
    extracted_context = utilities.extract_otel_trace_context()
    if extracted_context:
        context.attach(extracted_context)
        trace.set_span_in_context(trace.get_current_span())

    args = parse_args()
    log_args(logger, args)

    # Note (gsinha): Directly using environment variables for AWS credentials is a test hook
    # that avoids running CGS. Remove once flow is fully implemented.
    has_access_key_id = "AWS_ACCESS_KEY_ID" in os.environ
    has_secret_key = "AWS_SECRET_ACCESS_KEY" in os.environ
    if has_access_key_id and has_secret_key:
        logger.info(f"AWS secrets are available via environment variables")
        aws_access_key_id = os.environ["AWS_ACCESS_KEY_ID"]
        aws_secret_access_key = os.environ["AWS_SECRET_ACCESS_KEY"]
    else:
        utilities.wait_for_services_readiness(
            logger,
            tracer,
            [
                args.otel_collector_port,
                args.governance_port,
            ],
        )
        logger.info(f"Fetching secret '{args.cgs_aws_s3_config_secret}' from CGS")
        aws_config_base64 = utilities.get_cgs_secret(
            logger, tracer, args.governance_port, args.cgs_aws_s3_config_secret
        )
        aws_config = json.loads(base64.b64decode(aws_config_base64).decode("utf-8"))
        aws_access_key_id = aws_config["awsAccessKeyId"]
        aws_secret_access_key = aws_config["awsSecretAccessKey"]

    # Create directories if they don't exist.
    os.makedirs(args.mount_path, exist_ok=True)
    os.makedirs("/tmp/s3fs_tmp", exist_ok=True)

    max_retries = S3FS_LAUNCHER_RETRIES
    attempt = 0

    while attempt < max_retries:
        logger.info(
            f"Starting s3fs mount at '{args.mount_path}' for bucket '{bucket_name}' with aws_url '{args.aws_url}', read-only: {args.read_only}"
        )
        os.environ["AWS_ACCESS_KEY_ID"] = aws_access_key_id
        os.environ["AWS_SECRET_ACCESS_KEY"] = aws_secret_access_key

        returncode = utilities.launch_s3fs(
            logger,
            tracer,
            bucket_name,
            args.mount_path,
            args.read_only,
            args.aws_url,
            args.use_path_request_style,
            telemetry_path,
        )
        if returncode == 0:
            logger.info(f"s3fs process returncode: {returncode}")
            break
        else:
            # TODO (ashank) for returncode != 0, extract the error code from s3fs logs
            # and set the error code in the marker file.
            # Only retry for error codes that are transient.
            if attempt == max_retries:
                logger.error(
                    f"s3fs process failed with returncode: {returncode}. Giving up."
                )
            else:
                attempt += 1
                logger.error(
                    f"s3fs process failed with returncode: {returncode}. Retrying after {S3FS_LAUNCHER_RETRY_DELAY}s."
                )
                time.sleep(S3FS_LAUNCHER_RETRY_DELAY)

    # Create a marker file for other containers that are waiting for the mount point to be
    # available.
    if not os.path.exists(volumestatus_path):
        os.makedirs(volumestatus_path)
    if returncode == 0:
        with open(
            os.path.join(volumestatus_path, f"{access_name}.volume.ready"), "w"
        ) as f:
            f.write(json.dumps({"mount_path": args.mount_path}))
            f.close()

        try:
            while True:
                time.sleep(3600)  # 1 hour at a time or gets interrupted due to SIGTERM.
        except KeyboardInterrupt:
            logger.info("Interrupted!")

    else:
        trace.get_current_span().set_status(
            status=trace.StatusCode.ERROR,
            description=f"s3fs process returncode: {returncode}",
        )
        # Non zero return code from s3fs. Record error.
        with open(
            os.path.join(volumestatus_path, f"{access_name}.volume.error"), "w"
        ) as f:
            f.write(json.dumps({"error_code": returncode}))
            f.close()
    sys.exit(returncode)


if __name__ == "__main__":
    main()
