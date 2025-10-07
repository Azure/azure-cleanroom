import base64
import hashlib
import json
import logging
import os
import socket
import subprocess
import time

import requests
from opentelemetry import trace
from opentelemetry.trace.propagation.tracecontext import TraceContextTextMapPropagator
from ruamel.yaml import YAML


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


def unwrap_secret(
    logger: logging.Logger,
    tracer: trace.Tracer,
    secrets_port: str,
    client_id: str,
    tenant_id: str,
    kid: str,
    akv_endpoint: str,
    kek_kid: str,
    kek_akv_endpoint: str,
    maa_endpoint: str,
) -> bytes:
    with tracer.start_as_current_span("unwrap_secret") as span:
        try:
            response = requests.post(
                f"http://localhost:{secrets_port}/secrets/unwrap",
                headers={"Content-Type": "application/json"},
                data=json.dumps(
                    {
                        "clientId": client_id,
                        "tenantId": tenant_id,
                        "kid": kid,
                        "akvEndpoint": akv_endpoint,
                        "kek": {
                            "kid": kek_kid,
                            "akvEndpoint": kek_akv_endpoint,
                            "maaEndpoint": maa_endpoint,
                        },
                    }
                ),
            )
            response.raise_for_status()
        except Exception as e:
            logger.error(
                f"Failed to unwrap secret {kid} via secrets sidecar. Error: {e}"
            )
            span.set_status(
                status=trace.StatusCode.ERROR,
                description=f"Failed to unwrap secret {kid} via secrets sidecar.",
            )
            span.record_exception(e)
            raise e
        else:
            result = response.json()
            encodedSecret = result["value"]
            secret = base64.b64decode(encodedSecret)
            return secret


def get_cgs_secret(
    logger: logging.Logger,
    tracer: trace.Tracer,
    governance_port: str,
    kid: str,
) -> bytes:
    with tracer.start_as_current_span("get_secret") as span:
        try:
            response = requests.post(
                f"http://localhost:{governance_port}/secrets/{kid}"
            )
            response.raise_for_status()
        except Exception as e:
            logger.error(
                f"Failed to get secret {kid} via governance sidecar. Error: {e}"
            )
            span.set_status(
                status=trace.StatusCode.ERROR,
                description=f"Failed to get secret {kid} via governance sidecar.",
            )
            span.record_exception(e)
            raise e
        else:
            result = response.json()
            encodedSecret = result["value"]
            secret = base64.b64decode(encodedSecret)
            return secret


def wait_for_services_readiness(logger, tracer, service_ports):
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
                span.set_status(
                    status=trace.StatusCode.ERROR,
                    description=f"Service on port {service_port} is not available even after waiting for the threshold.",
                )
                span.record_exception(ex)
                raise ex


def subprocess_launch(
    logger: logging.Logger,
    tracer: trace.Tracer,
    operationName: str,
    command: list[str],
    wait_for_completion: bool = True,
):
    with tracer.start_as_current_span(operationName) as span:
        # wait for sidecar to be available
        logger.info(f"Launching subprocess {operationName} with command {command}.")
        try:
            process = subprocess.Popen(
                command, stdout=subprocess.PIPE, stderr=subprocess.PIPE
            )
            if wait_for_completion:
                process.wait()
                logger.info(
                    f"Subprocess {operationName} exitCode: {process.returncode}"
                )
            return process
        except subprocess.SubprocessError as e:
            logger.error(
                f"Failed to launch subprocess {operationName} with command {command}."
                + f"Error: {e}"
            )
            span.set_status(
                status=trace.StatusCode.ERROR,
                description=f"Failed to launch subprocess {operationName} with command {command}.",
            )
            span.record_exception(e)
            raise e


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

        proc = subprocess_launch(
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

        proc = subprocess_launch(
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
