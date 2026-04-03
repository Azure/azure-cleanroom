"""Models for JobRecord CRD to track Spark job runs and statistics."""

from __future__ import annotations

import uuid
from datetime import datetime
from typing import List, Optional

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator

from .spark_application_models import ApplicationStateEnum, SparkErrorCode


class CamelCaseModel(BaseModel):
    """Base model with camelCase alias support."""

    model_config = ConfigDict(populate_by_name=True)


class JobRunError(CamelCaseModel):
    """Error details for a failed job run."""

    code: str = Field(default="")
    message: str = Field(default="")

    @classmethod
    def from_state(
        cls, state: ApplicationStateEnum, error_message: Optional[str] = None
    ) -> JobRunError:
        """Create a JobRunError from application state."""
        return cls(
            code=SparkErrorCode.from_state(state),
            message=error_message or f"Spark job terminated with state: {state.value}",
        )


class JobRunStats(CamelCaseModel):
    """Statistics for a single job run."""

    rows_read: int = Field(default=0, alias="rowsRead")
    rows_written: int = Field(default=0, alias="rowsWritten")


class JobRun(CamelCaseModel):
    """Represents a single run of a job."""

    run_id: str = Field(
        default_factory=lambda: str(uuid.uuid4()),
        alias="runId",
    )
    start_time: Optional[datetime] = Field(default=None, alias="startTime")
    end_time: Optional[datetime] = Field(default=None, alias="endTime")
    is_successful: bool = Field(default=False, alias="isSuccessful")
    error: Optional[JobRunError] = Field(default=None)
    stats: Optional[JobRunStats] = Field(default=None)

    @field_validator("start_time", "end_time", mode="before")
    @classmethod
    def parse_datetime(cls, v):
        return _parse_datetime(v)

    @model_validator(mode="after")
    def validate_times(self) -> JobRun:
        """Validate that end_time is after start_time if both are provided."""
        if self.start_time and self.end_time and self.end_time < self.start_time:
            raise ValueError("end_time must be after start_time")
        return self

    @property
    def duration_seconds(self) -> float:
        """Duration of the run in seconds, or 0.0 if times are not set."""
        if self.start_time and self.end_time:
            return (self.end_time - self.start_time).total_seconds()
        return 0.0


class JobRecordSummary(CamelCaseModel):
    """Aggregated statistics across all tracked runs."""

    total_runs: int = Field(default=0, alias="totalRuns")
    successful_runs: int = Field(default=0, alias="successfulRuns")
    failed_runs: int = Field(default=0, alias="failedRuns")
    total_runtime_seconds: float = Field(default=0.0, alias="totalRuntimeSeconds")
    avg_duration_seconds: float = Field(default=0.0, alias="avgDurationSeconds")
    total_rows_read: int = Field(default=0, alias="totalRowsRead")
    total_rows_written: int = Field(default=0, alias="totalRowsWritten")

    def accumulate(self, run: JobRun) -> JobRecordSummary:
        """Return a new summary with the run's statistics accumulated."""
        total_runs = self.total_runs + 1
        total_runtime = self.total_runtime_seconds + run.duration_seconds

        return JobRecordSummary(
            total_runs=total_runs,
            successful_runs=self.successful_runs + int(run.is_successful),
            failed_runs=self.failed_runs + int(not run.is_successful),
            total_runtime_seconds=round(total_runtime, 2),
            avg_duration_seconds=round(total_runtime / total_runs, 2),
            total_rows_read=self.total_rows_read
            + (run.stats.rows_read if run.stats else 0),
            total_rows_written=self.total_rows_written
            + (run.stats.rows_written if run.stats else 0),
        )


class JobRecordSpec(CamelCaseModel):
    """Spec for JobRecord CRD."""

    query_id: str = Field(alias="queryId")


class JobRecordStatus(CamelCaseModel):
    """Status for JobRecord CRD."""

    latest_run: Optional[JobRun] = Field(default=None, alias="latestRun")
    runs: List[JobRun] = Field(default_factory=list)
    summary: Optional[JobRecordSummary] = Field(default=None)


class JobRecord(CamelCaseModel):
    """JobRecord CRD model for tracking Spark job runs."""

    api_version: str = Field(default="cleanroom.azure.com/v1alpha1", alias="apiVersion")
    kind: str = Field(default="JobRecord")
    metadata: Optional[dict] = Field(default=None)
    spec: JobRecordSpec
    status: Optional[JobRecordStatus] = Field(default=None)


class JobRecordResponse(CamelCaseModel):
    """Response model for job record API."""

    query_id: str = Field(alias="queryId")
    latest_run: Optional[JobRun] = Field(default=None, alias="latestRun")
    runs: List[JobRun] = Field(default_factory=list)
    summary: Optional[JobRecordSummary] = Field(default=None)

    @classmethod
    def from_job_record(cls, job_record: JobRecord) -> JobRecordResponse:
        """Create a response from a JobRecord CRD."""
        status = job_record.status or JobRecordStatus()
        return cls(
            query_id=job_record.spec.query_id,
            latest_run=status.latest_run,
            runs=status.runs,
            summary=status.summary,
        )


def _parse_datetime(value):
    """Parse datetime from various formats."""
    if value is None:
        return None
    if isinstance(value, datetime):
        return value
    return datetime.fromisoformat(value.replace("Z", "+00:00"))
