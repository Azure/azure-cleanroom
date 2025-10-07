import argparse
import base64
import hashlib
import json
import logging
import os
import signal
import sys
import time
import uuid
from math import e

import utilities
from opentelemetry import _logs, context, metrics, trace
from opentelemetry.exporter.otlp.proto.grpc._log_exporter import OTLPLogExporter
from opentelemetry.exporter.otlp.proto.grpc.metric_exporter import OTLPMetricExporter
from opentelemetry.exporter.otlp.proto.grpc.trace_exporter import OTLPSpanExporter
from opentelemetry.instrumentation.logging import LoggingInstrumentor
from opentelemetry.instrumentation.requests import RequestsInstrumentor
from opentelemetry.sdk._logs import LoggerProvider, LoggingHandler
from opentelemetry.sdk._logs.export import BatchLogRecordProcessor
from opentelemetry.sdk.metrics import MeterProvider
from opentelemetry.sdk.metrics.export import PeriodicExportingMetricReader
from opentelemetry.sdk.resources import Resource
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import BatchSpanProcessor

# TODO these are also define under code_launcher in utilities.py, consider moving them to a common place
# Updates if any should be done at both places
BLOBFUSE_LAUNCHER_RETRIES = 5
BLOBFUSE_LAUNCHER_RETRY_DELAY = 10

container_name = os.environ.get("AZURE_STORAGE_ACCOUNT_CONTAINER")
access_name = os.environ.get("ACCESS_NAME")
volumestatus_path = os.environ.get("VOLUMESTATUS_MOUNT_PATH", "/mnt/volumestatus")
telemetry_path = os.environ.get("TELEMETRY_MOUNT_PATH", "/mnt/telemetry")

if container_name is None:
    raise ValueError("AZURE_STORAGE_ACCOUNT_CONTAINER environment variable is not set")

logger_name = "-".join([access_name, container_name, str(uuid.uuid4())[:8]])

# Initialize tracing.
tracer_provider = TracerProvider(
    resource=Resource.create(
        {
            "service.name": f"{logger_name}-blobfuse-launcher",
        }
    ),
)
tracer_provider.add_span_processor(BatchSpanProcessor(OTLPSpanExporter(insecure=True)))

# Sets the global default tracer provider.
trace.set_tracer_provider(tracer_provider)

# Creates a tracer from the global tracer provider.
tracer = trace.get_tracer("blobfuse-launcher")

# Initialize logging
logger_provider = LoggerProvider(
    resource=Resource.create(
        {
            "service.name": f"{logger_name}-blobfuse-launcher",
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
logger = logging.getLogger("blobfuse-launcher")
logger.addHandler(handler)

# Create a meter provider
exporter = OTLPMetricExporter(insecure=True)
reader = PeriodicExportingMetricReader(exporter, export_interval_millis=1000)
meter_provider = MeterProvider(
    metric_readers=[reader],
    resource=Resource.create(
        {
            "service.name": f"{logger_name}-blobfuse-launcher",
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
    # Run blobfuse unmount command
    try:
        utilities.subprocess_launch(
            logger,
            tracer,
            "blobfuse-unmount",
            ["blobfuse2", "unmount", args.mount_path],
            wait_for_completion=True,
        )
        logger.info(f"Successfully unmounted blobfuse from {args.mount_path}")
    except Exception as e:
        logger.error(f"Failed to unmount blobfuse: {e}")

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
        prog="blobfuse-launcher.py",
        description="Launch blobfuse with secure key release",
    )
    parser.add_argument(
        "--skr-port", type=int, default=8284, help="The port for the SKR sidecar"
    )
    parser.add_argument(
        "--governance-port",
        type=int,
        default=8300,
        help="The port for the governance sidecar",
    )
    parser.add_argument(
        "--secrets-port",
        type=int,
        default=9300,
        help="The port for the secrets sidecar",
    )
    parser.add_argument(
        "--maa-endpoint",
        type=str,
        default=os.environ.get("MAA_ENDPOINT"),
        help="The MAA endpoint to use for secure key release",
    )
    parser.add_argument(
        "--akv-endpoint",
        type=str,
        default=os.environ.get("AKV_ENDPOINT"),
        help="The Azure Key Vault to use for secure key release",
    )
    parser.add_argument(
        "--kid", type=str, default=os.environ.get("KID"), help="The key ID in AKV"
    )
    parser.add_argument(
        "--imds-port", type=int, default=8290, help="The port for the Identity sidecar"
    )
    parser.add_argument(
        "--otel-collector-port",
        type=int,
        default=4317,
        help="The port for the OTel collector",
    )
    parser.add_argument(
        "--tenant-id",
        type=str,
        default=os.environ.get("TENANT_ID"),
        help="The tenant ID for the MSI token",
    )
    parser.add_argument(
        "--client-id",
        type=str,
        default=os.environ.get("CLIENT_ID"),
        help="The client ID for the MSI token",
    )
    parser.add_argument(
        "--mount-path",
        type=str,
        default="/mnt/blob",
        help="The mount path for blobfuse",
    )
    parser.add_argument(
        "--read-only",
        type=bool,
        action=argparse.BooleanOptionalAction,
        help="The mount container in read only or not",
    )
    parser.add_argument(
        "--use-adls",
        type=bool,
        action=argparse.BooleanOptionalAction,
        help="Use ADLS as the storage backend",
    )
    parser.add_argument(
        "--wrapped-dek-secret",
        type=str,
        default=os.environ.get("WRAPPED_DEK_SECRET"),
        help="The wrapped DEK secret",
    )
    parser.add_argument(
        "--wrapped-dek-akv-endpoint",
        type=str,
        default=os.environ.get("WRAPPED_DEK_AKV_ENDPOINT"),
        help="The Azure Key Vault endpoint holding the wrapped DEK",
    )
    parser.add_argument(
        "--sub-directory",
        type=str,
        default="",
        help="The sub-directory to mount the container for onelake storage",
    )
    parser.add_argument(
        "--encryption-mode",
        type=str,
        choices=["CPK", "CSE", "SSE"],
        default="CPK",
        help="The encryption mode to use for blobfuse",
    )
    parser.add_argument(
        "--block-size-mb",
        type=int,
        default=16,
        help="The block size in MB for blobfuse",
    )
    parser.add_argument(
        "--cgs-dek-secret",
        type=str,
        default=os.environ.get("CGS_DEK_SECRET"),
        help="The CGS DEK secret ID",
    )

    return parser.parse_args()


@tracer.start_as_current_span("blobfuse-launcher")
def main():
    global args
    extracted_context = utilities.extract_otel_trace_context()
    if extracted_context:
        context.attach(extracted_context)
        trace.set_span_in_context(trace.get_current_span())

    args = parse_args()
    log_args(logger, args)

    encryption_key_base64 = ""
    utilities.wait_for_services_readiness(
        logger,
        tracer,
        [
            args.otel_collector_port,
            args.imds_port,
            args.governance_port,
        ],
    )
    if args.encryption_mode in ["CPK", "CSE"]:
        if args.cgs_dek_secret:
            logger.info(f"Fetching secret '{args.cgs_dek_secret}' from CGS")
            encryption_key = utilities.get_cgs_secret(
                logger, tracer, args.governance_port, args.cgs_dek_secret
            )
        else:
            utilities.wait_for_services_readiness(
                logger,
                tracer,
                [
                    args.skr_port,
                    args.secrets_port,
                ],
            )
            logger.info(
                f"Releasing key '{args.kid}' from Key vault '{args.akv_endpoint}' using MAA '{args.maa_endpoint}'"
            )

            encryption_key = utilities.unwrap_secret(
                logger,
                tracer,
                args.secrets_port,
                args.client_id,
                args.tenant_id,
                args.wrapped_dek_secret,
                args.wrapped_dek_akv_endpoint,
                args.kid,
                args.akv_endpoint,
                args.maa_endpoint,
            )
        encryption_key_base64 = base64.standard_b64encode(encryption_key).decode()
    # Create directories if they don't exist.
    os.makedirs(args.mount_path, exist_ok=True)
    os.makedirs("/tmp/blobfuse_tmp", exist_ok=True)

    os.environ["AZURE_STORAGE_AUTH_TYPE"] = "msi"
    os.environ["MSI_ENDPOINT"] = (
        f"http://localhost:{args.imds_port}/metadata/identity/{args.tenant_id}/{args.client_id}/oauth2/token"
    )

    max_retries = BLOBFUSE_LAUNCHER_RETRIES
    attempt = 0

    while attempt < max_retries:
        logger.info(
            f"Starting blobfuse mount at '{args.mount_path}',"
            + f"Read Only: '{args.read_only}',"
            + f"encryption mode: '{args.encryption_mode}'"
        )
        if args.encryption_mode == "CPK":
            # Hash the byte array
            sha256_hash = hashlib.sha256(encryption_key).digest()
            encryption_key_sha256 = base64.b64encode(sha256_hash).decode("utf-8")
            os.environ["AZURE_STORAGE_CPK_ENCRYPTION_KEY"] = encryption_key_base64
            os.environ["AZURE_STORAGE_CPK_ENCRYPTION_KEY_SHA256"] = (
                encryption_key_sha256
            )

            returncode = utilities.launch_blobfuse(
                logger,
                tracer,
                args.mount_path,
                args.read_only,
                args.sub_directory,
                args.use_adls,
                True,
                False,
                args.block_size_mb,
                telemetry_path,
            )
        elif args.encryption_mode == "SSE":
            returncode = utilities.launch_blobfuse(
                logger,
                tracer,
                args.mount_path,
                args.read_only,
                args.sub_directory,
                args.use_adls,
                False,
                False,
                args.block_size_mb,
                telemetry_path,
            )
        else:
            os.environ["ENCRYPTION_KEY"] = encryption_key_base64
            returncode = utilities.launch_blobfuse_encrypted(
                logger, tracer, args.mount_path, args.read_only, telemetry_path
            )

        if returncode == 0:
            logger.info(f"Blobfuse process returncode: {returncode}")
            break
        else:
            # TODO (ashank) for returncode != 0, extract the error code from blobfuse logs
            # and set the error code in the marker file.
            # Only retry for error codes that are transient.
            if attempt == max_retries:
                logger.error(
                    f"Blobfuse process failed with returncode: {returncode}. Giving up."
                )
            else:
                attempt += 1
                logger.error(
                    f"Blobfuse process failed with returncode: {returncode}. Retrying after {BLOBFUSE_LAUNCHER_RETRY_DELAY}s."
                )
                logger.info("Reading blobfuse logs for more details...")
                try:
                    utilities.read_blobfuse_logs(
                        logger, args.mount_path, telemetry_path
                    )
                except Exception as e:
                    logger.error(f"Failed to read blobfuse logs: {e}")
                time.sleep(BLOBFUSE_LAUNCHER_RETRY_DELAY)

    # Create a marker file for other containers that are waiting for the mount point to be
    # available.
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
            description=f"Blobfuse process returncode: {returncode}",
        )
        # Non zero return code from blobfuse. Record error.
        with open(
            os.path.join(volumestatus_path, f"{access_name}.volume.error"), "w"
        ) as f:
            f.write(json.dumps({"error_code": returncode}))
            f.close()
    sys.exit(returncode)


if __name__ == "__main__":
    main()
