import base64
import json
import logging
import os
import time
from typing import Callable, Dict, Optional

import kubernetes
from kubernetes.config.config_exception import ConfigException
from opentelemetry import trace
from opentelemetry.trace import Status, StatusCode
from pydantic import ValidationError
from src.config.configuration import ServiceSettings, SparkResourceSettings
from src.exceptions.custom_exceptions import ResourceNotFound
from src.models.cleanroom_spark_application import CleanRoomSparkApplication
from src.models.spark_application_models import SparkApplication
from src.telemetry.metrics import get_metrics
from src.utilities.constants import Constants

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
    _spark_resource_settings: SparkResourceSettings

    def __init__(
        self,
        kubeconfig_path: str,
        spark_resource_settings: SparkResourceSettings,
    ):
        """
        Initialize the Kubernetes client with the provided kubeconfig path.
        """
        self._kubeconfig_path = kubeconfig_path
        self._spark_resource_settings = spark_resource_settings
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

    def submit_job(
        self,
        name: str,
        namespace: str,
        spark_app: CleanRoomSparkApplication,
        tags: Optional[dict[str, str]] = None,
    ):
        """
        Submit a job to the Kubernetes cluster.
        """
        driver_config_map_name = f"{spark_app.spec.name}-driver-policy"
        executor_config_map_name = f"{spark_app.spec.name}-executor-policy"
        try:
            if spark_app.spec.driver.annotations is None:
                spark_app.spec.driver.annotations = {}
            if spark_app.spec.executor.annotations is None:
                spark_app.spec.executor.annotations = {}

            # Labels have a 63 character limit, hence we have to set the value of the config map
            # in annotations instead.
            spark_app.spec.executor.annotations[
                Constants.CCE_POLICY_CONFIG_MAP_ANNOTATION
            ] = base64.b64encode(
                json.dumps(
                    {
                        "name": executor_config_map_name,
                        "namespace": namespace,
                    }
                ).encode()
            ).decode(
                "utf-8"
            )
            spark_app.spec.driver.annotations[
                Constants.CCE_POLICY_CONFIG_MAP_ANNOTATION
            ] = base64.b64encode(
                json.dumps(
                    {
                        "name": driver_config_map_name,
                        "namespace": namespace,
                    }
                ).encode()
            ).decode(
                "utf-8"
            )

            logger.info(
                f"Submitting job with Name: {name}, Namespace: {namespace}, "
                + f"Driver ConfigMap: {driver_config_map_name}, Executor ConfigMap: {executor_config_map_name}"
            )

            # TODO (HPrabh): Do we need to also set the policy hash in the config map ?
            self.create_config_map(
                driver_config_map_name,
                namespace,
                {
                    Constants.CCE_POLICY_CONFIG_MAP_POLICY_KEY: spark_app.driver_policy.rego_base64
                },
                labels=tags,
            )
            self.create_config_map(
                executor_config_map_name,
                namespace,
                {
                    Constants.CCE_POLICY_CONFIG_MAP_POLICY_KEY: spark_app.executor_policy.rego_base64
                },
                labels=tags,
            )

            spark_spec = {
                "apiVersion": f"{self._spark_resource_settings.group}/{self._spark_resource_settings.version}",
                "kind": self._spark_resource_settings.kind,
                "metadata": {
                    "name": name,
                    "namespace": namespace,
                    "labels": tags or {},
                },
                "spec": spark_app.spec.dict(exclude_unset=True),
            }

            self._api_caller.call(
                operation="create",
                resource_type=self._spark_resource_settings.kind.lower(),
                api_callable=lambda: kubernetes.client.CustomObjectsApi().create_namespaced_custom_object(
                    group=self._spark_resource_settings.group,
                    version=self._spark_resource_settings.version,
                    namespace=namespace,
                    plural=self._spark_resource_settings.plural,
                    body=spark_spec,
                ),
                name=name,
                namespace=namespace,
            )

            self.set_config_map_owner(
                driver_config_map_name, namespace, spark_app.spec.name
            )
            self.set_config_map_owner(
                executor_config_map_name,
                namespace,
                spark_app.spec.name,
            )

            logger.info(f"Job submitted successfully with Name: {name}")
        except kubernetes.client.ApiException as e:
            logger.error(f"Failed to submit job: {e}")
            raise
        except Exception as e:
            logger.error(f"Failed to submit job: {e}")
            raise

    def get_spark_app(self, name: str, namespace: str) -> SparkApplication:
        try:
            ret_val = self._api_caller.call(
                operation="get",
                resource_type=self._spark_resource_settings.kind.lower(),
                api_callable=lambda: kubernetes.client.CustomObjectsApi().get_namespaced_custom_object(
                    group=self._spark_resource_settings.group,
                    version=self._spark_resource_settings.version,
                    namespace=namespace,
                    plural=self._spark_resource_settings.plural,
                    name=name,
                ),
                name=name,
                namespace=namespace,
            )

            # Convert metadata dictionary to V1ObjectMeta object
            # This needs to be done because pydantic can't handle the conversion natively
            # as the V1ObjectMeta class is not a pydantic model.
            metadata_dict = ret_val.get("metadata", {})
            try:
                # Create V1ObjectMeta with only the fields that are present
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
                    f"Failed to create V1ObjectMeta from: {metadata_dict}, error: {e}"
                )
                raise

            status_dict = ret_val.get("status", {})
            if not status_dict:
                logger.warning(f"SparkApplication {name} has empty status.")
            logger.info(f"Status: {status_dict}")
            spark_app_data = {
                "metadata": metadata_obj,
                "status": status_dict if status_dict else None,
            }

            try:
                return SparkApplication.model_validate(spark_app_data)
            except ValidationError as validation_error:
                logger.error(
                    f"Pydantic validation failed for SparkApplication: {validation_error}"
                )
                for error in validation_error.errors():
                    logger.error(f"Field '{error.get('loc')}': {error.get('msg')}")
                raise
            except Exception as validation_error:
                logger.error(
                    f"Unexpected validation error for SparkApplication: {validation_error}"
                )
                raise

        except kubernetes.client.ApiException as e:
            if e.status == 404:
                logger.error(f"Spark Application:  {name} not found")
                raise ResourceNotFound(f"Spark Application {name} not found")
            logger.error(f"Failed to get spark application: {e}")
            raise
        except Exception as e:
            logger.error(f"Error retrieving spark application: {e}")
            raise

    def get_config_map(self, name: str, namespace: str):
        try:
            return self._api_caller.call(
                operation="get",
                resource_type="configmap",
                api_callable=lambda: kubernetes.client.CoreV1Api().read_namespaced_config_map(
                    name=name, namespace=namespace
                ),
                name=name,
                namespace=namespace,
            )
        except kubernetes.client.ApiException as e:
            if e.status == 404:
                logger.error(f"ConfigMap {name} not found in namespace {namespace}")
            logger.error(f"Failed to get ConfigMap: {e}")
            raise
        except Exception as e:
            logger.error(f"Error retrieving ConfigMap: {e}")
            raise

    def create_config_map(
        self,
        name: str,
        namespace: str,
        data: dict,
        labels: Optional[Dict[str, str]] = {},
    ):
        config_map = kubernetes.client.V1ConfigMap(
            metadata=kubernetes.client.V1ObjectMeta(
                name=name, namespace=namespace, labels=labels
            ),
            data=data,
        )

        try:
            self._api_caller.call(
                operation="create",
                resource_type="configmap",
                api_callable=lambda: kubernetes.client.CoreV1Api().create_namespaced_config_map(
                    namespace=namespace, body=config_map
                ),
                name=name,
                namespace=namespace,
            )
            logger.info(
                f"ConfigMap {name} created successfully in namespace {namespace}"
            )
        except kubernetes.client.ApiException as e:
            logger.error(f"Failed to create ConfigMap: {e}")
            raise

    def set_config_map_owner(self, name: str, namespace: str, spark_app_name: str):
        spark_app = self.get_spark_app(name=spark_app_name, namespace=namespace)

        body = {
            "metadata": {
                "ownerReferences": [
                    {
                        "apiVersion": f"{self._spark_resource_settings.group}/{self._spark_resource_settings.version}",
                        "kind": self._spark_resource_settings.kind,
                        "name": spark_app_name,
                        "uid": spark_app.metadata.uid,
                        "controller": True,
                        "blockOwnerDeletion": False,
                    }
                ]
            }
        }

        try:
            self._api_caller.call(
                operation="patch",
                resource_type="configmap",
                api_callable=lambda: kubernetes.client.CoreV1Api().patch_namespaced_config_map(
                    name=name, namespace=namespace, body=body
                ),
                name=name,
                namespace=namespace,
            )
            logger.info(
                f"Owner reference set for ConfigMap {name} in namespace {namespace}"
            )
        except kubernetes.client.ApiException as e:
            if e.status == 404:
                logger.error(f"ConfigMap {name} not found in namespace {namespace}")
            else:
                logger.error(f"Failed to set owner reference for ConfigMap: {e}")
            raise

    def create_mutating_webhook_configuration(
        self, service_settings: ServiceSettings, cert_path: str
    ):
        admission_api = kubernetes.client.AdmissionregistrationV1Api()

        if not os.path.exists(cert_path):
            logger.error(f"Service cert file not found at {cert_path}")
            raise Exception(
                f"Service cert file not found at {cert_path}",
            )

        with open(cert_path, "rb") as f:
            ca_cert = base64.b64encode(f.read()).decode("utf-8")

        webhook_client_config = (
            kubernetes.client.AdmissionregistrationV1WebhookClientConfig(
                service=kubernetes.client.AdmissionregistrationV1ServiceReference(
                    name=service_settings.name,
                    namespace=service_settings.namespace,
                    path="/mutate",
                ),
                ca_bundle=ca_cert,
            )
        )

        pod_selector = kubernetes.client.V1LabelSelector(
            match_labels={Constants.CCE_POLICY_INJECTOR_LABEL: "true"}
        )

        webhook = kubernetes.client.V1MutatingWebhook(
            name=f"{Constants.CCE_POLICY_INJECTOR_WEBHOOK_NAME}.{service_settings.namespace}.svc",
            client_config=webhook_client_config,
            rules=[
                kubernetes.client.V1RuleWithOperations(
                    operations=["CREATE"],
                    api_groups=[""],
                    api_versions=["v1"],
                    resources=["pods"],
                )
            ],
            admission_review_versions=["v1"],
            side_effects="None",
            timeout_seconds=10,
            failure_policy="Fail",
            object_selector=pod_selector,
        )

        webhook_config = kubernetes.client.V1MutatingWebhookConfiguration(
            metadata=kubernetes.client.V1ObjectMeta(name=webhook.name),
            webhooks=[webhook],
        )

        try:
            existing_webhook = self._api_caller.call(
                operation="get",
                resource_type="mutatingwebhookconfiguration",
                name=webhook.name,
                api_callable=lambda: admission_api.read_mutating_webhook_configuration(
                    webhook.name
                ),
            )
            existing_webhook.webhooks[0].client_config = webhook_client_config
            self._api_caller.call(
                operation="patch",
                resource_type="mutatingwebhookconfiguration",
                name=webhook.name,
                api_callable=lambda: admission_api.replace_mutating_webhook_configuration(
                    name=webhook.name, body=existing_webhook
                ),
            )
            logger.info(
                f"Updated existing mutating webhook configuration: {webhook.name}"
            )
        except kubernetes.client.ApiException as e:
            logger.info(e.status)
            if e.status == 404:
                logger.info(
                    f"Creating new mutating webhook configuration: {webhook.name}"
                )
                self._api_caller.call(
                    operation="create",
                    resource_type="mutatingwebhookconfiguration",
                    name=webhook.name,
                    api_callable=lambda: admission_api.create_mutating_webhook_configuration(
                        body=webhook_config
                    ),
                )
            else:
                logger.error(
                    f"Failed to create or update mutating webhook configuration: {e}"
                )
                raise
