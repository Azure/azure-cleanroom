import base64
import json
import logging
import os
import socket
import time

from opentelemetry import trace
from opentelemetry.trace.propagation.tracecontext import TraceContextTextMapPropagator

# TODO these are also defined in blobfuse_launcher.py, consider moving them to a common place
# Updates if any should be done at both places
BLOBFUSE_LAUNCHER_RETRIES = 10
BLOBFUSE_LAUNCHER_RETRY_DELAY = 30

volumestatus_mountpath = os.environ.get("VOLUMESTATUS_MOUNT_PATH", "/mnt/volumestatus")


def extract_otel_trace_context():
    logger = logging.getLogger("utilities")
    try:
        trace_context_b64 = os.environ.get("OTEL_TRACE_CONTEXT_BASE64")
        if trace_context_b64:
            logger.info(
                "Found OTEL_TRACE_CONTEXT_BASE64 environment variable, setting trace context"
            )

            trace_context_json = base64.b64decode(trace_context_b64).decode()
            trace_context = json.loads(trace_context_json)

            return TraceContextTextMapPropagator().extract(
                trace_context,
                context=None,
            )
        else:
            logger.info("No OTEL_TRACE_CONTEXT_BASE64 environment variable found")
    except Exception as e:
        logger.error(f"Error restoring OTEL trace context: {e}")


def wait_for_mount_point(access_name) -> str:
    logger = logging.getLogger("utilities")
    tracer = trace.get_tracer("utilities")
    volume_ready = False
    max_retries = BLOBFUSE_LAUNCHER_RETRIES + 2
    delay = BLOBFUSE_LAUNCHER_RETRY_DELAY
    attempt = 0

    with tracer.start_as_current_span(f"wait_for_mount_point-{access_name}") as span:
        while attempt < max_retries:
            logger.info(f"Checking if mount point for {access_name} is ready")
            span.set_attribute("attempt", attempt)
            if is_volume_ready(access_name):
                volume_ready = True
                break
            if is_volume_error(access_name):
                volume_ready = False
                break
            attempt += 1
            time.sleep(delay)

        if not volume_ready:
            from src.exceptions.custom_exceptions import MountPointUnavailableFailure

            err = get_blobfuse_error(access_name)
            ex = MountPointUnavailableFailure(
                f"Mount point for {access_name} is not available. Blobfuse exited with error : {err}"
            )
            span.record_exception(ex)
            raise ex

    logger.info(f"Mount point for {access_name} is available")
    return get_mount_path(access_name)


def get_mount_path(access_name) -> str:
    volume_ready_file = os.path.join(
        volumestatus_mountpath, f"{access_name}.volume.ready"
    )
    with open(volume_ready_file, "r") as f:
        return json.loads(f.read())["mount_path"]


def is_volume_ready(access_name) -> bool:
    return os.path.exists(
        os.path.join(volumestatus_mountpath, f"{access_name}.volume.ready")
    )


def is_volume_error(access_name) -> bool:
    return os.path.exists(
        os.path.join(volumestatus_mountpath, f"{access_name}.volume.error")
    )


def get_blobfuse_error(access_name):
    error_file = os.path.join(volumestatus_mountpath, f"{access_name}.volume.error")
    if os.path.exists(error_file):
        with open(error_file, "r") as f:
            return json.loads(f.read())["error_code"]
    return "Unknown"


def wait_for_services_readiness(service_ports):
    logger = logging.getLogger("utilities")
    tracer = trace.get_tracer("utilities")
    max_retries = 60
    delay = 5
    attempt = 0

    for service_port in service_ports:
        with tracer.start_as_current_span(
            f"wait_for_services_readiness-{service_port}"
        ) as span:
            logger.info(f"Waiting for readines of the service on port {service_port}")
            while attempt < max_retries:
                try:
                    s = socket.socket()
                    s.connect(("localhost", service_port))
                    logger.info(f"Service on port {service_port} is available")
                    break
                except:
                    logger.info(
                        f"Service on port {service_port} is not available. Retrying in {delay} seconds"
                    )
                    attempt += 1
                    time.sleep(delay)
                finally:
                    s.close()
            if attempt == max_retries:
                ex = Exception(
                    f"Service on port {service_port} is not available even after waiting for the threshold. Exiting..."
                )
                span.record_exception(ex)
                raise ex
