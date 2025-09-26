import time
from typing import Any, Dict, Optional

from opentelemetry import metrics


class SparkFrontendMetrics:
    """Custom metrics for Spark Frontend service"""

    def __init__(self, meter_name: str = "cleanroom-spark-frontend"):
        self.meter = metrics.get_meter(meter_name)

        # Counters
        self.job_submissions_total = self.meter.create_counter(
            name="spark_job_submissions_total",
            description="Total number of Spark job submissions",
            unit="1",
        )

        self.job_submissions_failed = self.meter.create_counter(
            name="spark_job_submissions_failed_total",
            description="Total number of failed Spark job submissions",
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

        self.spark_jobs_failed_total = self.meter.create_counter(
            name="spark_job_failed_total",
            description="Total number of Spark job failures",
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
        self.spark_active_jobs = self.meter.create_up_down_counter(
            name="spark_active_jobs_total",
            description="The number of active spark jobs",
            unit="1",
        )

        # Histograms
        self.job_submission_duration = self.meter.create_histogram(
            name="spark_job_submission_duration_seconds",
            description="Time taken to submit Spark jobs",
            unit="s",
        )

        self.http_request_duration = self.meter.create_histogram(
            name="http_request_duration_seconds",
            description="HTTP request duration",
            unit="s",
        )

        self.spark_job_duration = self.meter.create_histogram(
            name="spark_job_duration_seconds",
            description="Spark job request duration",
            unit="s",
        )

        self.k8s_operation_duration = self.meter.create_histogram(
            name="k8s_operation_duration_seconds",
            description="Kubernetes operation duration",
            unit="s",
        )

    def record_job_submission(
        self, job_type: str, success: bool, duration: float, **labels
    ):
        """Record job submission metrics"""
        base_attributes = {
            "job_type": job_type,
            "success": str(success).lower(),
            **labels,
        }

        self.job_submissions_total.add(1, base_attributes)

        if not success:
            self.job_submissions_failed.add(1, base_attributes)
        else:
            self.spark_active_jobs.add(1, base_attributes)

        self.job_submission_duration.record(duration, base_attributes)

    def record_job_completion(
        self, job_type: str, success: bool, duration: float, **labels
    ):
        base_attributes = {
            "job_type": job_type,
            "success": str(success).lower(),
            **labels,
        }

        # Decrement the number of active jobs
        self.spark_active_jobs.add(-1, base_attributes)

        if not success:
            self.spark_jobs_failed_total.add(1, base_attributes)

        self.spark_job_duration.record(duration, base_attributes)

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
        **labels
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
_metrics_instance: Optional[SparkFrontendMetrics] = None


def get_metrics() -> SparkFrontendMetrics:
    """Get the global metrics instance"""
    global _metrics_instance
    if _metrics_instance is None:
        _metrics_instance = SparkFrontendMetrics()
    return _metrics_instance
