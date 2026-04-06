import logging
import socket
import subprocess
import time

from opentelemetry import trace


def wait_for_services_readiness(logger, tracer, service_ports):
    max_retries = 18
    delay = 5
    attempt = 0

    for service_port in service_ports:
        with tracer.start_as_current_span(
            f"wait_for_services_readiness-{service_port}"
        ) as span:
            logger.info(f"Waiting for readiness of the service on port {service_port}")
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
