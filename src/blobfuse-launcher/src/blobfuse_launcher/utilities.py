import logging
import os

from cleanroom_internal.utilities import utilities as internal_utilities
from opentelemetry import trace
from ruamel.yaml import YAML


def launch_blobfuse(
    logger: logging.Logger,
    tracer: trace.Tracer,
    mountPath: str,
    readOnly: bool,
    subdirectory: str,
    useAdls: bool,
    cpkEnabled: bool,
    disableWritebackCache: bool,
    blockSizeMB: int,
    telemetryPath: str,
) -> int:
    with tracer.start_as_current_span("launch_blobfuse") as span:

        proc = internal_utilities.subprocess_launch(
            logger,
            tracer,
            "blobfuse-mount",
            [
                "blobfuse2",
                "mount",
                mountPath,
                "--allow-other",
                "--read-only=" + str(readOnly).lower(),
                f"--cpk-enabled=" + str(cpkEnabled).lower(),
                "--virtual-directory=true",
                "--block-cache",
                "--block-cache-path",
                "/tmp/blobfuse_tmp",
                "--block-cache-block-size=" + str(blockSizeMB),
                "--block-cache-pool-size=256",
                # Setting prefetch explicitly to 0. Otherwise, blobfuse tries to determine prefetch using
                # the container CPUs and memory size instead of honoring the mem-size-mb limits and fails to start.
                "--block-cache-prefetch=0",
                # Due to the memory limit of 50 GB for CACI, we need to ensure that the total
                # disk space used by all blobfuse processes does not exceed 50 GB.
                # Otherwise, blobfuse might fail with a "transport endpoint disconnected" error.
                "--block-cache-disk-size=4096",
                "--log-file-path",
                f"{telemetryPath}/infrastructure/{os.path.basename(mountPath)}-blobfuse.log",
                "--log-level=LOG_INFO",
                "--subdirectory",
                f"{subdirectory}",
                "--use-adls=" + str(useAdls).lower(),
                "--disable-writeback-cache=" + str(disableWritebackCache).lower(),
            ],
        )
        return proc.returncode


def launch_blobfuse_encrypted(
    logger: logging.Logger,
    tracer: trace.Tracer,
    mountPath: str,
    readOnly: bool,
    telemetryPath: str,
) -> int:
    with tracer.start_as_current_span("launch_blobfuse") as span:

        # To allow encryptor component we need config file for blobfuse mount.
        yaml = YAML()
        with open("encryptor-config.yaml", "r") as file:
            config = yaml.load(file)

        config_details = {
            "block_cache": {"block-size-mb": 16},
            "encryptor": {
                "block-size-mb": 16,
                "encrypted-mount-path": f"{mountPath}-plain/",
            },
        }
        config["block_cache"].update(config_details["block_cache"])
        config["encryptor"].update(config_details["encryptor"])

        with open("config.yaml", "w") as file:
            yaml.dump(config, file)

        proc = internal_utilities.subprocess_launch(
            logger,
            tracer,
            "blobfuse-mount",
            [
                "blobfuse2",
                "mount",
                mountPath,
                "--config-file=config.yaml",
                "--read-only=" + str(readOnly).lower(),
                "--log-file-path",
                f"{telemetryPath}/infrastructure/{os.path.basename(mountPath)}-blobfuse.log",
            ],
        )

        return proc.returncode


def read_blobfuse_logs(logger: logging.Logger, mountPath: str, telemetryPath: str):
    log_file_path = (
        f"{telemetryPath}/infrastructure/{os.path.basename(mountPath)}-blobfuse.log"
    )
    try:
        with open(log_file_path, "r") as log_file:
            logs = log_file.read()
            logger.info(f"Blobfuse logs from {log_file_path}:\n{logs}")
    except Exception as e:
        logger.error(f"Failed to read blobfuse logs from {log_file_path}. Error: {e}")
