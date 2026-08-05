"""Client for interacting with the JobEventRecord CRD in Kubernetes.

Persists operational/statistics events for a Spark job so they outlive the
~1h Kubernetes core-event garbage collection window.
"""

import logging
from typing import Optional

import kubernetes

from ..models.job_event_models import JobEventRecord, PersistedJobEvent
from ..utilities.constants import JobEventRecordConstants
from ..utilities.crd_helpers import conflict_retry
from ..utilities.helpers import log_safe

logger = logging.getLogger("job_event_record_client")


class JobEventRecordError(Exception):
    """Base exception for JobEventRecord operations."""


class JobEventRecordConflictError(JobEventRecordError):
    """Raised when a JobEventRecord update conflicts after all retries."""


class JobEventRecordClient:
    """Client for managing JobEventRecord custom resources in Kubernetes."""

    _RESOURCE_PARAMS = {
        "group": JobEventRecordConstants.GROUP,
        "version": JobEventRecordConstants.VERSION,
        "plural": JobEventRecordConstants.PLURAL,
    }

    def __init__(self):
        self._custom_objects_api = kubernetes.client.CustomObjectsApi()

    def get_event_record(self, job_id: str, namespace: str) -> Optional[JobEventRecord]:
        """Get a JobEventRecord by job ID. Returns None if not found."""
        try:
            result = self._custom_objects_api.get_namespaced_custom_object(
                **self._RESOURCE_PARAMS,
                namespace=namespace,
                name=job_id,
            )
            return JobEventRecord.model_validate(result)
        except kubernetes.client.ApiException as e:
            if e.status == 404:
                return None
            logger.error(
                f"Failed to get JobEventRecord: job_id={log_safe(job_id)}, namespace={namespace}, error={e}"
            )
            raise

    def add_event(
        self,
        job_id: str,
        namespace: str,
        event: PersistedJobEvent,
        owner_references: Optional[list[dict]] = None,
        tags: Optional[dict[str, str]] = None,
    ) -> JobEventRecord:
        """Append an event to a JobEventRecord. Optimistic-locked with retry.

        When the record has to be created, ``owner_references`` (if given) are
        stamped onto its metadata so the Kubernetes garbage collector
        cascade-deletes it when its owner (the SparkApplication) is deleted -
        e.g. when the app's TTL expires - so events expire together with the
        app. ``tags`` (if given) are copied verbatim onto the record as labels;
        the query-id tag groups runs of the same query for the per-query
        retention sweep.
        """
        try:
            return self._add_event_with_retry(
                job_id, namespace, event, owner_references, tags
            )
        except kubernetes.client.ApiException as e:
            if e.status == 409:
                raise JobEventRecordConflictError(
                    f"Failed to add event to JobEventRecord {job_id} after "
                    f"{JobEventRecordConstants.MAX_RETRY_ATTEMPTS} retries"
                ) from e
            raise

    def _create_event_record(
        self,
        job_id: str,
        namespace: str,
        owner_references: Optional[list[dict]] = None,
        tags: Optional[dict[str, str]] = None,
    ) -> JobEventRecord:
        labels = {
            "app.kubernetes.io/name": "jobeventrecord",
        }
        if tags:
            labels.update(tags)
        metadata = {
            "name": job_id,
            "namespace": namespace,
            "labels": labels,
            "annotations": {
                "cleanroom.azure.com/created-by": "cleanroom-spark-frontend",
            },
        }
        if owner_references:
            metadata["ownerReferences"] = owner_references
        body = {
            "apiVersion": f"{JobEventRecordConstants.GROUP}/{JobEventRecordConstants.VERSION}",
            "kind": JobEventRecordConstants.KIND,
            "metadata": metadata,
            "spec": {
                "jobId": job_id,
            },
        }
        result = self._custom_objects_api.create_namespaced_custom_object(
            **self._RESOURCE_PARAMS,
            namespace=namespace,
            body=body,
        )
        logger.info(
            f"Created JobEventRecord: job_id={log_safe(job_id)}, namespace={namespace}"
        )
        # A new record for this query was just created; enforce the per-query
        # retention cap by pruning the oldest records beyond the limit. This is
        # best-effort and must never fail event persistence.
        query_id = (tags or {}).get(JobEventRecordConstants.QUERY_ID_LABEL)
        if query_id:
            self._prune_query_records(query_id, namespace)
        return JobEventRecord.model_validate(result)

    def _prune_query_records(self, query_id: str, namespace: str) -> None:
        """Delete the oldest JobEventRecords for *query_id* beyond the per-query
        retention limit. Best-effort: logs and swallows any failure."""
        try:
            result = self._custom_objects_api.list_namespaced_custom_object(
                **self._RESOURCE_PARAMS,
                namespace=namespace,
                label_selector=(f"{JobEventRecordConstants.QUERY_ID_LABEL}={query_id}"),
            )
            items = result.get("items", [])
            keep_last = JobEventRecordConstants.PER_QUERY_LIMIT
            if len(items) <= keep_last:
                return
            # Newest first by creationTimestamp (RFC3339 sorts lexicographically).
            items.sort(
                key=lambda r: r.get("metadata", {}).get("creationTimestamp", ""),
                reverse=True,
            )
            for stale in items[keep_last:]:
                name = stale.get("metadata", {}).get("name")
                if not name:
                    continue
                try:
                    self._custom_objects_api.delete_namespaced_custom_object(
                        **self._RESOURCE_PARAMS,
                        namespace=namespace,
                        name=name,
                    )
                    logger.info(
                        f"Pruned JobEventRecord beyond per-query limit: "
                        f"name={log_safe(name)}, query_id={log_safe(query_id)}"
                    )
                except kubernetes.client.ApiException as e:
                    if e.status != 404:
                        logger.warning(
                            f"Failed to prune JobEventRecord {log_safe(name)}: {e}"
                        )
        except Exception as e:
            logger.warning(
                f"Failed to prune JobEventRecords for query_id={log_safe(query_id)}: {e}"
            )

    def _get_or_create_event_record(
        self,
        job_id: str,
        namespace: str,
        owner_references: Optional[list[dict]] = None,
        tags: Optional[dict[str, str]] = None,
    ) -> JobEventRecord:
        record = self.get_event_record(job_id, namespace)
        if record is None:
            try:
                record = self._create_event_record(
                    job_id, namespace, owner_references, tags
                )
            except kubernetes.client.ApiException as e:
                if e.status == 409:
                    # Race: another writer created it first; fetch it.
                    record = self.get_event_record(job_id, namespace)
                    if record is None:
                        raise JobEventRecordError(
                            f"Failed to get JobEventRecord {job_id} after conflict"
                        )
                else:
                    raise
        return record

    @staticmethod
    def _bound_message(event: PersistedJobEvent) -> PersistedJobEvent:
        """Truncate an over-long event message so a single event (e.g. a stack
        trace) can't bloat the record. Returns the event unchanged if within
        the limit."""
        limit = JobEventRecordConstants.MAX_EVENT_MESSAGE_CHARS
        if len(event.message) <= limit:
            return event
        event.message = event.message[:limit] + "…[truncated]"
        return event

    @conflict_retry()
    def _add_event_with_retry(
        self,
        job_id: str,
        namespace: str,
        event: PersistedJobEvent,
        owner_references: Optional[list[dict]] = None,
        tags: Optional[dict[str, str]] = None,
    ) -> JobEventRecord:
        record = self._get_or_create_event_record(
            job_id, namespace, owner_references, tags
        )
        resource_version = record.metadata["resourceVersion"]
        existing = record.status.events if record.status else []

        event = self._bound_message(event)
        updated = [e for e in existing if e.name != event.name]
        updated.append(event)
        updated = updated[-JobEventRecordConstants.MAX_EVENTS_PER_RECORD :]

        status_body = {
            "apiVersion": f"{JobEventRecordConstants.GROUP}/{JobEventRecordConstants.VERSION}",
            "kind": JobEventRecordConstants.KIND,
            "metadata": {
                "name": job_id,
                "resourceVersion": resource_version,
            },
            "status": {
                "events": [e.model_dump(by_alias=True) for e in updated],
            },
        }
        try:
            result = self._custom_objects_api.patch_namespaced_custom_object_status(
                **self._RESOURCE_PARAMS,
                namespace=namespace,
                name=job_id,
                body=status_body,
            )
            return JobEventRecord.model_validate(result)
        except kubernetes.client.ApiException as e:
            if e.status == 409:
                logger.warning(
                    f"Conflict updating JobEventRecord, will retry: job_id={log_safe(job_id)}, "
                    f"namespace={namespace}, resourceVersion={resource_version}"
                )
            else:
                logger.error(
                    f"Failed to update JobEventRecord: job_id={log_safe(job_id)}, "
                    f"namespace={namespace}, error={e}"
                )
            raise
