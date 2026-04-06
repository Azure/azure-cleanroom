import logging
import os
import socket
import time

from cleanroom_internal.utilities import mountpoint_utilities
from cleanroom_internal.utilities import utilities as internal_utilities
from opentelemetry import trace

# Code-launcher specific constants
BLOBFUSE_LAUNCHER_RETRIES = 5
BLOBFUSE_LAUNCHER_RETRY_DELAY = 10


def wait_for_services_enabled():
    """Check if waiting for services is enabled."""
    return os.environ.get("DISABLE_WAIT_FOR_SERVICES") != "true"


def events_enabled():
    """Check if governance events are enabled."""
    return os.environ.get("DISABLE_GOV_EVENTS") != "true"


def wait_for_mount_point(access_name) -> str:
    """Wait for a mount point to become available and return its path."""
    from ..exceptions.custom_exceptions import MountPointUnavailableFailure

    return mountpoint_utilities.wait_for_mount_point(
        access_name=access_name,
        max_retries=BLOBFUSE_LAUNCHER_RETRIES,
        retry_delay=BLOBFUSE_LAUNCHER_RETRY_DELAY,
        mount_failure_exception_class=MountPointUnavailableFailure,
    )


async def wait_for_podman_service():
    """Wait for podman service to become available."""
    logger = logging.getLogger("utilities")
    tracer = trace.get_tracer("utilities")
    service_ready = False
    max_retries = 12
    delay = 5
    attempt = 0

    from . import podman_utilities

    with tracer.start_as_current_span(f"wait_for_podman_service") as span:
        while attempt < max_retries:
            logger.info(f"Checking if podman service is reachable")
            span.set_attribute("attempt", attempt)
            try:
                if await podman_utilities.ping():
                    service_ready = True
                    break
            except Exception as e:
                logger.error(f"Ping failed with error {e}. Retrying...")
            attempt += 1
            time.sleep(delay)

        if not service_ready:
            from ..exceptions.custom_exceptions import PodmanServiceUnreachable

            ex = PodmanServiceUnreachable(f"Podman service in unreachable.")
            span.record_exception(ex)
            raise ex

    logger.info(f"Podman service is reachable")


def wait_for_services_readiness(service_ports):
    """Wait for services to be ready on specified ports.

    This is a convenience wrapper around the internal utilities function
    that automatically provides logger and tracer instances.
    """
    logger = logging.getLogger("utilities")
    tracer = trace.get_tracer("utilities")
    return internal_utilities.wait_for_services_readiness(logger, tracer, service_ports)
