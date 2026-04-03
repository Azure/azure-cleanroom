import os

from cleanroom_internal.utilities import mountpoint_utilities

# Analytics-specific constants
BLOBFUSE_LAUNCHER_RETRIES = 10
BLOBFUSE_LAUNCHER_RETRY_DELAY = 30


def wait_for_mount_point(access_name) -> str:
    """Wait for a mount point to become available and return its path."""
    from analytics_workload.exceptions.custom_exceptions import (
        MountPointUnavailableFailure,
    )

    return mountpoint_utilities.wait_for_mount_point(
        access_name=access_name,
        max_retries=BLOBFUSE_LAUNCHER_RETRIES,
        retry_delay=BLOBFUSE_LAUNCHER_RETRY_DELAY,
        mount_failure_exception_class=MountPointUnavailableFailure,
    )


def events_enabled():
    return not ((os.environ.get("DISABLE_GOV_EVENTS") or "false").lower() == "true")
