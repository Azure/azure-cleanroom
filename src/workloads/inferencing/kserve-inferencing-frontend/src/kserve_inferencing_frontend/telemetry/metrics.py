from typing import Optional

from opentelemetry import metrics


class KServeFrontendMetrics:
    """Custom metrics for KServe Frontend service"""

    def __init__(self, meter_name: str = "kserve-inferencing-frontend"):
        self.meter = metrics.get_meter(meter_name)

        # Counters
        self.job_submissions_total = self.meter.create_counter(
            name="kserve_job_submissions_total",
            description="Total number of inference service submissions",
            unit="1",
        )

        self.job_submissions_failed = self.meter.create_counter(
            name="kserve_job_submissions_failed_total",
            description="Total number of failed inference service submissions",
            unit="1",
        )

        self.http_requests_total = self.meter.create_counter(
            name="http_requests_total",
            description="Total number of HTTP requests",
            unit="1",
        )

        self.http_requests_failed = self.meter.create_counter(
            name="http_requests_failed_total",
            description="Total number of failed HTTP requests (status_code >= 400)",
            unit="1",
        )

        self.kserve_jobs_failed_total = self.meter.create_counter(
            name="kserve_job_failed_total",
            description="Total number of Inference Service failures",
            unit="1",
        )

        self.k8s_operations_total = self.meter.create_counter(
            name="k8s_operations_total",
            description="Total number of Kubernetes operations",
            unit="1",
        )

        self.k8s_operations_failed = self.meter.create_counter(
            name="k8s_operations_failed_total",
            description="Total number of failed Kubernetes operations",
            unit="1",
        )

        # Up down counters
        self.kserve_active_jobs = self.meter.create_up_down_counter(
            name="kserve_active_jobs_total",
            description="The number of active kserve jobs",
            unit="1",
        )

        # Histograms
        self.job_submission_duration = self.meter.create_histogram(
            name="kserve_job_submission_duration_seconds",
            description="Time taken to submit inference services",
            unit="s",
        )

        self.http_request_duration = self.meter.create_histogram(
            name="http_request_duration_seconds",
            description="HTTP request duration",
            unit="s",
        )

        self.kserve_job_duration = self.meter.create_histogram(
            name="kserve_job_duration_seconds",
            description="Inference service request duration",
            unit="s",
        )

        self.k8s_operation_duration = self.meter.create_histogram(
            name="k8s_operation_duration_seconds",
            description="Kubernetes operation duration",
            unit="s",
        )

    def record_job_submission(self, success: bool, duration: float, **labels):
        """Record job submission metrics"""
        base_attributes = {
            "success": str(success).lower(),
            **labels,
        }

        self.job_submissions_total.add(1, base_attributes)

        if not success:
            self.job_submissions_failed.add(1, base_attributes)
        else:
            self.kserve_active_jobs.add(1, base_attributes)

        self.job_submission_duration.record(duration, base_attributes)

    def record_job_completion(
        self, job_type: str, success: bool, duration: float, **labels
    ):
        """Record job completion metrics."""
        base_attributes = {
            "job_type": job_type,
            "success": str(success).lower(),
            **labels,
        }

        self.kserve_active_jobs.add(-1, base_attributes)

        if not success:
            self.kserve_jobs_failed_total.add(1, base_attributes)

        self.kserve_job_duration.record(duration, base_attributes)

    def record_http_request(
        self, method: str, path: str, status_code: int, duration: float
    ):
        """Record HTTP request metrics"""
        attributes = {
            "method": method,
            "path": path,
            "status_code": str(status_code),
        }

        self.http_requests_total.add(1, attributes)
        self.http_request_duration.record(duration, attributes)
        if status_code >= 400:
            self.http_requests_failed.add(1, attributes)

    def record_k8s_operation(
        self,
        operation: str,
        resource_kind: str,
        success: bool,
        duration: float,
        **labels,
    ):
        """Record Kubernetes operation metrics"""
        base_attributes = {
            "operation": operation,
            "resource_kind": resource_kind,
            "success": str(success).lower(),
            **labels,
        }

        self.k8s_operations_total.add(1, base_attributes)

        if not success:
            self.k8s_operations_failed.add(1, base_attributes)

        self.k8s_operation_duration.record(duration, base_attributes)


# Global metrics instance
_metrics_instance: Optional[KServeFrontendMetrics] = None


def get_metrics() -> KServeFrontendMetrics:
    """Get the global metrics instance"""
    global _metrics_instance
    if _metrics_instance is None:
        _metrics_instance = KServeFrontendMetrics()
    return _metrics_instance
