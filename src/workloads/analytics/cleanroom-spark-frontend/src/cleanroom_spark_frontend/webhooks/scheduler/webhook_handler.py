"""Webhook handler for pod scheduling mutations."""

import json
import logging
from typing import Any, Dict, Optional

from ..base_webhook_handler import BaseWebhookHandler
from .pod_scheduler import PodScheduler

logger = logging.getLogger("webhook_handler")


class SchedulerWebhookHandler(BaseWebhookHandler):

    def __init__(self, scheduler: PodScheduler):
        super().__init__(webhook_name="pod_scheduler")
        self.scheduler = scheduler

    def _handle_request(
        self,
        pod_name: str,
        namespace: str,
        pod: Dict[str, Any],
    ) -> tuple[bool, str, Optional[str]]:

        logger.info(
            f"Received scheduling request for pod {pod_name} in namespace {namespace}"
        )

        pod_spec = pod.get("spec", {})

        # Check if pod already has a nodeName assigned
        if pod_spec.get("nodeName"):
            logger.info(
                f"Pod {pod_name} already has nodeName assigned: {pod_spec['nodeName']}"
            )
            return (True, "Pod already has nodeName assigned", None)

        # Select a node for the pod
        try:
            selected_node = self.scheduler.select_node()
        except Exception as e:
            logger.error(f"Error selecting node for pod {pod_name}: {e}")
            return (False, f"Internal error while selecting node: {str(e)}", None)

        # If no node is available, reject the request
        if not selected_node:
            logger.warning(f"No available nodes for pod {pod_name}. Request rejected.")
            return (False, "No nodes available with capacity.", None)

        # Create JSON patch to add nodeName to pod spec
        patch = [
            {
                "op": "add",
                "path": "/spec/nodeName",
                "value": selected_node,
            }
        ]
        patch_json = json.dumps(patch)

        logger.info(f"Assigning pod {pod_name} to node {selected_node}")

        return (True, f"Pod assigned to node {selected_node}", patch_json)
