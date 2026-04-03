import json
import logging
import os
import time

from opentelemetry import trace


def get_volumestatus_mountpath() -> str:
    """Get the volume status mount path from environment."""
    return os.environ.get("VOLUMESTATUS_MOUNT_PATH", "/mnt/volumestatus")


def get_mount_path(access_name: str) -> str:
    """Get the mount path for a given access name."""
    volumestatus_mountpath = get_volumestatus_mountpath()
    volume_ready_file = os.path.join(
        volumestatus_mountpath, f"{access_name}.volume.ready"
    )
    with open(volume_ready_file, "r") as f:
        return json.loads(f.read())["mount_path"]


def is_volume_ready(access_name: str) -> bool:
    """Check if a volume is ready."""
    volumestatus_mountpath = get_volumestatus_mountpath()
    return os.path.exists(
        os.path.join(volumestatus_mountpath, f"{access_name}.volume.ready")
    )


def is_volume_error(access_name: str) -> bool:
    """Check if a volume has an error."""
    volumestatus_mountpath = get_volumestatus_mountpath()
    return os.path.exists(
        os.path.join(volumestatus_mountpath, f"{access_name}.volume.error")
    )


def get_blobfuse_error(access_name: str) -> str:
    """Get the error code for a failed blobfuse mount."""
    volumestatus_mountpath = get_volumestatus_mountpath()
    error_file = os.path.join(volumestatus_mountpath, f"{access_name}.volume.error")
    if os.path.exists(error_file):
        with open(error_file, "r") as f:
            return json.loads(f.read())["error_code"]
    return "Unknown"


def wait_for_mount_point(
    access_name: str,
    max_retries: int = 12,
    retry_delay: int = 30,
    mount_failure_exception_class=None,
) -> str:
    """Wait for a mount point to become available and return its path.

    Args:
        access_name: Name of the access/volume to wait for
        max_retries: Maximum number of retries
        retry_delay: Delay between retries in seconds
        mount_failure_exception_class: Exception class to raise on failure

    Returns:
        The mount path for the volume

    Raises:
        Exception or custom exception if mount point becomes unavailable
    """
    logger = logging.getLogger("utilities")
    tracer = trace.get_tracer("utilities")
    volume_ready = False
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
            time.sleep(retry_delay)

        if not volume_ready:
            err = get_blobfuse_error(access_name)
            error_msg = f"Mount point for {access_name} is not available. Blobfuse exited with error : {err}"

            if mount_failure_exception_class:
                ex = mount_failure_exception_class(error_msg)
            else:
                ex = Exception(error_msg)

            span.record_exception(ex)
            raise ex

    logger.info(f"Mount point for {access_name} is available")
    return get_mount_path(access_name)
