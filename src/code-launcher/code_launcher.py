import argparse
import asyncio
import base64
import os
import subprocess
import sys
import uuid
import logging

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse
from podman.errors.exceptions import PodmanError, APIError
import uvicorn

from opentelemetry import _logs
from opentelemetry.sdk._logs import LoggerProvider, LoggingHandler
from opentelemetry.sdk.resources import Resource
from opentelemetry.sdk._logs.export import BatchLogRecordProcessor
from opentelemetry.exporter.otlp.proto.grpc._log_exporter import (
    OTLPLogExporter,
)
from opentelemetry import trace
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.exporter.otlp.proto.grpc.trace_exporter import OTLPSpanExporter
from opentelemetry.sdk.trace.export import BatchSpanProcessor

from opentelemetry import metrics
from opentelemetry.sdk.metrics import MeterProvider
from opentelemetry.sdk.metrics.export import PeriodicExportingMetricReader
from opentelemetry.exporter.otlp.proto.grpc.metric_exporter import OTLPMetricExporter

from opentelemetry.instrumentation.requests import RequestsInstrumentor
from opentelemetry.instrumentation.logging import LoggingInstrumentor
from opentelemetry.instrumentation.fastapi import FastAPIInstrumentor


from src.cmd_executors.executors import ACRCmdExecutor
from src.utilities import utilities, podman_utilities

from cleanroomspec.models.python.model import (
    Application,
    ApplicationStartType,
    ConsentCheckScope,
)

app = FastAPI()
cmd_arguments: argparse.Namespace
application: Application
telemetry_path = os.environ.get("TELEMETRY_MOUNT_PATH", "/mnt/telemetry")

application_name = os.environ.get("APPLICATION_NAME", "no-name")
logger_name = "-".join([application_name, str(uuid.uuid4())[:8]])

# Initialize tracing.
tracer_provider = TracerProvider(
    resource=Resource.create(
        {
            "service.name": f"{logger_name}-code-launcher",
        }
    ),
)
tracer_provider.add_span_processor(BatchSpanProcessor(OTLPSpanExporter()))

# Sets the global default tracer provider.
trace.set_tracer_provider(tracer_provider)

# Creates a tracer from the global tracer provider.
tracer = trace.get_tracer("code-launcher")

# Initialize logging
logger_provider = LoggerProvider(
    resource=Resource.create(
        {
            "service.name": f"{logger_name}-code-launcher",
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
logger = logging.getLogger("code-launcher")
logger.addHandler(handler)

# Create a meter provider
exporter = OTLPMetricExporter()
reader = PeriodicExportingMetricReader(exporter, export_interval_millis=10000)
meter_provider = MeterProvider(
    metric_readers=[reader],
    resource=Resource.create(
        {
            "service.name": f"{logger_name}-code-launcher",
        }
    ),
)
metrics.set_meter_provider(meter_provider)

# Add all the external instrumentors that are required.
RequestsInstrumentor().instrument(
    tracer_provider=tracer_provider, meter_provider=meter_provider
)
FastAPIInstrumentor.instrument_app(
    app, tracer_provider=tracer_provider, meter_provider=meter_provider
)
LoggingInstrumentor().instrument(
    set_logging_format=True,
    tracer_provider=tracer_provider,
    meter_provider=meter_provider,
)


def log_args(logger: logging.Logger, args: argparse.Namespace):
    logger.info("Arguments:")
    for arg in vars(args):
        logger.info(f"{arg}: {getattr(args, arg)}")


def parse_args(cmd_args):
    arg_parser = argparse.ArgumentParser(
        description="Launch container in non-root namespace"
    )
    # Telemetry params
    arg_parser.add_argument(
        "--application-name",
        type=str,
        default="app",
        help="Application Name for telemetry export",
    )
    arg_parser.add_argument(
        "--identity-port",
        type=int,
        default=8290,
        help="The port for the identity sidecar. Defaults to 8290.",
    )
    # governance params
    arg_parser.add_argument(
        "--governance-port",
        type=int,
        default=8300,
        help="The port for the governance sidecar. Defaults to 8300.",
    )
    # secrets params
    arg_parser.add_argument(
        "--secrets_port",
        type=int,
        default=9300,
        help="The port for the secrets sidecar. Defaults to 9300.",
    )
    # otelcollector params
    arg_parser.add_argument(
        "--otelcollector_port",
        type=int,
        default=4317,
        help="The port for the otelcollector sidecar. Defaults to 4317.",
    )
    # Code launcher port
    arg_parser.add_argument(
        "--codelauncher_port",
        type=int,
        default=8200,
        help="The port for the otelcollector sidecar. Defaults to 4317.",
    )
    # TODO (HPrabh): Take this as an array and support multiple applications.
    arg_parser.add_argument(
        "--application-base-64",
        type=str,
        default=os.environ.get("APPLICATION_SETTINGS_BASE64"),
        help="The application settings in a base64 encoded format.",
    )
    parsed_args = arg_parser.parse_args(cmd_args)
    validate_args(parsed_args, arg_parser)
    return parsed_args


def validate_args(cmd_args, arg_parser):
    if cmd_args.application_base_64 is None:
        arg_parser.error("application details not provided")

    application = base64.urlsafe_b64decode(cmd_args.application_base_64)
    Application.model_validate_json(application)


@app.exception_handler(PodmanError)
async def podman_error_handler(request: Request, exc: PodmanError):
    logger.error(f"An error occurred: {repr(exc)}")
    # TODO (HPrabh): Add more specific error handling based on the error type.
    return JSONResponse(
        status_code=500,
        content={
            "message": f"An error occurred while processing {request.url.path}",
            "error": f"{repr(exc)}",
            "details": f"{exc}",
        },
    )


@app.exception_handler(APIError)
async def podman_api_error_handler(request: Request, exc: APIError):
    logger.error(f"An error occurred: {repr(exc)}")
    status_code = exc.status_code if exc.status_code is not None else 500
    return JSONResponse(
        status_code=status_code,
        content={
            "message": f"An error occurred while processing {request.url.path}",
            "error": f"{repr(exc)}",
            "details": f"{exc}",
        },
    )


from src.exceptions.custom_exceptions import ConsentCheckFailure


@app.exception_handler(ConsentCheckFailure)
async def consent_check_failure_handler(request: Request, exc: ConsentCheckFailure):
    return JSONResponse(
        status_code=403,
        content={"code": "ConsentCheckFailed", "message": str(exc)},
    )


@app.post("/gov/{application_name}/start")
async def start(application_name):
    from src.connectors.httpconnectors import GovernanceHttpConnector

    await GovernanceHttpConnector.check_consent(ConsentCheckScope.Execution.value)
    if application.name != application_name:
        raise HTTPException(status_code=404, detail="Application not found")

    try:
        await podman_utilities.start_application_container(application, telemetry_path)
        return JSONResponse(
            status_code=200, content={"message": "Application started successfully."}
        )
    except Exception as e:
        logger.error(
            f"Starting application container for {application.name} failed with error {repr(e)}.",
            exc_info=True,
        )
        await GovernanceHttpConnector.put_event(
            f"Starting application container for {application.name} failed with error {repr(e)}."
        )
        raise


@app.get("/gov/{application_name}/status")
async def getStatus(application_name):
    from src.exceptions.custom_exceptions import PodmanContainerNotFound
    from src.connectors.httpconnectors import GovernanceHttpConnector

    try:
        return await podman_utilities.get_application_status(application_name)
    except PodmanContainerNotFound as e:
        await GovernanceHttpConnector.put_event(
            f"Get Status for {application.name} failed with error {repr(e)}."
        )
        raise HTTPException(status_code=404, detail="Application not found")
    except Exception as e:
        logger.error(
            f"Get Status for {application.name} failed with error {repr(e)}.",
            exc_info=True,
        )
        await GovernanceHttpConnector.put_event(
            f"Get Status for {application.name} failed with error {repr(e)}."
        )
        raise


@app.post("/gov/exportLogs")
async def exportLogs():
    import shutil
    from src.connectors.httpconnectors import (
        GovernanceHttpConnector,
    )

    await GovernanceHttpConnector.check_consent(ConsentCheckScope.Logging.value)
    application_telemetry_path = utilities.wait_for_mount_point("application-telemetry")

    try:
        logger.info(
            f"Copying application telemetry data to {application_telemetry_path}"
        )
        shutil.copytree(
            f"{telemetry_path}/application",
            f"{application_telemetry_path}",
            dirs_exist_ok=True,
        )
        return JSONResponse(
            status_code=200,
            content={"message": "Application telemetry data exported successfully."},
        )
    except Exception as e:
        from src.exceptions.custom_exceptions import TelemetryCaptureFailure

        await GovernanceHttpConnector.put_event(
            f"Exporting application telemetry (logs) failed with error {e}."
        )
        raise TelemetryCaptureFailure("Failed to export application telemetry") from e


@app.post("/gov/exportTelemetry")
async def exportTelemetry():
    import shutil
    from src.connectors.httpconnectors import (
        GovernanceHttpConnector,
    )

    await GovernanceHttpConnector.check_consent(ConsentCheckScope.Telemetry.value)
    infrastructure_telemetry_path = utilities.wait_for_mount_point(
        "infrastructure-telemetry"
    )

    try:
        logger.info(
            f"Copying infrastructure telemetry data to {infrastructure_telemetry_path}"
        )
        shutil.copytree(
            f"{telemetry_path}/infrastructure",
            f"{infrastructure_telemetry_path}",
            dirs_exist_ok=True,
        )
        return JSONResponse(
            status_code=200,
            content={"message": "Infrastructure telemetry data exported successfully."},
        )
    except Exception as e:
        from src.exceptions.custom_exceptions import TelemetryCaptureFailure

        await GovernanceHttpConnector.put_event(
            f"Exporting infrastructure telemetry failed with error {repr(e)}."
        )
        raise TelemetryCaptureFailure(
            "Failed to export infrastructure telemetry"
        ) from e


async def main(cmd_args):
    global cmd_arguments
    global application
    cmd_arguments = parse_args(cmd_args)

    if utilities.wait_for_services_enabled():
        # wait for critical infra services to be ready
        utilities.wait_for_services_readiness(
            [
                cmd_arguments.otelcollector_port,
                cmd_arguments.governance_port,
                cmd_arguments.identity_port,
                cmd_arguments.secrets_port,
            ]
        )

    try:
        import psutil

        process = psutil.Popen(
            ["podman", "system", "service", "--time", "0"],
        )
        logger.info(f"Subprocess podman start service exitCode: {process.returncode}")
    except subprocess.SubprocessError as e:
        logger.error(f"Failed to launch subprocess podman start service. Error: {e}")
        raise e

    await utilities.wait_for_podman_service()

    decoded_application = base64.urlsafe_b64decode(
        cmd_arguments.application_base_64
    ).decode()
    application = Application.model_validate_json(decoded_application)
    logger.info(f"Starting podman for application: {application.model_dump_json()}")

    log_args(logger, cmd_arguments)

    from src.connectors.httpconnectors import (
        IdentityHttpConnector,
        GovernanceHttpConnector,
    )

    IdentityHttpConnector.set_port(cmd_arguments.identity_port)
    GovernanceHttpConnector.set_port(cmd_arguments.governance_port)

    # execute handlers
    for handlers in [ACRCmdExecutor()]:
        await handlers.execute(application)

    if application.startType == ApplicationStartType.Auto:
        try:
            await GovernanceHttpConnector.check_consent(
                ConsentCheckScope.Execution.value
            )
            await podman_utilities.start_application_container(
                application, telemetry_path
            )
        except Exception as e:
            logger.error(
                f"Starting application container for {application.name} failed with error {repr(e)}.",
                exc_info=True,
            )
            await GovernanceHttpConnector.put_event(
                f"Starting application container for {application.name} failed with error {repr(e)}."
            )
    else:
        logger.info(
            f"Not starting application {application.name} as the start type is not {ApplicationStartType.Auto}."
        )

    config = uvicorn.Config(
        app=app,
        host="0.0.0.0",
        port=cmd_arguments.codelauncher_port,
        log_level="info",
    )
    _server = uvicorn.Server(config)
    await _server.serve()


if __name__ == "__main__":
    asyncio.run(main(sys.argv[1:]))
