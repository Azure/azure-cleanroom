"""Models for the JobEventRecord CRD that persists Spark job events.

Kubernetes garbage-collects core events ~1h after creation, so the operational
and statistics events recorded against a SparkApplication disappear from the
status response. These events are mirrored into a JobEventRecord CRD (one per
job id) so they survive and can be returned by the status endpoint indefinitely.
"""

from __future__ import annotations

from datetime import datetime
from typing import List, Optional

from pydantic import BaseModel, ConfigDict, Field


class CamelCaseModel(BaseModel):
    """Base model with camelCase alias support."""

    model_config = ConfigDict(populate_by_name=True)


class PersistedJobEvent(CamelCaseModel):
    """A single job event mirrored from a Kubernetes event."""

    name: Optional[str] = Field(default=None)
    reason: str = Field(default="")
    message: str = Field(default="")
    type: str = Field(default="Normal")
    first_timestamp: Optional[datetime] = Field(default=None, alias="firstTimestamp")
    last_timestamp: Optional[datetime] = Field(default=None, alias="lastTimestamp")
    count: int = Field(default=1)


class JobEventRecordSpec(CamelCaseModel):
    """Spec for the JobEventRecord CRD."""

    job_id: str = Field(alias="jobId")


class JobEventRecordStatus(CamelCaseModel):
    """Status for the JobEventRecord CRD."""

    events: List[PersistedJobEvent] = Field(default_factory=list)


class JobEventRecord(CamelCaseModel):
    """JobEventRecord CRD model for persisting Spark job events."""

    api_version: str = Field(default="cleanroom.azure.com/v1alpha1", alias="apiVersion")
    kind: str = Field(default="JobEventRecord")
    metadata: Optional[dict] = Field(default=None)
    spec: JobEventRecordSpec
    status: Optional[JobEventRecordStatus] = Field(default=None)
