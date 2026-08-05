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
from fastapi import FastAPI, HTTPException, Request
from fastapi.exceptions import RequestValidationError
from fastapi.params import Body
from fastapi.responses import JSONResponse
from opentelemetry import context

from cleanroom_internal.utilities.otel_setup_utilities import TelemetryConfig
from cleanroom_internal.utilities.otel_utilities import extract_context_from_carrier
from cleanroom_internal.utilities.tracing_utilities import (
    create_span_context,
)

from .clients.kubernetes_client import KubernetesClient
from .config.config_manager import ConfigManager
from .config.configuration import Configuration
from .exceptions.custom_exceptions import ResourceNotFound
from .models.input_models import JobInput
from .telemetry.metrics import KServeFrontendMetrics, get_metrics
from .utilities import job_converters

# Configure logger
logger = logging.getLogger("kserve-inferencing-frontend")

app = FastAPI()
k8s_client: KubernetesClient
config: Configuration
metrics_collector: KServeFrontendMetrics = get_metrics()

_kserve_agent_digest_patched = False


def _ensure_kserve_agent_digest_pinned(cfg: Configuration):
    """Patch the KServe agent image in the ConfigMap once with a digest-pinned
    reference. Subsequent calls are no-ops."""
    global _kserve_agent_digest_patched
    if _kserve_agent_digest_patched:
        return

    try:
        from .builders.inference_service_builder import resolve_kserve_agent_image

        agent_image = resolve_kserve_agent_image(cfg.cleanroom)
        if agent_image:
            k8s_client.patch_kserve_agent_image(agent_image)
            _kserve_agent_digest_patched = True
    except Exception as e:
        logger.warning(f"Failed to pin KServe agent digest: {e}")


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

            # Ensure the KServe agent sidecar image is pinned by digest in the
            # inferenceservice-config ConfigMap. This is a no-op if already set.
            _ensure_kserve_agent_digest_pinned(config)

            # Reuse existing predictor annotations when the policy is unchanged
            # to avoid an unnecessary KServe rolling update on redeploy.
            from .connectors.governance_connector import GovernanceHttpConnector

            existing_spec = k8s_client.get_existing_inference_service_spec(
                job.model_name, namespace
            )
            existing_annotations = (
                (existing_spec or {}).get("predictor", {}).get("annotations")
            )

            policy_base64 = inference_svc_spec.predictor_policy.json_base64

            if (
                existing_annotations
                and existing_annotations.get("api-server-proxy.io/policy")
                == policy_base64
            ):
                logger.info(
                    f"Reusing existing predictor annotations for "
                    f"'{job.model_name}' (policy unchanged)."
                )
                inference_svc_spec.spec.predictor.annotations = existing_annotations
            else:
                signature = GovernanceHttpConnector.sign_policy(policy_base64)
                inference_svc_spec.spec.predictor.annotations = {
                    "api-server-proxy.io/policy": policy_base64,
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


def _is_inference_service_ready(status) -> bool:
    """Check if a KServe InferenceService status indicates
    readiness. The status can be a pydantic model or a dict."""
    if not status:
        return False
    # Handle both pydantic model and dict.
    if isinstance(status, dict):
        url = status.get("url") or status.get("address", {}).get("url")
        conditions = status.get("conditions", [])
    else:
        url = getattr(status, "url", None) or getattr(
            getattr(status, "address", None), "url", None
        )
        conditions = getattr(status, "conditions", None) or []
    if not url:
        return False
    for c in conditions:
        if isinstance(c, dict):
            c_type, c_status = c.get("type"), c.get("status")
        else:
            c_type = getattr(c, "type", None)
            c_status = getattr(c, "status", None)
        if c_type == "Ready":
            return c_status == "True"
    return False


def _parse_k8s_error(exc: kubernetes.client.ApiException) -> dict:
    """Extract a user-friendly message from a K8s ApiException."""
    status_code = exc.status or 500
    reason = None
    message = None
    try:
        body = json.loads(exc.body) if exc.body else {}
        reason = body.get("reason", "")
        message = body.get("message", "")
    except (json.JSONDecodeError, TypeError):
        pass

    if status_code == 404 or reason == "NotFound":
        user_message = "The requested resource was not found."
    elif reason == "AlreadyExists":
        user_message = "The resource already exists."
    elif status_code == 409:
        user_message = "A conflict occurred while processing the request."
    elif status_code == 422 or reason == "Invalid":
        user_message = "The request was invalid."
    else:
        user_message = "An error occurred while processing the request."

    return {
        "status_code": status_code,
        "message": user_message,
        "details": message or exc.reason or user_message,
    }


@app.exception_handler(kubernetes.client.ApiException)
async def kubernetes_error_handler(
    request: Request, exc: kubernetes.client.ApiException
):
    parsed = _parse_k8s_error(exc)
    logger.error(
        f"K8s error on {request.url.path}: "
        f"status={parsed['status_code']}, details={parsed['details']}"
    )
    return JSONResponse(
        status_code=parsed["status_code"],
        content={
            "message": parsed["message"],
            "details": parsed["details"],
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
    except kubernetes.client.ApiException:
        raise
    except (ValueError, NotImplementedError) as e:
        logger.error(
            f"Failed to create inferencing service: {e},"
            f"  traceback: {traceback.format_exc()}"
        )
        raise HTTPException(
            status_code=422,
            detail=f"Failed to create inferencing service: {e}",
        )
    except Exception as e:
        logger.error(
            f"Failed to create inferencing service: {e},"
            f"  traceback: {traceback.format_exc()}"
        )
        raise HTTPException(
            status_code=500,
            detail=f"Failed to create inferencing service: {e}",
        )


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
        response = {"id": model_name, "status": job_status}

        # When the InferenceService is not ready, augment
        # the response with pod-level diagnostics so the
        # caller can surface the root cause.
        if not _is_inference_service_ready(job_status):
            try:
                pod_health = k8s_client.get_pod_health(
                    model_name,
                    config.applications.inferencing.namespace,
                )
                response["podHealth"] = pod_health
            except Exception as e:
                logger.warning(f"Failed to get pod health for {model_name}: {e}")

        return response

    except ResourceNotFound as e:
        logger.error(f"Job with ID {model_name} not found.")
        raise HTTPException(
            status_code=404,
            detail=f"Job with ID {model_name} not found",
        )
    except kubernetes.client.ApiException:
        raise
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
    except kubernetes.client.ApiException:
        raise
    except (ValueError, NotImplementedError) as e:
        logger.error(
            f"Failed to get inferencing pod policy: {e},"
            f" traceback: {traceback.format_exc()}"
        )
        raise HTTPException(
            status_code=422,
            detail=f"Failed to get inferencing pod policy: {e}",
        )
    except Exception as e:
        logger.error(
            f"Failed to get inferencing pod policy: {e},"
            f" traceback: {traceback.format_exc()}"
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

    # Conditionally register integration test endpoints.
    if config.applications.inferencing.enable_test_endpoints:
        from .routes.test_routes import create_test_router

        app.include_router(create_test_router(k8s_client, config, metrics_collector))
        logger.info("Test endpoints enabled.")

    uvicorn.run(app, host="0.0.0.0", port=args.port, log_level="debug")
