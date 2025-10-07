import base64
import json
import logging
import os
import socket
import subprocess
import time

import requests
from opentelemetry import trace
from opentelemetry.trace.propagation.tracecontext import TraceContextTextMapPropagator


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


def get_cgs_secret(
    logger: logging.Logger,
    tracer: trace.Tracer,
    governance_port: str,
    kid: str,
) -> str:
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
            secret = result["value"]
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

        proc = subprocess_launch(
            logger,
            tracer,
            "s3fs-mount",
            command,
        )
        return proc.returncode
