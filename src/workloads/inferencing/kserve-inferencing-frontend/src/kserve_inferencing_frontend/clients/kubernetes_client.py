import logging
import time
from typing import Callable, Optional

import kubernetes
from kubernetes.config.config_exception import ConfigException
from opentelemetry import trace
from opentelemetry.trace import Status, StatusCode
from pydantic import ValidationError

from ..config.configuration import InferenceServiceResourceSettings
from ..exceptions.custom_exceptions import ResourceNotFound
from ..models.inference_service_models import InferenceService, InferenceServiceSpec
from ..telemetry.metrics import get_metrics

logger = logging.getLogger("kubernetes_client")


class KubernetesOperationTracer:
    def __init__(
        self,
        operation: str,
        resource_type: str,
        name: Optional[str] = None,
        namespace: Optional[str] = None,
    ):
        self.operation = operation
        self.resource_type = resource_type
        self.name = name
        self.namespace = namespace
        self.tracer = trace.get_tracer(__name__)
        self.span = None
        self.span_context = None
        self.start_time = None
        self.metrics = get_metrics()

    def __enter__(self):
        """Enter the context manager and start the span"""
        self.start_time = time.time()

        span_name = f"k8s.{self.operation}.{self.resource_type}"
        self.span_context = self.tracer.start_as_current_span(span_name)
        self.span = self.span_context.__enter__()

        # Set span attributes
        self.span.set_attribute("k8s.operation", self.operation)
        self.span.set_attribute("k8s.resource_type", self.resource_type)
        if self.namespace:
            self.span.set_attribute("k8s.namespace", self.namespace)
        if self.name:
            self.span.set_attribute("k8s.resource_name", self.name)

        return self.span

    def __exit__(self, exc_type, exc_val, exc_tb):
        """Exit the context manager and finalize the span"""
        duration = time.time() - self.start_time if self.start_time else 0.0
        success = exc_type is None

        # Record metrics
        labels = {}
        if self.namespace:
            labels["namespace"] = self.namespace
        if self.name:
            labels["resource_name"] = self.name

        self.metrics.record_k8s_operation(
            operation=self.operation,
            resource_kind=self.resource_type,
            success=success,
            duration=duration,
            **labels,
        )

        if self.span:
            if success:
                self.span.set_status(Status(StatusCode.OK))
            else:
                self.span.set_status(Status(StatusCode.ERROR, str(exc_val)))
                self.span.record_exception(exc_val)

        if self.span_context:
            self.span_context.__exit__(exc_type, exc_val, exc_tb)

        return False


class KubernetesAPICaller:

    def call(
        self,
        operation: str,
        resource_type: str,
        api_callable: Callable,
        name: Optional[str] = None,
        namespace: Optional[str] = None,
    ):
        with KubernetesOperationTracer(
            operation=operation,
            resource_type=resource_type,
            name=name,
            namespace=namespace,
        ):
            try:
                return api_callable()
            except Exception:
                raise


class KubernetesClient:
    _kubeconfig_path: str
    _resource_settings: InferenceServiceResourceSettings

    def __init__(
        self,
        kubeconfig_path: str,
        resource_settings: InferenceServiceResourceSettings,
    ):
        """
        Initialize the Kubernetes client with the provided kubeconfig path.
        """
        self._kubeconfig_path = kubeconfig_path
        self._resource_settings = resource_settings
        self._api_caller = KubernetesAPICaller()
        self._create_kubernetes_client()

    def _create_kubernetes_client(self):
        """
        Create a Kubernetes client using the provided kubeconfig path.
        """
        try:
            if self._kubeconfig_path:
                logger.info(f"Loading kubeconfig from {self._kubeconfig_path}")
                kubernetes.config.load_kube_config(config_file=self._kubeconfig_path)
            else:
                logger.info("No kubeconfig path provided, using in-cluster config.")
                kubernetes.config.load_incluster_config()
            logger.info("Kubernetes client created successfully.")

        except ConfigException:
            logger.error("Kubeconfig file not found or invalid.")
            raise
        except Exception as e:
            logger.error(f"Failed to create Kubernetes client: {e}")
            raise

    def submit_custom_resource(
        self,
        name: str,
        namespace: str,
        spec_dict: dict,
        group: str,
        version: str,
        kind: str,
        plural: str,
        tags: Optional[dict[str, str]] = None,
        annotations: Optional[dict[str, str]] = None,
    ):
        """
        Submit a custom resource to the cluster. Create or update.
        """
        try:
            logger.info(f"Submitting cr with Name: {name}, Namespace: {namespace}")

            resource_body = {
                "apiVersion": f"{group}/{version}",
                "kind": kind,
                "metadata": {
                    "name": name,
                    "namespace": namespace,
                    "labels": tags or {},
                    "annotations": annotations or {},
                },
                "spec": spec_dict,
            }

            try:
                existing = (
                    kubernetes.client.CustomObjectsApi().get_namespaced_custom_object(
                        group=group,
                        version=version,
                        namespace=namespace,
                        plural=plural,
                        name=name,
                    )
                )

                logger.info(f"Resource {name} already exists, updating it")
                resource_body["metadata"]["resourceVersion"] = existing["metadata"][
                    "resourceVersion"
                ]

                self._api_caller.call(
                    operation="update",
                    resource_type=kind.lower(),
                    api_callable=lambda: (
                        kubernetes.client.CustomObjectsApi().replace_namespaced_custom_object(
                            group=group,
                            version=version,
                            namespace=namespace,
                            plural=plural,
                            name=name,
                            body=resource_body,
                        )
                    ),
                    name=name,
                    namespace=namespace,
                )
                logger.info(f"Custom resource updated successfully: {name}")

            except kubernetes.client.ApiException as get_e:
                if get_e.status == 404:
                    logger.info(f"Resource {name} doesn't exist, creating it")
                    self._api_caller.call(
                        operation="create",
                        resource_type=kind.lower(),
                        api_callable=lambda: (
                            kubernetes.client.CustomObjectsApi().create_namespaced_custom_object(
                                group=group,
                                version=version,
                                namespace=namespace,
                                plural=plural,
                                body=resource_body,
                            )
                        ),
                        name=name,
                        namespace=namespace,
                    )
                    logger.info(f"Custom resource created successfully: {name}")
                else:
                    logger.error(f"Failed to check if resource exists: {get_e}")
                    raise

        except kubernetes.client.ApiException as e:
            logger.error(f"Failed to submit custom resource: {e}")
            raise
        except Exception as e:
            logger.error(f"Failed to submit custom resource: {e}")
            raise

    def get_custom_resource(
        self,
        name: str,
        namespace: str,
        group: str,
        version: str,
        kind: str,
        plural: str,
    ) -> dict:
        """
        Get a custom resource from the cluster as a raw dict.
        """
        try:
            return self._api_caller.call(
                operation="get",
                resource_type=kind.lower(),
                api_callable=lambda: (
                    kubernetes.client.CustomObjectsApi().get_namespaced_custom_object(
                        group=group,
                        version=version,
                        namespace=namespace,
                        plural=plural,
                        name=name,
                    )
                ),
                name=name,
                namespace=namespace,
            )
        except kubernetes.client.ApiException as e:
            if e.status == 404:
                logger.error(f"Custom resource {name} not found")
                raise ResourceNotFound(f"Custom resource {name} not found")
            logger.error(f"Failed to get custom resource: {e}")
            raise
        except Exception as e:
            logger.error(f"Error retrieving custom resource: {e}")
            raise

    def submit_inference_service(
        self,
        name: str,
        namespace: str,
        inference_svc_spec: InferenceServiceSpec,
        tags: Optional[dict[str, str]] = None,
        annotations: Optional[dict[str, str]] = None,
    ):
        """
        Submit an InferenceService CR. Delegates to submit_custom_resource.
        """
        self.submit_custom_resource(
            name=name,
            namespace=namespace,
            spec_dict=inference_svc_spec.model_dump(exclude_unset=True),
            group=self._resource_settings.group,
            version=self._resource_settings.version,
            kind=self._resource_settings.kind,
            plural=self._resource_settings.plural,
            tags=tags,
            annotations=annotations,
        )

        # Create a companion service that routes HTTPS traffic to the ccr-proxy
        # sidecar for TLS-terminated access to the predictor.
        # The InferenceService CR is set as the owner so the service is
        # garbage-collected when the InferenceService is deleted.
        owner_ref = self._get_inference_service_owner_ref(name, namespace)
        self._create_ccr_proxy_service(name, namespace, owner_ref)

    def _get_inference_service_owner_ref(
        self, name: str, namespace: str
    ) -> kubernetes.client.V1OwnerReference:
        """Look up the InferenceService CR and return an owner reference for it."""
        cr = kubernetes.client.CustomObjectsApi().get_namespaced_custom_object(
            group=self._resource_settings.group,
            version=self._resource_settings.version,
            namespace=namespace,
            plural=self._resource_settings.plural,
            name=name,
        )
        return kubernetes.client.V1OwnerReference(
            api_version=f"{self._resource_settings.group}/{self._resource_settings.version}",
            kind=self._resource_settings.kind,
            name=name,
            uid=cr["metadata"]["uid"],
            block_owner_deletion=True,
            controller=True,
        )

    def _create_ccr_proxy_service(
        self,
        name: str,
        namespace: str,
        owner_ref: kubernetes.client.V1OwnerReference,
    ) -> None:
        """
        Create a K8s Service named {name}-predictor-https that targets the
        ccr-proxy sidecar for TLS-terminated access to the predictor.
        """
        svc_name = f"{name}-predictor-https"
        svc = kubernetes.client.V1Service(
            metadata=kubernetes.client.V1ObjectMeta(
                name=svc_name,
                namespace=namespace,
                labels={
                    "app": f"isvc.{name}-predictor",
                    "component": "predictor",
                },
                owner_references=[owner_ref],
            ),
            spec=kubernetes.client.V1ServiceSpec(
                selector={"app": f"isvc.{name}-predictor"},
                ports=[
                    kubernetes.client.V1ServicePort(
                        name="https",
                        port=443,
                        target_port=443,
                        protocol="TCP",
                    )
                ],
            ),
        )

        core_api = kubernetes.client.CoreV1Api()
        try:
            core_api.create_namespaced_service(namespace=namespace, body=svc)
            logger.info(f"Created ccr-proxy service: {svc_name}")
        except kubernetes.client.ApiException as e:
            if e.status == 409:
                logger.info(f"Service {svc_name} already exists, skipping creation.")
            else:
                raise

    def get_inference_service(self, name: str, namespace: str) -> InferenceService:
        """
        Get an InferenceService CR and deserialize to the typed model.
        """
        ret_val = self.get_custom_resource(
            name=name,
            namespace=namespace,
            group=self._resource_settings.group,
            version=self._resource_settings.version,
            kind=self._resource_settings.kind,
            plural=self._resource_settings.plural,
        )

        # Convert metadata dictionary to V1ObjectMeta object.
        # Pydantic cannot handle this natively as V1ObjectMeta
        # is not a pydantic model.
        metadata_dict = ret_val.get("metadata", {})
        try:
            metadata_obj = kubernetes.client.V1ObjectMeta(
                name=metadata_dict.get("name"),
                namespace=metadata_dict.get("namespace"),
                creation_timestamp=metadata_dict.get("creationTimestamp"),
                labels=metadata_dict.get("labels"),
                annotations=metadata_dict.get("annotations"),
                uid=metadata_dict.get("uid"),
                resource_version=metadata_dict.get("resourceVersion"),
                generation=metadata_dict.get("generation"),
            )
        except Exception as e:
            logger.error(
                f"Failed to create V1ObjectMeta from: " f"{metadata_dict}, error: {e}"
            )
            raise

        status_dict = ret_val.get("status", {})
        if not status_dict:
            logger.warning(f"InferenceService {name} has empty status.")
        logger.info(f"Status: {status_dict}")
        inference_svc_data = {
            "metadata": metadata_obj,
            "status": status_dict if status_dict else None,
        }

        try:
            return InferenceService.model_validate(inference_svc_data)
        except ValidationError as validation_error:
            logger.error(
                "Pydantic validation failed for "
                f"InferenceService: {validation_error}"
            )
            for error in validation_error.errors():
                logger.error(f"Field '{error.get('loc')}': {error.get('msg')}")
            raise
