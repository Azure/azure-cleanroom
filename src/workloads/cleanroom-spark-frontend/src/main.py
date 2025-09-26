import argparse
import base64
import hashlib
import json
import logging
import os
import time
import traceback
from base64 import b64decode
from typing import Annotated, Optional

import attestation_container_pb2 as attestation_container
import attestation_container_pb2_grpc as attestation_container_grpc
import grpc
import kubernetes
from fastapi import FastAPI, HTTPException, Request
from fastapi.exceptions import RequestValidationError
from fastapi.params import Body
from fastapi.responses import JSONResponse
from opentelemetry import context
from opentelemetry.trace.propagation.tracecontext import TraceContextTextMapPropagator
from src.clients.kubernetes_client import KubernetesClient
from src.config.config_manager import ConfigManager
from src.config.configuration import Configuration
from src.exceptions.custom_exceptions import ResourceNotFound
from src.models.input_models import JobInput, SQLJobInput
from src.models.spark_application_models import ApplicationStateEnum, SparkApplication
from src.telemetry.metrics import SparkFrontendMetrics, get_metrics
from src.telemetry.otel_config import TelemetryConfig
from src.telemetry.tracing import create_span_context, trace_function
from src.utilities import job_converters
from src.utilities.constants import Constants
from src.utilities.job_converters import SparkJobProviderType

# Configure logger
logger = logging.getLogger("cleanroom-spark-frontend")

app = FastAPI()
k8s_client: KubernetesClient
config: Configuration
metrics_collector: SparkFrontendMetrics = get_metrics()


def extract_context_from_request(headers: list[tuple[str, str]]):
    carrier = {key: value for key, value in headers}

    # Extract context from headers
    extracted_context = TraceContextTextMapPropagator().extract(carrier)
    return extracted_context


async def submit_job(
    provider_type: SparkJobProviderType,
    job_id: str,
    job: JobInput,
    namespace: str,
    enable_telemetry_collection: bool = True,
    tags: Optional[dict[str, str]] = None,
):
    global k8s_client
    global config

    start_time = time.time()
    success = False

    with create_span_context(
        "submit_job",
        {"job.id": job_id, "job.type": provider_type, "job.namespace": namespace},
    ):
        try:
            converter = job_converters.get(provider_type, config)
            logger.info(f"Submitting spark job to Kubernetes: {job_id}")

            spark_spec = converter.to_spark_spec(
                job_id,
                job,
                telemetry_settings=(
                    config.service.telemetry if enable_telemetry_collection else None
                ),
            )
            k8s_client.submit_job(spark_spec.spec.name, namespace, spark_spec, tags)

            success = True
            return {"status": "success", "id": spark_spec.spec.name}

        except Exception as e:
            logger.error(f"Failed to submit job {job_id}: {e}")
            raise
        finally:
            duration = time.time() - start_time
            metrics_collector.record_job_submission(
                job_type=provider_type,
                success=success,
                duration=duration,
                namespace=namespace,
            )


@app.middleware("http")
async def telemetry_middleware(request: Request, call_next):
    """Middleware to add telemetry for all HTTP requests and extract trace context"""
    start_time = time.time()

    extracted_context = extract_context_from_request(request.headers.items())
    token = context.attach(extracted_context)
    try:
        logger.info(f"Incoming request: {request.method} {request.url.path}")
        logger.info(f"Request headers: {request.headers}")

        # Process request
        response = await call_next(request)

        # Record metrics
        duration = time.time() - start_time
        metrics_collector.record_http_request(
            method=request.method,
            path=request.url.path,
            status_code=response.status_code,
            duration=duration,
        )

        return response
    finally:
        logger.info(
            f"Request processing completed in {time.time() - start_time:.2f} seconds"
        )
        context.detach(token)


@app.middleware("http")
async def log_requests(request: Request, call_next):
    logger.info(f"Incoming request: {request.method} {request.url.path}")
    logger.info(f"Request headers: {request.headers}")
    return await call_next(request)


@app.exception_handler(RequestValidationError)
async def validation_exception_handler(request: Request, exc: RequestValidationError):
    logger.error(f"Validation error for request: {await request.body()}")
    logger.error(f"Request headers: {request.headers}")
    logger.error(f"Error details: {exc.errors()}")

    return JSONResponse(
        status_code=422,
        content={
            "message": "Validation Failed",
            "errors": exc.errors(),
        },
    )


@app.exception_handler(kubernetes.client.ApiException)
async def kubernetes_error_handler(
    request: Request, exc: kubernetes.client.ApiException
):
    logger.error(f"An error occurred: {repr(exc)}")
    status_code = exc.status if exc.status else 500
    # TODO (HPrabh): Add more specific error handling based on the individual errors.
    return JSONResponse(
        status_code=status_code,
        content={
            "message": f"An error occurred while processing {request.url.path}",
            "error": f"{repr(exc)}",
            "details": f"{exc}",
        },
    )


@app.post("/analytics/submitSqlJob")
async def submit_sql_job(
    job: SQLJobInput,
    job_id: Annotated[str, Body(alias="jobId")] = "",
    enable_telemetry_collection: Annotated[
        bool, Body(alias="enableTelemetryCollection")
    ] = True,
):
    global config

    job_id = job_id or f"{int(time.time())}"
    tags = {"job_type": "sql"}
    try:
        return await submit_job(
            SparkJobProviderType.SQL,
            job_id,
            job,
            config.applications.analytics.namespace,
            enable_telemetry_collection=enable_telemetry_collection,
            tags=tags,
        )
    except Exception as e:
        logger.error(
            f"Failed to submit SQL job: {e},  traceback: {traceback.format_exc()}"
        )
        raise HTTPException(
            status_code=500,
            detail=f"Failed to submit SQL job: {e}",
        )


@app.post("/analytics/generateSecurityPolicy")
async def get_sql_job_policy(
    job: SQLJobInput,
    enable_telemetry_collection: Annotated[
        bool, Body(alias="enableTelemetryCollection")
    ] = True,
):
    global config

    job_id = f"{int(time.time())}"
    converter = job_converters.get(SparkJobProviderType.SQL, config)
    try:
        spark_spec = converter.to_spark_spec(
            job_id,
            job,
            config.service.telemetry if enable_telemetry_collection else None,
        )
        return {
            "driver": {
                "rego": spark_spec.driver_policy.rego,
                "regoBase64": spark_spec.driver_policy.rego_base64,
                "hostData": spark_spec.driver_policy.host_data,
            },
            "executor": {
                "rego": spark_spec.executor_policy.rego,
                "regoBase64": spark_spec.executor_policy.rego_base64,
                "hostData": spark_spec.executor_policy.host_data,
            },
        }
    except Exception as e:
        logger.error(
            f"Failed to get spark pod policy: {e}, traceback: {traceback.format_exc()}"
        )
        raise HTTPException(
            status_code=500,
            detail=f"Failed to get spark pod policy: {e}",
        )


@app.post("/analytics/submitPiJob")
async def submit_pi_job():
    global config
    import time

    job_id = f"spark-pi-python-{int(time.time())}"
    tags = {"job_type": "pi"}
    job = JobInput(
        contractId=job_id,
        datasets=[],
        datasink=None,
        governance=None,
    )
    try:
        return await submit_job(
            SparkJobProviderType.PI,
            job_id,
            job,
            config.applications.analytics.namespace,
            enable_telemetry_collection=True,
            tags=tags,
        )
    except Exception as e:
        logger.error(
            f"Failed to submit PI job: {e}, traceback: {traceback.format_exc()}"
        )
        raise HTTPException(
            status_code=500,
            detail=f"Failed to submit PI job: {e}",
        )


@app.get("/analytics/status/{job_id}")
async def get_status(job_id: str):
    global config
    try:
        spark_app = k8s_client.get_spark_app(
            job_id, config.applications.analytics.namespace
        )
        if not spark_app.status:
            logger.warning(
                f"No status field found for job {job_id}. Returning state as UNKNOWN."
            )
            job_status = {"applicationState": {"state": "UNKNOWN"}}

            return {"id": job_id, "status": job_status}

        job_status = spark_app.status
        if job_status.applicationState.state in [
            ApplicationStateEnum.Completed,
            ApplicationStateEnum.Failed,
        ]:
            job_type = (
                "unknown"
                if spark_app.metadata.labels == None
                else spark_app.metadata.labels.get("job_type", "unknown")
            )
            if (
                job_status.terminationTime is not None
                and job_status.lastSubmissionAttemptTime is not None
            ):
                duration = (
                    job_status.terminationTime - job_status.lastSubmissionAttemptTime
                ).total_seconds()
            else:
                duration = 0
            metrics_collector.record_job_completion(
                job_type=job_type,
                success=(
                    job_status.applicationState.state == ApplicationStateEnum.Completed
                ),
                duration=duration,
            )
        return {"id": job_id, "status": job_status}

    except ResourceNotFound as e:
        logger.error(f"Job with ID {job_id} not found.")
        raise HTTPException(
            status_code=404,
            detail=f"Job with ID {job_id} not found",
        )
    except Exception as e:
        logger.error(
            f"Failed to get job status: {e}, traceback: {traceback.format_exc()}"
        )
        raise HTTPException(
            status_code=500,
            detail=f"Failed to get job status: {e}",
        )


@app.get("/ready")
async def is_ready():
    return {"status": "up"}


@app.get("/report")
async def getFrontendReport():
    service_cert_location = os.environ.get(
        "SERVICE_CERT_LOCATION", "/app/service/service-cert.pem"
    )
    if not os.path.exists(service_cert_location):
        logger.error(f"Service cert file not found at {service_cert_location}")
        raise HTTPException(
            status_code=404,
            detail=f"Service cert file not found at {service_cert_location}",
        )

    with open(service_cert_location, "r") as f:
        service_cert = f.read()

    report_data_content = {"serviceCert": service_cert}
    report_data_bytes = bytes(json.dumps(report_data_content), "utf-8")
    report_data_payload = base64.b64encode(report_data_bytes).decode("utf-8")

    if isSevSnp():
        platform = "snp"
        report_data_hash = hashlib.sha256(report_data_bytes).digest()
        report = get_report(report_data_hash)
    else:
        platform = "virtual"
        report = None

    return {
        "platform": platform,
        "report": report,
        "reportDataPayload": report_data_payload,
    }


@app.post("/mutate")
async def mutate(request: Request):
    body = await request.json()
    req = body.get("request", {})
    uid = req.get("uid")
    pod = req.get("object", {})

    if not pod:
        logger.error("Pod object is missing in the mutation request.")
        return JSONResponse(
            content={
                "apiVersion": "admission.k8s.io/v1",
                "kind": "AdmissionReview",
                "response": {
                    "uid": uid,
                    "allowed": False,
                    "status": {
                        "code": 400,
                        "message": "Pod object is missing in the mutation request.",
                    },
                },
            }
        )

    pod_labels = pod.get("metadata", {}).get("labels", {})
    pod_annotations = pod.get("metadata", {}).get("annotations", {})
    pod_name = pod.get("metadata", {}).get("name", None)

    if not pod_name:
        logger.error("Pod name is missing in the mutation request.")
        return JSONResponse(
            content={
                "apiVersion": "admission.k8s.io/v1",
                "kind": "AdmissionReview",
                "response": {
                    "uid": uid,
                    "allowed": False,
                    "status": {
                        "code": 400,
                        "message": "Pod name is missing in the mutation request.",
                    },
                },
            }
        )
    logger.info(f"Received mutation request with UID: {uid}, name: {pod_name}")

    cce_policy_map_base64 = pod_annotations.get(
        Constants.CCE_POLICY_CONFIG_MAP_ANNOTATION, None
    )
    if not cce_policy_map_base64:
        logger.error(
            f"Pod: {pod_name} does not have '{Constants.CCE_POLICY_CONFIG_MAP_ANNOTATION}' annotation. Failing mutation."
        )
        return JSONResponse(
            content={
                "apiVersion": "admission.k8s.io/v1",
                "kind": "AdmissionReview",
                "response": {
                    "uid": uid,
                    "allowed": False,
                    "status": {
                        "code": 400,
                        "message": f"Pod does not have '{Constants.CCE_POLICY_CONFIG_MAP_ANNOTATION}' annotation.",
                    },
                },
            }
        )

    try:
        cce_policy_map = json.loads(b64decode(cce_policy_map_base64).decode("utf-8"))
        cce_policy_map_name = cce_policy_map.get("name", None)
        cce_policy_map_namespace = cce_policy_map.get("namespace", None)
        if not cce_policy_map_name or not cce_policy_map_namespace:
            logger.error(
                f"ConfigMap information for pod: {pod_name} is incomplete: {json.dumps(cce_policy_map)} . Failing mutation."
            )
            return JSONResponse(
                content={
                    "apiVersion": "admission.k8s.io/v1",
                    "kind": "AdmissionReview",
                    "response": {
                        "uid": uid,
                        "allowed": False,
                        "status": {
                            "code": 400,
                            "message": f"ConfigMap information is incomplete: {json.dumps(cce_policy_map)}.",
                        },
                    },
                }
            )

        cce_policy = k8s_client.get_config_map(
            cce_policy_map_name, cce_policy_map_namespace
        )

        policy = cce_policy.data.get(Constants.CCE_POLICY_CONFIG_MAP_POLICY_KEY, None)
        if not policy:
            logger.error(
                f"ConfigMap {cce_policy_map_name} in namespace {cce_policy_map_namespace} for pod: {pod_name} "
                + f"does not contain '{Constants.CCE_POLICY_CONFIG_MAP_POLICY_KEY}' key. Failing mutation."
            )
            return JSONResponse(
                content={
                    "apiVersion": "admission.k8s.io/v1",
                    "kind": "AdmissionReview",
                    "response": {
                        "uid": uid,
                        "allowed": False,
                        "status": {
                            "code": 400,
                            "message": f"ConfigMap {cce_policy_map_name} in namespace {cce_policy_map_namespace} "
                            + f"does not contain '{Constants.CCE_POLICY_CONFIG_MAP_POLICY_KEY}' key.",
                        },
                    },
                }
            )

        cce_policy_annotation_name = pod_labels.get(
            Constants.CCE_POLICY_ANNOTATION_NAME_LABEL, ""
        )
        if not cce_policy_annotation_name:
            logger.error(
                f"Pod {pod_name} does not have '{Constants.CCE_POLICY_ANNOTATION_NAME_LABEL}' label. Failing mutation."
            )
            return JSONResponse(
                content={
                    "apiVersion": "admission.k8s.io/v1",
                    "kind": "AdmissionReview",
                    "response": {
                        "uid": uid,
                        "allowed": False,
                        "status": {
                            "code": 400,
                            "message": f"Pod does not have '{Constants.CCE_POLICY_ANNOTATION_NAME_LABEL}' label.",
                        },
                    },
                }
            )

        patch = [
            {
                "op": "add",
                "path": f"/metadata/annotations/{cce_policy_annotation_name}",
                "value": policy,
            }
        ]

        patch_bytes = json.dumps(patch).encode()
        patch_base64 = base64.b64encode(patch_bytes).decode()

        logger.info(
            f"Patching pod {pod_name} with cce policy from config map: {cce_policy_map}"
        )
        return JSONResponse(
            content={
                "apiVersion": "admission.k8s.io/v1",
                "kind": "AdmissionReview",
                "response": {
                    "uid": uid,
                    "allowed": True,
                    "patchType": "JSONPatch",
                    "patch": patch_base64,
                },
            }
        )
    except kubernetes.client.ApiException as e:
        logger.error(f"Failed to mutate resource. Error: {e}")
        logger.error(f"Traceback: {traceback.format_exc()}")
        return JSONResponse(
            content={
                "apiVersion": "admission.k8s.io/v1",
                "kind": "AdmissionReview",
                "response": {
                    "uid": uid,
                    "allowed": False,
                    "status": {
                        "code": e.status if e.status else 500,
                        "message": f"Failed to mutate resource. Error: {e}",
                    },
                },
            }
        )


def parse_args():
    parser = argparse.ArgumentParser(description="Run the FastAPI server.")
    parser.add_argument(
        "--port", type=int, default=8000, help="Port to run the server on"
    )
    parser.add_argument(
        "--kubeconfig",
        type=str,
        required=False,
        help="Path to the kubeconfig file",
    )
    parser.add_argument(
        "--config",
        type=str,
        default="config.yaml",
        help="Path to the configuration file",
    )
    return parser.parse_args()


def log_args(args):
    logger.info(f"Starting server with arguments: {args}")


def isSevSnp():
    return os.environ.get("INSECURE_VIRTUAL_ENVIRONMENT") != "true"


def get_report(report_data: bytes):
    attestation_request = attestation_container.FetchAttestationRequest()
    attestation_request.report_data = report_data
    with grpc.insecure_channel("unix:/mnt/uds/sock") as channel:
        stub = attestation_container_grpc.AttestationContainerStub(channel)
        response = stub.FetchAttestation(attestation_request)
    return {
        "attestation": base64.b64encode(response.attestation).decode("utf-8"),
        "platformCertificates": base64.b64encode(response.platform_certificates).decode(
            "utf-8"
        ),
        "uvmEndorsements": base64.b64encode(response.uvm_endorsements).decode("utf-8"),
    }


@trace_function()
def wait_for_file(file_path: str):
    timeout_seconds = 60
    interval_seconds = 5
    elapsed = 0
    logger.info(f"Waiting for file at {file_path}")
    while elapsed < timeout_seconds:
        if os.path.exists(file_path):
            return
        logger.warning(
            f"File not found at {file_path}. Waiting for {interval_seconds} seconds..."
        )
        time.sleep(interval_seconds)
        elapsed += interval_seconds
    logger.error(f"File not found at {file_path} after {timeout_seconds} seconds.")
    raise FileNotFoundError(
        f"File not found at {file_path} after {timeout_seconds} seconds."
    )


if __name__ == "__main__":
    import uvicorn

    args = parse_args()
    log_args(args)
    # Load configuration from config.yaml
    config_manager = ConfigManager(config_file=args.config)
    config = config_manager.get_config()
    logger.info(f"Loaded configuration: {config}")

    # Setup telemetry before starting the server.
    telemetry_config = TelemetryConfig(config=config)
    telemetry_config.setup_telemetry()
    telemetry_config.instrument_fastapi(app)

    k8s_client = KubernetesClient(
        kubeconfig_path=args.kubeconfig,
        spark_resource_settings=config.spark.resource,
    )

    # Sometimes ccr-proxy takes a while to generate the service certificate.
    # Wait for it to be available before starting the server.
    service_cert_location = os.environ.get(
        "SERVICE_CERT_LOCATION", "/app/service/service-cert.pem"
    )
    wait_for_file(service_cert_location)
    k8s_client.create_mutating_webhook_configuration(
        config.service, service_cert_location
    )

    uvicorn.run(app, host="0.0.0.0", port=args.port, log_level="debug")
