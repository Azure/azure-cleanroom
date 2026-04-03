import base64
import json
import logging
import traceback
from typing import Any, Dict, Optional

from kubernetes import client

from ..base_webhook_handler import BaseWebhookHandler
from .policy_injector import PolicyInjector

logger = logging.getLogger("policy_injector_webhook_handler")


class PolicyInjectorWebhookHandler(BaseWebhookHandler):
    """
    Handler for Kubernetes mutating webhook admission requests for CCE policy injection.
    """

    def __init__(
        self,
        policy_injector: PolicyInjector,
        cce_policy_config_map_annotation: str,
        cce_policy_config_map_policy_key: str,
        cce_policy_annotation_name_label: str,
    ):
        super().__init__(webhook_name="cce_policy_injector")
        self.policy_injector = policy_injector
        self.cce_policy_config_map_annotation = cce_policy_config_map_annotation
        self.cce_policy_config_map_policy_key = cce_policy_config_map_policy_key
        self.cce_policy_annotation_name_label = cce_policy_annotation_name_label

    def _handle_request(
        self,
        pod_name: str,
        namespace: str,
        pod: Dict[str, Any],
    ) -> tuple[bool, str, Optional[str]]:

        logger.info(
            f"Received cce policy injection request with name: {pod_name}, namespace: {namespace}"
        )

        pod_metadata = pod.get("metadata", {})
        pod_labels = pod_metadata.get("labels", {})
        pod_annotations = pod_metadata.get("annotations", {})

        # Check for CCE policy ConfigMap annotation
        cce_policy_map_base64 = pod_annotations.get(
            self.cce_policy_config_map_annotation
        )
        if not cce_policy_map_base64:
            logger.error(
                f"Pod: {pod_name} does not have '{self.cce_policy_config_map_annotation}' annotation. Failing mutation."
            )
            return (
                False,
                f"Pod does not have '{self.cce_policy_config_map_annotation}' annotation.",
                None,
            )

        try:
            # Decode ConfigMap reference
            cce_policy_map = json.loads(
                base64.b64decode(cce_policy_map_base64).decode("utf-8")
            )
            cce_policy_map_name = cce_policy_map.get("name")
            cce_policy_map_namespace = cce_policy_map.get("namespace")

            if not cce_policy_map_name or not cce_policy_map_namespace:
                logger.error(
                    f"ConfigMap information for pod: {pod_name} is incomplete: {json.dumps(cce_policy_map)}"
                )
                return (
                    False,
                    f"ConfigMap information is incomplete: {json.dumps(cce_policy_map)}.",
                    None,
                )

            # Retrieve policy from ConfigMap
            policy = self.policy_injector.get_policy_from_config_map(
                cce_policy_map_name,
                cce_policy_map_namespace,
                self.cce_policy_config_map_policy_key,
            )

            if not policy:
                logger.error(
                    f"ConfigMap {cce_policy_map_name} in namespace {cce_policy_map_namespace} for pod: {pod_name} "
                    f"does not contain '{self.cce_policy_config_map_policy_key}' key. Failing mutation."
                )
                return (
                    False,
                    f"ConfigMap {cce_policy_map_name} in namespace {cce_policy_map_namespace} "
                    f"does not contain '{self.cce_policy_config_map_policy_key}' key.",
                    None,
                )

            # Get the annotation name from pod labels
            cce_policy_annotation_name = pod_labels.get(
                self.cce_policy_annotation_name_label
            )
            if not cce_policy_annotation_name:
                logger.error(
                    f"Pod {pod_name} does not have '{self.cce_policy_annotation_name_label}' label. Failing mutation."
                )
                return (
                    False,
                    f"Pod does not have '{self.cce_policy_annotation_name_label}' label.",
                    None,
                )

            # Create JSON patch to add policy annotation
            patch = [
                {
                    "op": "add",
                    "path": f"/metadata/annotations/{cce_policy_annotation_name}",
                    "value": policy,
                }
            ]
            patch_json = json.dumps(patch)

            logger.info(
                f"Patching pod {pod_name} with cce policy from config map: {cce_policy_map}"
            )

            return (True, f"Policy injected into pod {pod_name}", patch_json)

        except client.ApiException as e:
            logger.error(f"Failed to mutate resource. Error: {e}")
            logger.error(f"Traceback: {traceback.format_exc()}")
            return (False, f"Failed to mutate resource. Error: {e}", None)
        except Exception as e:
            logger.error(f"Unexpected error during mutation: {e}")
            logger.error(f"Traceback: {traceback.format_exc()}")
            return (False, f"Internal error during mutation: {str(e)}", None)
