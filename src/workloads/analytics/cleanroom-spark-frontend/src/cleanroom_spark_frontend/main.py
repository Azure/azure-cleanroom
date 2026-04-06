import argparse
import base64
import json
import logging
import os
import time
import traceback
import uuid
from typing import Annotated, Optional

import kubernetes
import requests
from analytics_contracts.events import OperationalEvent
from analytics_contracts.statistics import (
    QueryStatisticsData,
    StatisticsEvent,
    StatisticsEventType,
)
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
from opentelemetry import context

from .clients.ai_optimizer_client import AIOptimizerClient
from .clients.job_record_client import JobRecordClient
from .clients.kubernetes_client import KubernetesClient
from .config.config_manager import ConfigManager
from .config.configuration import (
    Configuration,
    DriverSettings,
    ExecutorInstanceSettings,
    ExecutorSettings,
    SkuSettings,
)
from .exceptions.custom_exceptions import ResourceNotFound
from .models.input_models import JobInput, SQLJobInput
from .models.job_record_models import (
    JobRecord,
    JobRecordResponse,
    JobRun,
    JobRunError,
    JobRunStats,
)
from .models.spark_application_models import (
    ApplicationStateEnum,
    SparkApplication,
    SparkApplicationStatus,
)
from .telemetry.metrics import SparkFrontendMetrics, get_metrics
from .utilities import job_converters
from .utilities.constants import Constants
from .utilities.helpers import generate_query_id, utc_now
from .utilities.job_converters import SparkJobProviderType
from .webhooks.cce_policy_injector import PolicyInjector, PolicyInjectorWebhookHandler
from .webhooks.scheduler import PodScheduler, SchedulerWebhookHandler

# Configure logger
logger = logging.getLogger("cleanroom-spark-frontend")

app = FastAPI()
k8s_client: KubernetesClient
job_record_client: JobRecordClient
config: Configuration
metrics_collector: SparkFrontendMetrics = get_metrics()
scheduler_webhook_handler: SchedulerWebhookHandler
policy_injector_webhook_handler: PolicyInjectorWebhookHandler


async def submit_job(
    provider_type: SparkJobProviderType,
    job_id: str,
    job: JobInput,
    namespace: str,
    enable_telemetry_collection: bool = True,
    tags: Optional[dict[str, str]] = None,
    override_sku_settings: Optional[dict] = None,
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
                override_sku_settings=override_sku_settings,
            )
            job_name = spark_spec.spec.name
            k8s_client.submit_job(job_name, namespace, spark_spec, tags)

            success = True
            return {"status": "success", "id": job_name}

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


@app.post("/analytics/submitSqlJob")
async def submit_sql_job(
    job: SQLJobInput,
    job_id: Annotated[str, Body(alias="jobId")] = "",
    query_id: Annotated[str, Body(alias="queryId")] = "",
    enable_telemetry_collection: Annotated[
        bool, Body(alias="enableTelemetryCollection")
    ] = True,
    dry_run: Annotated[bool, Body(alias="dryRun")] = False,
):
    global config

    job_id = job_id or f"{int(time.time())}"
    query_id = query_id or generate_query_id(job.query)
    tags = {"job_type": "sql", "query_id": query_id}
    override_sku_settings = None
    reasoning = None
    try:
        # Use AI optimizer if enabled in config and requested in job input
        if config.service.optimizer.enabled and job.use_optimizer:
            logger.info(f"Using AI optimizer for job {job_id}")
            try:
                optimizer_client = AIOptimizerClient(
                    endpoint=config.service.optimizer.endpoint,
                    timeout=config.service.optimizer.timeout,
                )

                # Build dataset info string for the AI model
                dataset_info = f"Datasets: {len(job.datasets)}\n"
                for ds in job.datasets:
                    dataset_info += f"- {ds.name}: format={ds.format}, "
                    if ds.schema_:
                        # Handle both dict and object schema representations
                        if isinstance(ds.schema_, dict):
                            fields = ds.schema_.get("fields", [])
                            dataset_info += f"fields={len(fields)}"
                        else:
                            dataset_info += f"fields={len(ds.schema_.fields)}"
                    dataset_info += "\n"

                # Get optimized configuration from AI
                optimized_config = optimizer_client.get_optimized_config(
                    query=job.query, dataset_info=dataset_info
                )

                if optimized_config:
                    logger.info(
                        f"AI optimizer recommended: driver={optimized_config.driver_cores}c/"
                        f"{optimized_config.driver_memory}, "
                        f"executor={optimized_config.executor_cores}c/"
                        f"{optimized_config.executor_memory} "
                        f"(instances: {optimized_config.executor_instances_min}-"
                        f"{optimized_config.executor_instances_max}) "
                        f"reasoning={optimized_config.reasoning}"
                    )

                    reasoning = optimized_config.reasoning

                    # Create override SKU settings from AI recommendations

                    override_sku_settings = SkuSettings(
                        driver=DriverSettings(
                            cores=optimized_config.driver_cores,
                            memory=optimized_config.driver_memory,
                            serviceAccount=config.applications.analytics.sql.driver.service_account,
                        ),
                        executor=ExecutorSettings(
                            cores=optimized_config.executor_cores,
                            memory=optimized_config.executor_memory,
                            instances=ExecutorInstanceSettings(
                                min=optimized_config.executor_instances_min,
                                max=optimized_config.executor_instances_max,
                            ),
                            deleteOnTermination=config.applications.analytics.sql.executor.delete_on_termination,
                        ),
                    )
                else:
                    logger.warning(
                        "AI optimizer did not return valid configuration, using default settings"
                    )
            except Exception as e:
                logger.error(
                    f"Failed to get AI optimization (using defaults): {e}, "
                    f"traceback: {traceback.format_exc()}"
                )

        # If dry run is enabled, return the SKU settings that would be used
        if dry_run:
            # Determine which settings would be used
            effective_sku_settings = (
                override_sku_settings
                if override_sku_settings
                else config.applications.analytics.sql
            )

            return {
                "status": "success",
                "id": "none",
                "dryRun": True,
                "jobId": job_id,
                "optimizationUsed": override_sku_settings is not None,
                "reasoning": reasoning,
                "skuSettings": {
                    "driver": {
                        "cores": effective_sku_settings.driver.cores,
                        "memory": effective_sku_settings.driver.memory,
                        "serviceAccount": effective_sku_settings.driver.service_account,
                    },
                    "executor": {
                        "cores": effective_sku_settings.executor.cores,
                        "memory": effective_sku_settings.executor.memory,
                        "instances": {
                            "min": effective_sku_settings.executor.instances.min,
                            "max": effective_sku_settings.executor.instances.max,
                        },
                        "deleteOnTermination": effective_sku_settings.executor.delete_on_termination,
                    },
                },
            }

        return await submit_job(
            SparkJobProviderType.SQL,
            job_id,
            job,
            config.applications.analytics.namespace,
            enable_telemetry_collection=enable_telemetry_collection,
            tags=tags,
            override_sku_settings=override_sku_settings,
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
            enable_telemetry_collection=False,
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

            return {"id": job_id, "status": job_status, "events": []}

        job_status = spark_app.status
        terminal_states = [
            ApplicationStateEnum.Completed,
            ApplicationStateEnum.Failed,
            ApplicationStateEnum.SubmissionFailed,
        ]
        is_terminal = job_status.applicationState.state in terminal_states
        # Only record terminal job once - check annotation to avoid repeated processing
        is_recorded = (
            spark_app.metadata
            and spark_app.metadata.annotations
            and spark_app.metadata.annotations.get(
                Constants.JOB_RECORD_RECORDED_ANNOTATION
            )
        )

        # Fetch related events
        k8s_events = []
        if spark_app.metadata and spark_app.metadata.uid:
            try:
                k8s_events = k8s_client.list_events_for_spark_app(
                    spark_app_name=job_id,
                    spark_app_uid=spark_app.metadata.uid,
                    namespace=config.applications.analytics.namespace,
                )
            except Exception as e:
                logger.error(
                    f"Failed to fetch events for job {job_id}: {e}, traceback: {traceback.format_exc()}"
                )

        # Record metrics and job run if in terminal state and not already recorded
        if is_terminal and not is_recorded:
            job_type = (
                "unknown"
                if spark_app.metadata.labels is None
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

            _record_job_run(
                spark_app,
                job_id,
                job_status,
                config.applications.analytics.namespace,
                k8s_events,
            )

        events = [
            {
                "name": event.metadata.name if event.metadata else None,
                "reason": event.reason,
                "message": event.message,
                "type": event.type,
                "firstTimestamp": (
                    event.first_timestamp.isoformat() if event.first_timestamp else None
                ),
                "lastTimestamp": (
                    event.last_timestamp.isoformat() if event.last_timestamp else None
                ),
                "count": event.count,
            }
            for event in k8s_events
        ]

        return {"id": job_id, "status": job_status, "events": events}

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


def _parse_query_stats_from_events(
    events: list[kubernetes.client.CoreV1Event], job_id: str
) -> tuple[int, int]:
    """Parse query stats from linked events. Returns (rows_read, rows_written)."""
    for event in events:
        if event.reason == StatisticsEventType.QUERY_STATISTICS.value and event.message:
            try:
                stats_data = QueryStatisticsData.model_validate_json(event.message)
                return stats_data.num_rows_read, stats_data.num_rows_written
            except Exception:
                logger.warning(f"Failed to parse stats from event: job_id={job_id}")
    return 0, 0


def _record_job_run(
    spark_app: SparkApplication,
    job_id: str,
    job_status: SparkApplicationStatus,
    namespace: str,
    events: list[kubernetes.client.CoreV1Event],
) -> None:
    """
    Add a job record for the query if the job has a query_id label.

    This function checks if the spark application has a query_id label,
    and if so, creates a JobRun record and adds it to the JobRecord CRD.
    Duplicate runs (same run_id) are automatically skipped by the job_record_client.

    Args:
        spark_app: The SparkApplication object with metadata and status.
        job_id: The unique job identifier (used as run_id).
        job_status: The SparkApplicationStatus object.
        namespace: The Kubernetes namespace for the job record.
        events: The list of Kubernetes events linked to the SparkApplication.
    """
    query_id = (
        spark_app.metadata.labels.get("query_id")
        if spark_app.metadata and spark_app.metadata.labels
        else None
    )

    if not query_id:
        logger.debug(f"No query_id label found, skipping job record: job_id={job_id}")
        return

    try:
        state = job_status.applicationState.state
        is_successful = state == ApplicationStateEnum.Completed
        error = (
            None
            if is_successful
            else JobRunError.from_state(state, job_status.applicationState.errorMessage)
        )
        rows_read, rows_written = _parse_query_stats_from_events(events, job_id)
        end_time = job_status.terminationTime or utc_now()

        run = JobRun(
            run_id=job_id,
            start_time=job_status.lastSubmissionAttemptTime,
            end_time=end_time,
            is_successful=is_successful,
            error=error,
            stats=JobRunStats(rows_read=rows_read, rows_written=rows_written),
        )

        job_record_client.add_run(
            query_id=query_id,
            namespace=namespace,
            run=run,
        )

        # Mark as recorded in SparkApplication annotation to prevent repeated recording
        try:
            k8s_client.patch_spark_app_annotation(
                name=job_id,
                namespace=namespace,
                annotation_key=Constants.JOB_RECORD_RECORDED_ANNOTATION,
                annotation_value="true",
            )
        except Exception as annotation_error:
            logger.warning(
                f"Failed to set recorded annotation: job_id={job_id}, error={annotation_error}"
            )

        logger.info(
            f"Updated job record: query_id={query_id}, run_id={job_id}, namespace={namespace}, is_successful={is_successful}"
        )
    except Exception as e:
        logger.error(
            f"Failed to update job record: query_id={query_id}, run_id={job_id}, namespace={namespace}, error={e}"
        )


@app.get("/analytics/{query_id}/runs")
async def get_runs(query_id: str):
    """
    Get the run history for a specific query ID.

    Returns the last 5 runs along with aggregated statistics including
    success/failure counts, average duration, and percentile metrics (p50, p95, p99).
    """
    global config

    if not query_id or not query_id.strip():
        raise HTTPException(
            status_code=400,
            detail="query_id cannot be null or empty.",
        )

    try:
        job_record = job_record_client.get_job_record(
            query_id, config.applications.analytics.namespace
        )

        if job_record is None:
            raise HTTPException(
                status_code=404,
                detail=f"No run history found for query ID: {query_id}",
            )

        return JobRecordResponse.from_job_record(job_record)

    except HTTPException:
        raise
    except Exception as e:
        logger.error(
            f"Failed to get runs for query {query_id}: {e}, traceback: {traceback.format_exc()}"
        )
        raise HTTPException(
            status_code=500,
            detail=f"Failed to get runs for query {query_id}: {e}",
        )


async def _create_spark_app_event(
    job_id: str,
    event_name: str,
    message: str,
    reason: str,
    event_type: str = "Normal",
):
    global k8s_client
    global config

    try:
        spark_app = k8s_client.get_spark_app(
            job_id, config.applications.analytics.namespace
        )

        if not spark_app.metadata:
            raise HTTPException(
                status_code=404,
                detail=f"Spark application {job_id} not found or has no metadata",
            )

        # Validate required metadata fields
        if (
            not spark_app.metadata.name
            or not spark_app.metadata.namespace
            or not spark_app.metadata.uid
        ):
            raise HTTPException(
                status_code=500,
                detail=f"Spark application {job_id} has incomplete metadata",
            )

        # Create a Kubernetes event
        k8s_client.create_event(
            name=event_name,
            namespace=config.applications.analytics.namespace,
            message=message,
            reason=reason,
            involved_object_name=spark_app.metadata.name,
            involved_object_kind="SparkApplication",
            involved_object_namespace=spark_app.metadata.namespace,
            involved_object_uid=spark_app.metadata.uid,
            event_type=event_type,
        )
    except ResourceNotFound as e:
        logger.error(f"Spark application {job_id} not found: {e}")
        raise HTTPException(
            status_code=404,
            detail=f"Spark application {job_id} not found",
        )


@app.put("/analytics/{job_id}/record_operational_event")
async def record_operational_event(job_id: str, event: OperationalEvent):
    try:
        event_name = f"{job_id}-event-{event.id}-{str(uuid.uuid4())[:8]}"
        await _create_spark_app_event(
            job_id=job_id,
            event_name=event_name,
            message=event.get_message(),
            reason=event.name,
            event_type="Normal",
        )
    except Exception as e:
        logger.error(f"Failed to record operational event for job {job_id}: {e}")
        raise HTTPException(
            status_code=500,
            detail=f"Failed to record operational event: {e}",
        )


@app.put("/analytics/{job_id}/record_statistics")
async def record_statistics(job_id: str, event: StatisticsEvent):
    try:
        if event.type != StatisticsEventType.QUERY_STATISTICS.value:
            raise HTTPException(
                status_code=400,
                detail=f"Invalid statistics event type: {event.type}. Expected 'QueryStatistics'",
            )

        event_name = f"{job_id}-statistics"

        decoded_data = base64.b64decode(event.data_base64).decode("utf-8")

        stats_data = QueryStatisticsData.model_validate_json(decoded_data)

        metrics_collector.record_query_statistics(
            job_id=job_id,
            rows_read=stats_data.num_rows_read,
            rows_written=stats_data.num_rows_written,
            duration_s=stats_data.duration_sec,
        )

        await _create_spark_app_event(
            job_id=job_id,
            event_name=event_name,
            message=decoded_data,
            reason=event.type,
            event_type="Normal",
        )
    except ResourceNotFound as e:
        logger.error(f"Spark application {job_id} not found: {e}")
        raise HTTPException(
            status_code=404,
            detail=f"Spark application {job_id} not found",
        )
    except Exception as e:
        logger.error(f"Failed to record statistics for job {job_id}: {e}")
        raise HTTPException(
            status_code=500,
            detail=f"Failed to record statistics: {e}",
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


@app.post("/pod/ccepolicy/mutate")
async def mutate(request: Request):
    global policy_injector_webhook_handler

    body = await request.json()
    logger.info(f"Received CCE policy injection webhook request")

    return policy_injector_webhook_handler.handle_request(body)


@app.post("/pod/schedulednode/mutate")
async def scheduler_mutate(request: Request):
    global scheduler_webhook_handler

    body = await request.json()
    logger.info(f"Received scheduler webhook request")

    return scheduler_webhook_handler.handle_request(body)


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
    global scheduler_webhook_handler
    global policy_injector_webhook_handler
    global job_record_client

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
        spark_resource_settings=config.spark.resource,
    )

    # Initialize JobRecord client for tracking job runs
    job_record_client = JobRecordClient()

    # Initialize the pod scheduler and webhook handler
    pod_scheduler = PodScheduler(k8s_client, config.service.scheduler)
    scheduler_webhook_handler = SchedulerWebhookHandler(pod_scheduler)
    logger.info("Initialized pod scheduler and webhook handler")

    # Initialize the CCE policy injector and webhook handler
    policy_injector = PolicyInjector(k8s_client)
    policy_injector_webhook_handler = PolicyInjectorWebhookHandler(
        policy_injector=policy_injector,
        cce_policy_config_map_annotation=Constants.CCE_POLICY_CONFIG_MAP_ANNOTATION,
        cce_policy_config_map_policy_key=Constants.CCE_POLICY_CONFIG_MAP_POLICY_KEY,
        cce_policy_annotation_name_label=Constants.CCE_POLICY_ANNOTATION_NAME_LABEL,
    )
    logger.info("Initialized CCE policy injector and webhook handler")

    # ccr-proxy generates this file and frontend starts only after ccr-proxy has started successfully
    # (based on the startup probe) so the file should already exist.
    service_cert_location = os.environ.get(
        "SERVICE_CERT_LOCATION", "/app/service/service-cert.pem"
    )
    if not os.path.exists(service_cert_location):
        logger.error(f"File not found at {service_cert_location}.")
        raise FileNotFoundError(f"File not found at {service_cert_location}.")

    k8s_client.create_mutating_webhook_configuration(
        name=Constants.CCE_POLICY_INJECTOR_WEBHOOK_NAME,
        path="/pod/ccepolicy/mutate",
        pod_labels={Constants.CCE_POLICY_INJECTOR_LABEL: "true"},
        service_settings=config.service,
        cert_path=service_cert_location,
    )
    k8s_client.create_mutating_webhook_configuration(
        name=Constants.SPARK_POD_SCHEDULER_WEBHOOK_NAME,
        path="/pod/schedulednode/mutate",
        pod_labels={Constants.SPARK_POD_SCHEDULER_LABEL: "true"},
        service_settings=config.service,
        cert_path=service_cert_location,
    )

    uvicorn.run(app, host="0.0.0.0", port=args.port, log_level="debug")
