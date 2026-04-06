import argparse
import base64
import json
import logging
import os
import time
import traceback
from typing import Annotated, Optional

import kubernetes
import requests
from cleanroom_internal.utilities.otel_setup_utilities import TelemetryConfig
from cleanroom_internal.utilities.otel_utilities import extract_context_from_carrier
from cleanroom_internal.utilities.tracing_utilities import (
    create_span_context,
    trace_function,
)
from fastapi import FastAPI, HTTPException, Request
from fastapi.exceptions import RequestValidationError
from fastapi.params import Body
from fastapi.responses import JSONResponse
from kserve_inferencing_frontend import telemetry
from opentelemetry import context

from .clients.kubernetes_client import KubernetesClient
from .config.config_manager import ConfigManager
from .config.configuration import Configuration
from .exceptions.custom_exceptions import ResourceNotFound
from .models.input_models import JobInput, NodeType
from .telemetry.metrics import KServeFrontendMetrics, get_metrics
from .utilities import job_converters
from .utilities.constants import Constants

# Configure logger
logger = logging.getLogger("kserve-inferencing-frontend")

app = FastAPI()
k8s_client: KubernetesClient
config: Configuration
metrics_collector: KServeFrontendMetrics = get_metrics()


async def deploy_model(
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
        "deploy_model",
        {"job.id": job_id, "job.namespace": namespace},
    ):
        try:
            converter = job_converters.get(config)
            logger.info(f"Submitting inferencing model to Kubernetes: {job_id}")

            inference_svc_spec = converter.to_inference_svc_spec(
                job_id,
                job,
                telemetry_settings=(
                    config.service.telemetry if enable_telemetry_collection else None
                ),
            )

            # Sign the allow-all policy via governance sidecar.
            from .connectors.governance_connector import GovernanceHttpConnector

            signature = GovernanceHttpConnector.sign_policy(
                Constants.ALLOW_ALL_POLICY_BASE64
            )
            inference_svc_spec.spec.predictor.annotations = {
                "api-server-proxy.io/policy": Constants.ALLOW_ALL_POLICY_BASE64,
                "api-server-proxy.io/signature": signature,
            }

            k8s_client.submit_inference_service(
                job.model_name, namespace, inference_svc_spec.spec, tags
            )

            success = True
            return {"status": "success", "id": job.model_name}

        except Exception as e:
            logger.error(f"Failed to submit inference service {job_id}: {e}")
            raise
        finally:
            duration = time.time() - start_time
            metrics_collector.record_job_submission(
                success=success,
                duration=duration,
                namespace=namespace,
            )


# ---------------------------------------------------------------------------
# Integration test endpoints. These use KServe's native model/runtime spec
# with a public storageUri, bypassing the production containers-spec builder.
# They exist so that CI (test-cluster.ps1) can validate cluster infrastructure
# (KServe controller, kubelet proxy, flex node scheduling) without needing the
# full governance/agent/blobfuse pipeline.
# ---------------------------------------------------------------------------


async def deploy_test_model(
    name: str,
    job_id: str,
    namespace: str,
    node_type: Optional[NodeType] = None,
    host_network: Optional[bool] = None,
    signature: Optional[str] = None,
    tags: Optional[dict[str, str]] = None,
):
    global k8s_client
    global config

    start_time = time.time()
    success = False

    with create_span_context(
        "deploy_test_model",
        {"job.id": job_id, "job.namespace": namespace},
    ):
        try:
            logger.info(f"Submitting test inferencing model to Kubernetes: {job_id}")

            from kubernetes.client import models as k8smodels

            from .models.inference_service_models import (
                InferenceServiceSpec,
                PredictorSpec,
            )

            # Uses containers spec with an init container to download the
            # model — same pattern as the production builder.
            model_volume = "model-data"
            model_url = (
                "https://storage.googleapis.com/"
                "kfserving-examples/models/sklearn/1.0/model/model.joblib"
            )

            init_container = k8smodels.V1Container(
                name="model-download",
                image="busybox:1.36",
                command=["sh", "-c"],
                args=[
                    f"mkdir -p /mnt/models && "
                    f"wget -q -O /mnt/models/model.joblib {model_url}"
                ],
                volume_mounts=[
                    k8smodels.V1VolumeMount(name=model_volume, mount_path="/mnt/models")
                ],
            )

            container = k8smodels.V1Container(
                name="kserve-container",
                image="docker.io/kserve/sklearnserver:v0.17.0",
                args=["--model_name=" + name, "--model_dir=/mnt/models"],
                ports=[k8smodels.V1ContainerPort(container_port=8080, protocol="TCP")],
                volume_mounts=[
                    k8smodels.V1VolumeMount(name=model_volume, mount_path="/mnt/models")
                ],
            )

            predictor = PredictorSpec()
            predictor.initContainers = [init_container]
            predictor.containers = [container]
            predictor.volumes = [k8smodels.V1Volume(name=model_volume, empty_dir={})]

            if node_type is not None:
                if node_type == NodeType.flexnode:
                    if host_network is not None:
                        predictor.hostNetwork = host_network
                    predictor.nodeSelector = {"pod-policy": "required"}
                    predictor.tolerations = [
                        {
                            "key": "pod-policy",
                            "operator": "Equal",
                            "value": "required",
                            "effect": "NoSchedule",
                        }
                    ]

            spec = InferenceServiceSpec(predictor=predictor)

            annotations = None
            if signature is not None:
                annotations = {
                    "api-server-proxy.io/policy": Constants.ALLOW_ALL_POLICY_BASE64,
                    "api-server-proxy.io/signature": signature,
                }

            k8s_client.submit_inference_service(
                name, namespace, spec, tags, annotations
            )

            success = True
            return {"status": "success", "id": name}

        except Exception as e:
            logger.error(f"Failed to submit inference service {job_id}: {e}")
            raise
        finally:
            duration = time.time() - start_time
            metrics_collector.record_job_submission(
                success=success,
                duration=duration,
                namespace=namespace,
            )


@app.middleware("http")
async def telemetry_middleware(request: Request, call_next):
    """Middleware to add telemetry for all HTTP requests and extract trace context"""
    start_time = time.time()
    token = None
    carrier = {key: value for key, value in request.headers.items()}
    extracted_context = extract_context_from_carrier(carrier)
    if extracted_context:
        token = context.attach(extracted_context)
    try:
        response = await call_next(request)

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
        if token:
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


@app.post("/inferencing/deployModel")
async def deploy_inferencing_model(
    job: JobInput,
    job_id: Annotated[str, Body(alias="jobId")] = "",
    enable_telemetry_collection: Annotated[
        bool, Body(alias="enableTelemetryCollection")
    ] = True,
):
    global config
    job_id = job_id or f"{int(time.time())}"
    tags = {"job_type": "inferencing"}
    try:
        return await deploy_model(
            job_id,
            job,
            config.applications.inferencing.namespace,
            enable_telemetry_collection=enable_telemetry_collection,
            tags=tags,
        )
    except Exception as e:
        logger.error(
            f"Failed to create inferencing service: {e},  traceback: {traceback.format_exc()}"
        )
        raise HTTPException(
            status_code=500,
            detail=f"Failed to create inferencing service: {e}",
        )


@app.post("/inferencing/test/deployModel")
async def deploy_inferencing_test_model(
    model_name: Annotated[str, Body(alias="modelName")],
    node_type: Annotated[Optional[NodeType], Body(alias="nodeType")] = None,
    host_network: Annotated[Optional[bool], Body(alias="hostNetwork")] = None,
    signature: Annotated[Optional[str], Body(alias="signature")] = None,
):
    """Integration test endpoint only. Deploys a hardcoded sklearn model
    using KServe's native model/runtime spec with a public storageUri."""
    global config

    job_id = f"{int(time.time())}"
    tags = {"job_type": "inferencing"}
    try:
        return await deploy_test_model(
            model_name,
            job_id,
            config.applications.inferencing.namespace,
            node_type=node_type,
            host_network=host_network,
            signature=signature,
            tags=tags,
        )
    except Exception as e:
        logger.error(
            f"Failed to create inferencing service: {e},  traceback: {traceback.format_exc()}"
        )
        raise HTTPException(
            status_code=500,
            detail=f"Failed to create inferencing service: {e}",
        )


@app.post("/inferencing/test/generateSecurityPolicy")
async def get_inferencing_test_policy(node_type: NodeType):
    """Integration test endpoint only. Returns an allow-all policy for
    flex node testing without requiring the full containers-spec builder."""
    if node_type != NodeType.flexnode:
        raise HTTPException(
            status_code=400,
            detail="Security policy generation is not supported for "
            "non-flexnode node type.",
        )

    return {
        "predictor": {
            "jsonBase64": Constants.ALLOW_ALL_POLICY_BASE64,
        },
    }


@app.get("/inferencing/status/{model_name}")
async def get_status(model_name: str):
    global config
    try:
        inference_svc = k8s_client.get_inference_service(
            model_name, config.applications.inferencing.namespace
        )
        if not inference_svc.status:
            logger.warning(
                f"No status field found for model {model_name}. Returning url as empty."
            )
            job_status = {"url": ""}

            return {"id": model_name, "status": job_status}

        job_status = inference_svc.status
        return {"id": model_name, "status": job_status}

    except ResourceNotFound as e:
        logger.error(f"Job with ID {model_name} not found.")
        raise HTTPException(
            status_code=404,
            detail=f"Job with ID {model_name} not found",
        )
    except Exception as e:
        logger.error(
            f"Failed to get model status: {e}, traceback: {traceback.format_exc()}"
        )
        raise HTTPException(
            status_code=500,
            detail=f"Failed to get model status: {e}",
        )


@app.post("/inferencing/generateSecurityPolicy")
async def get_inferencing_service_policy(
    job: JobInput,
    enable_telemetry_collection: Annotated[
        bool, Body(alias="enableTelemetryCollection")
    ] = True,
):
    global config

    job_id = f"{int(time.time())}"
    converter = job_converters.get(config)
    try:
        inference_svc_spec = converter.to_inference_svc_spec(
            job_id,
            job,
            telemetry_settings=(
                config.service.telemetry if enable_telemetry_collection else None
            ),
        )
        return {
            "predictor": {
                "jsonBase64": inference_svc_spec.predictor_policy.json_base64,
                "pcrs": inference_svc_spec.predictor_policy.pcrs,
            },
            "transformer": {
                "jsonBase64": inference_svc_spec.transformer_policy.json_base64,
                "pcrs": inference_svc_spec.transformer_policy.pcrs,
            },
        }
    except Exception as e:
        logger.error(
            f"Failed to get inferencing pod policy: {e}, traceback: {traceback.format_exc()}"
        )
        raise HTTPException(
            status_code=500,
            detail=f"Failed to get inferencing pod policy: {e}",
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
        report = get_report(report_data_bytes)
    else:
        platform = "virtual"
        report = None

    return {
        "platform": platform,
        "report": report,
        "reportDataPayload": report_data_payload,
    }


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
    url = "http://localhost:8284/attest/combined"
    runtime_data_b64 = base64.b64encode(report_data).decode("utf-8")
    payload = {"runtime_data": runtime_data_b64}
    response = requests.post(url, json=payload)
    response.raise_for_status()
    data = response.json()
    return {
        "attestation": data.get("evidence"),
        "platformCertificates": data.get("endorsements"),
        "uvmEndorsements": data.get("uvm_endorsements"),
    }


def main():
    import uvicorn

    global config
    global k8s_client

    args = parse_args()
    log_args(args)
    # Load configuration from config.yaml
    config_manager = ConfigManager(config_file=args.config)
    config = config_manager.get_config()
    logger.info(f"Loaded configuration: {config}")

    # Setup telemetry before starting the server.
    telemetry_config = TelemetryConfig(
        service_name=config.service.name,
        is_otel_enabled=config.service.telemetry.telemetry_collection_enabled,
        pod_name=os.getenv("POD_NAME", "unknown"),
        namespace=config.service.namespace,
    )
    telemetry_config.setup_telemetry()
    telemetry_config.instrument_requests()
    telemetry_config.instrument_fastapi(app)

    k8s_client = KubernetesClient(
        kubeconfig_path=args.kubeconfig,
        resource_settings=config.kserve.resource,
    )

    uvicorn.run(app, host="0.0.0.0", port=args.port, log_level="debug")
