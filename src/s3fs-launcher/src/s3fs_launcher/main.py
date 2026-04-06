import argparse
import base64
import json
import logging
import os
import signal
import sys
import time
import uuid

from cleanroom_internal.utilities import otel_utilities, secret_utilities
from cleanroom_internal.utilities import utilities as internal_utilities
from cleanroom_internal.utilities.otel_setup_utilities import TelemetryConfig
from opentelemetry import context, trace

from .utilities import *

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

# Global logger and telemetry providers - will be initialized in main()
logger = logging.getLogger("s3fs-launcher")
tracer = None
tracer_provider = None
logger_provider = None
meter_provider = None

args: argparse.Namespace


# Define the handler function
def handle_sigterm(signum, frame):
    global logger_provider, meter_provider, tracer_provider
    logger.info("Signal handler called with signal: %s", signum)
    if logger_provider:
        logger_provider.shutdown()
    if meter_provider:
        meter_provider.shutdown()
    if tracer_provider:
        tracer_provider.shutdown()
    logger.info("Signal handler exiting")
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


def main():
    global tracer, tracer_provider, logger_provider, meter_provider, logger

    # Get pod information for telemetry
    pod_name = os.getenv("POD_NAME", "s3fs-launcher-pod")
    namespace = os.getenv("POD_NAMESPACE", "default")

    # Initialize TelemetryConfig
    telemetry_config = TelemetryConfig(
        service_name=f"{logger_name}-s3fs-launcher",
        is_otel_enabled=True,  # Always enable for s3fs launcher
        pod_name=pod_name,
        namespace=namespace,
    )
    telemetry_config.setup_telemetry()
    telemetry_config.instrument_requests()

    # Get the tracer after telemetry setup
    tracer = trace.get_tracer("s3fs-launcher")

    with tracer.start_as_current_span("s3fs-launcher"):
        global args
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
        try:
            internal_utilities.wait_for_services_readiness(
                logger,
                tracer,
                [args.otel_collector_port],
            )
        except Exception:
            logger.warning(
                "OTel collector endpoint on port %s is not available."
                " Continuing without telemetry export.",
                args.otel_collector_port,
            )
        internal_utilities.wait_for_services_readiness(
            logger,
            tracer,
            [args.governance_port],
        )
        logger.info(f"Fetching secret '{args.cgs_aws_s3_config_secret}' from CGS")
        aws_config_bytes = secret_utilities.get_cgs_secret(
            logger, tracer, args.governance_port, args.cgs_aws_s3_config_secret
        )
        aws_config = json.loads(aws_config_bytes.decode("utf-8"))
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

        returncode = launch_s3fs(
            logger,
            tracer,
            bucket_name,  # type: ignore - checked above
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

    try:
        while True:
            time.sleep(3600)  # 1 hour at a time or gets interrupted due to SIGTERM.
    except KeyboardInterrupt:
        logger.info("Interrupted!")

    sys.exit(returncode)
