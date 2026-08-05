import base64
import json
import logging
import time
import traceback
from typing import Annotated, Optional

from fastapi import APIRouter, HTTPException
from fastapi.params import Body

from cleanroom_internal.utilities.tracing_utilities import create_span_context

from ..config.configuration import Configuration
from ..models.input_models import NodeType

logger = logging.getLogger("kserve-inferencing-frontend")


def _generate_test_policy_base64(name: str) -> str:
    """Generate the base64-encoded policy matching the test model containers."""
    model_volume = "model-data"
    model_url = (
        "https://storage.googleapis.com/"
        "kfserving-examples/models/sklearn/1.0/model/model.joblib"
    )
    policy = [
        {
            "name": "model-download",
            "properties": {
                "image": "busybox:1.36",
                "command": ["sh", "-c"],
                "args": [
                    f"mkdir -p /mnt/models && "
                    f"wget -q -O /mnt/models/model.joblib {model_url}"
                ],
                "volumeMounts": [
                    {
                        "name": model_volume,
                        "mountPath": "/mnt/models",
                        "readOnly": False,
                    },
                ],
            },
        },
        {
            "name": "kserve-container",
            "properties": {
                "image": "docker.io/kserve/sklearnserver:v0.17.0",
                "args": [
                    "--model_name=" + name,
                    "--model_dir=/mnt/models",
                ],
                "environmentVariables": [
                    {
                        "name": "INFERENCE_SERVICE_NAME",
                        "value": ".*",
                        "regex": True,
                    },
                ],
                "volumeMounts": [
                    {
                        "name": model_volume,
                        "mountPath": "/mnt/models",
                        "readOnly": False,
                    },
                ],
            },
        },
    ]
    return base64.b64encode(json.dumps(policy).encode()).decode()


def create_test_router(
    k8s_client, config: Configuration, metrics_collector
) -> APIRouter:
    """Create a router with integration test endpoints.

    These endpoints use KServe's native model/runtime spec with a public
    storageUri, bypassing the production containers-spec builder. They exist
    so that CI (test-cluster.ps1) can validate cluster infrastructure (KServe
    controller, kubelet proxy, flex node scheduling) without needing the full
    governance/agent/blobfuse pipeline.
    """
    router = APIRouter()

    @router.post("/inferencing/test/deployModel")
    async def deploy_inferencing_test_model(
        model_name: Annotated[str, Body(alias="modelName")],
        node_type: Annotated[Optional[NodeType], Body(alias="nodeType")] = None,
        host_network: Annotated[Optional[bool], Body(alias="hostNetwork")] = None,
        signature: Annotated[Optional[str], Body(alias="signature")] = None,
    ):
        """Integration test endpoint only. Deploys a hardcoded sklearn model
        using KServe's native model/runtime spec with a public storageUri."""
        job_id = f"{int(time.time())}"
        tags = {"job_type": "inferencing"}
        try:
            return await _deploy_test_model(
                k8s_client,
                config,
                metrics_collector,
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
                f"Failed to create inferencing service: {e},"
                f"  traceback: {traceback.format_exc()}"
            )
            raise HTTPException(
                status_code=500,
                detail=f"Failed to create inferencing service: {e}",
            )

    @router.post("/inferencing/test/generateSecurityPolicy")
    async def get_inferencing_test_policy(name: str, node_type: NodeType):
        """Integration test endpoint only. Returns a policy that matches the
        containers used by the test model deployment on the flex node."""
        if node_type != NodeType.flexnode:
            raise HTTPException(
                status_code=400,
                detail="Security policy generation is not supported for "
                "non-flexnode node type.",
            )

        return {
            "predictor": {
                "jsonBase64": _generate_test_policy_base64(name),
            },
        }

    return router


async def _deploy_test_model(
    k8s_client,
    config: Configuration,
    metrics_collector,
    name: str,
    job_id: str,
    namespace: str,
    node_type: Optional[NodeType] = None,
    host_network: Optional[bool] = None,
    signature: Optional[str] = None,
    tags: Optional[dict[str, str]] = None,
):
    start_time = time.time()
    success = False

    with create_span_context(
        "deploy_test_model",
        {"job.id": job_id, "job.namespace": namespace},
    ):
        try:
            logger.info(f"Submitting test inferencing model to Kubernetes: {job_id}")

            from kubernetes.client import models as k8smodels

            from ..models.inference_service_models import (
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
                policy_base64 = _generate_test_policy_base64(name)
                annotations = {
                    "api-server-proxy.io/policy": policy_base64,
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
