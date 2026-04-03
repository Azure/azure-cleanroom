import logging
import os

from cleanroom_internal.utilities import utilities as internal_utilities
from opentelemetry import trace


def launch_s3fs(
    logger: logging.Logger,
    tracer: trace.Tracer,
    bucketName: str,
    mountPath: str,
    readOnly: bool,
    awsUrl: str,
    usePathRequestStyle: bool,
    telemetryPath: str,
) -> int:
    with tracer.start_as_current_span("launch_s3fs") as span:
        command = [
            "s3fs",
            bucketName,
            mountPath,
            "-o",
            f"logfile={telemetryPath}/infrastructure/{os.path.basename(mountPath)}-s3fs.log",
            "-o",
            f"url={awsUrl}",
            "-o",
            "allow_other",
        ]
        if readOnly:
            command.append("-o")
            command.append("ro")
            command.append("-o")
            command.append("umask=022")
        if usePathRequestStyle:
            command.append("-o")
            command.append("use_path_request_style")

        proc = internal_utilities.subprocess_launch(
            logger,
            tracer,
            "s3fs-mount",
            command,
        )
        return proc.returncode
