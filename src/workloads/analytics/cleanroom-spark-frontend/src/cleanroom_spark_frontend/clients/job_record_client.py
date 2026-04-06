"""Client for interacting with JobRecord CRD in Kubernetes."""

import logging
from typing import Optional

import kubernetes
from tenacity import (
    before_sleep_log,
    retry,
    retry_if_exception,
    stop_after_attempt,
    wait_exponential,
    wait_random,
)

from ..models.job_record_models import (
    JobRecord,
    JobRecordSummary,
    JobRun,
)
from ..utilities.constants import JobRecordConstants

logger = logging.getLogger("job_record_client")


def _is_conflict_error(exception: Exception) -> bool:
    return (
        isinstance(exception, kubernetes.client.ApiException)
        and exception.status == 409
    )


def _conflict_retry():
    return retry(
        retry=retry_if_exception(_is_conflict_error),
        stop=stop_after_attempt(JobRecordConstants.MAX_RETRY_ATTEMPTS),
        wait=wait_exponential(
            multiplier=JobRecordConstants.RETRY_MULTIPLIER,
            min=JobRecordConstants.RETRY_MIN_WAIT,
            max=JobRecordConstants.RETRY_MAX_WAIT,
        )
        + wait_random(0, JobRecordConstants.RETRY_JITTER),
        before_sleep=before_sleep_log(logger, logging.WARNING),
        reraise=True,
    )


def _apply_history_limit(runs: list[JobRun]) -> list[JobRun]:
    return runs[-JobRecordConstants.HISTORY_LIMIT :]


class JobRecordError(Exception):
    """Base exception for JobRecord operations."""

    pass


class JobRecordConflictError(JobRecordError):
    """Raised when there's a conflict updating a JobRecord after all retries."""

    pass


class JobRecordClient:
    """Client for managing JobRecord custom resources in Kubernetes."""

    _RESOURCE_PARAMS = {
        "group": JobRecordConstants.GROUP,
        "version": JobRecordConstants.VERSION,
        "plural": JobRecordConstants.PLURAL,
    }

    def __init__(self):
        self._custom_objects_api = kubernetes.client.CustomObjectsApi()

    def get_job_record(self, query_id: str, namespace: str) -> Optional[JobRecord]:
        """Get a JobRecord by query ID. Returns None if not found."""
        try:
            result = self._custom_objects_api.get_namespaced_custom_object(
                **self._RESOURCE_PARAMS,
                namespace=namespace,
                name=query_id,
            )
            return self._parse_job_record(result)
        except kubernetes.client.ApiException as e:
            if e.status == 404:
                logger.info(
                    f"JobRecord not found: query_id={query_id}, namespace={namespace}"
                )
                return None
            logger.error(
                f"Failed to get JobRecord: query_id={query_id}, namespace={namespace}, error={e}"
            )
            raise

    def _create_job_record(self, query_id: str, namespace: str) -> JobRecord:
        """Create a new JobRecord."""
        body = {
            "apiVersion": f"{JobRecordConstants.GROUP}/{JobRecordConstants.VERSION}",
            "kind": JobRecordConstants.KIND,
            "metadata": {
                "name": query_id,
                "namespace": namespace,
                "labels": {
                    "app.kubernetes.io/name": "jobrecord",
                },
                "annotations": {
                    "cleanroom.azure.com/created-by": "cleanroom-spark-frontend",
                },
            },
            "spec": {
                "queryId": query_id,
            },
        }

        try:
            result = self._custom_objects_api.create_namespaced_custom_object(
                **self._RESOURCE_PARAMS,
                namespace=namespace,
                body=body,
            )
            logger.info(
                f"Created JobRecord: query_id={query_id}, namespace={namespace}"
            )
            return self._parse_job_record(result)
        except kubernetes.client.ApiException as e:
            logger.error(
                f"Failed to create JobRecord: query_id={query_id}, namespace={namespace}, error={e}"
            )
            raise

    def _get_or_create_job_record(self, query_id: str, namespace: str) -> JobRecord:
        """Get existing JobRecord or create new one. Handles race conditions."""
        job_record = self.get_job_record(query_id, namespace)
        if job_record is None:
            try:
                job_record = self._create_job_record(query_id, namespace)
            except kubernetes.client.ApiException as e:
                if e.status == 409:
                    # Race condition: another client created it first, fetch it
                    logger.info(
                        f"JobRecord created by another client, fetching: query_id={query_id}, namespace={namespace}"
                    )
                    job_record = self.get_job_record(query_id, namespace)
                    if job_record is None:
                        raise JobRecordError(
                            f"Failed to get JobRecord {query_id} after conflict"
                        )
                else:
                    raise
        return job_record

    def add_run(self, query_id: str, namespace: str, run: JobRun) -> JobRecord:
        """Add a new run to a JobRecord. Uses optimistic locking with retry on conflicts."""
        try:
            return self._add_run_with_retry(query_id, namespace, run)
        except kubernetes.client.ApiException as e:
            if e.status == 409:
                raise JobRecordConflictError(
                    f"Failed to add run to JobRecord {query_id} after "
                    f"{JobRecordConstants.MAX_RETRY_ATTEMPTS} retries"
                ) from e
            raise

    def _get_job_record_context(
        self, query_id: str, namespace: str
    ) -> tuple[JobRecord, str, list[JobRun], JobRecordSummary]:
        """Get job record with context for updates. Returns (job_record, resource_version, runs, summary)."""
        job_record = self._get_or_create_job_record(query_id, namespace)
        resource_version = job_record.metadata["resourceVersion"]
        existing_runs = job_record.status.runs if job_record.status else []
        existing_summary = (
            job_record.status.summary
            if job_record.status and job_record.status.summary
            else JobRecordSummary()
        )
        return job_record, resource_version, existing_runs, existing_summary

    @_conflict_retry()
    def _add_run_with_retry(
        self, query_id: str, namespace: str, run: JobRun
    ) -> JobRecord:
        job_record, resource_version, existing_runs, existing_summary = (
            self._get_job_record_context(query_id, namespace)
        )

        existing_run_ids = {r.run_id for r in existing_runs}
        if run.run_id in existing_run_ids:
            logger.info(
                f"Run already exists, skipping: query_id={query_id}, namespace={namespace}, run_id={run.run_id}"
            )
            return job_record

        updated_runs = _apply_history_limit(existing_runs + [run])

        return self._patch_job_record_status(
            query_id, namespace, resource_version, run, updated_runs, existing_summary
        )

    def _patch_job_record_status(
        self,
        query_id: str,
        namespace: str,
        resource_version: str,
        latest_run: JobRun,
        runs: list[JobRun],
        existing_summary: JobRecordSummary,
    ) -> JobRecord:
        summary = existing_summary.accumulate(latest_run)

        status_body = {
            "apiVersion": f"{JobRecordConstants.GROUP}/{JobRecordConstants.VERSION}",
            "kind": JobRecordConstants.KIND,
            "metadata": {
                "name": query_id,
                "resourceVersion": resource_version,
            },
            "status": {
                "latestRun": latest_run.model_dump(by_alias=True),
                "runs": [r.model_dump(by_alias=True) for r in runs],
                "summary": summary.model_dump(by_alias=True),
            },
        }

        try:
            result = self._custom_objects_api.patch_namespaced_custom_object_status(
                **self._RESOURCE_PARAMS,
                namespace=namespace,
                name=query_id,
                body=status_body,
            )
            logger.info(
                f"Updated JobRecord: query_id={query_id}, namespace={namespace}, run_id={latest_run.run_id}"
            )
            return self._parse_job_record(result)
        except kubernetes.client.ApiException as e:
            if e.status == 409:
                logger.warning(
                    f"Conflict updating JobRecord, will retry: query_id={query_id}, namespace={namespace}, "
                    f"run_id={latest_run.run_id}, resourceVersion={resource_version}"
                )
            else:
                logger.error(
                    f"Failed to update JobRecord: query_id={query_id}, namespace={namespace}, "
                    f"run_id={latest_run.run_id}, error={e}"
                )
            raise

    def _parse_job_record(self, job_record_data: dict) -> JobRecord:
        return JobRecord.model_validate(job_record_data)
