"""Base class for webhook handlers with metrics support."""

import base64
import logging
import time
from abc import ABC, abstractmethod
from typing import Any, Dict, Optional

from fastapi.responses import JSONResponse
from opentelemetry import trace

from ..telemetry.metrics import get_metrics

logger = logging.getLogger("base_webhook_handler")


class BaseWebhookHandler(ABC):

    def __init__(self, webhook_name: str):
        """
        Initialize the base webhook handler.

        Args:
            webhook_name: Name of the webhook for metrics and logging.
        """
        self.webhook_name = webhook_name
        self.metrics = get_metrics()
        self.tracer = trace.get_tracer(__name__)

    def create_admission_response(
        self, uid: str, allowed: bool, message: str = "", patch: Optional[str] = None
    ) -> Dict[str, Any]:
        response = {
            "apiVersion": "admission.k8s.io/v1",
            "kind": "AdmissionReview",
            "response": {
                "uid": uid,
                "allowed": allowed,
            },
        }

        if not allowed:
            response["response"]["status"] = {
                "code": 400 if not allowed else 200,
                "message": message,
            }
        elif patch:
            # Add patch to the response
            patch_bytes = patch.encode("utf-8")
            patch_base64 = base64.b64encode(patch_bytes).decode("utf-8")
            response["response"]["patchType"] = "JSONPatch"
            response["response"]["patch"] = patch_base64

        return response

    @abstractmethod
    def _handle_request(
        self,
        pod_name: str,
        namespace: str,
        pod: Dict[str, Any],
    ) -> tuple[bool, str, Optional[str]]:
        """
        Handle the admission request. Must be implemented by subclasses.

        Args:
            pod_name: The name of the pod.
            namespace: The namespace of the pod.
            pod: The pod object from the admission request.

        Returns:
            Tuple of (allowed boolean, message string, optional patch string).
        """
        pass

    def handle_request(self, admission_request: Dict[str, Any]) -> JSONResponse:
        start_time = time.monotonic_ns()
        req = admission_request.get("request", {})
        uid = req.get("uid", "unknown")
        pod = req.get("object", {})

        span_name = f"webhook.{self.webhook_name}.handle_request"

        with self.tracer.start_as_current_span(span_name) as span:
            span.set_attribute("webhook.name", self.webhook_name)
            span.set_attribute("webhook.req.uid", uid)

            # Validate pod object exists
            if not pod:
                logger.error("Pod object is missing in the admission request")
                return JSONResponse(
                    content=self.create_admission_response(
                        uid=uid,
                        allowed=False,
                        message="Pod object is missing in the admission request.",
                    )
                )

            # Extract common pod metadata
            pod_metadata = pod.get("metadata", {})
            pod_name = pod_metadata.get("name", "unknown")
            namespace = pod_metadata.get("namespace", "default")

            span.set_attribute("webhook.pod.name", pod_name)
            span.set_attribute("webhook.pod.namespace", namespace)

            try:
                allowed, message, patch = self._handle_request(pod_name, namespace, pod)

                response = JSONResponse(
                    content=self.create_admission_response(
                        uid=uid,
                        allowed=allowed,
                        message=message,
                        patch=patch,
                    )
                )

                end_time = time.monotonic_ns()
                duration_sec = (end_time - start_time) / 1e9

                self._record_metrics(
                    operation="handle_request",
                    success=allowed,
                    duration=duration_sec,
                )

                span.set_attribute("webhook.success", allowed)
                span.set_attribute("webhook.duration", duration_sec)
                logger.debug(
                    f"{self.webhook_name} processed request {uid} in {duration_sec:.3f}s (success={allowed})"
                )

                return response

            except Exception as e:
                end_time = time.monotonic_ns()
                duration_sec = (end_time - start_time) / 1e9

                self._record_metrics(
                    operation="handle_request",
                    success=False,
                    duration=duration_sec,
                    error=str(e),
                )

                span.set_attribute("webhook.success", False)
                span.set_attribute("webhook.error", str(e))
                span.set_attribute("webhook.duration", duration_sec)
                span.record_exception(e)

                logger.error(
                    f"{self.webhook_name} failed to process request {uid}: {e}"
                )

                return JSONResponse(
                    content=self.create_admission_response(
                        uid=uid,
                        allowed=False,
                        message=f"Internal error: {str(e)}",
                    )
                )

    def _record_metrics(
        self,
        operation: str,
        success: bool,
        duration: float,
        error: Optional[str] = None,
    ):
        labels = {}
        if error:
            labels["error"] = error

        self.metrics.record_webhook_request(
            webhook_name=self.webhook_name,
            operation=operation,
            success=success,
            duration=duration,
            **labels,
        )
