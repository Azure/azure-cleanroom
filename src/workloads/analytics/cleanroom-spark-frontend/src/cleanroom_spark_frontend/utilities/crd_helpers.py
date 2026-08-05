"""Shared helpers for the cleanroom CRD clients (JobRecord, JobEventRecord)."""

import logging

import kubernetes
from tenacity import (
    before_sleep_log,
    retry,
    retry_if_exception,
    stop_after_attempt,
    wait_exponential,
    wait_random,
)

from .constants import CrdConstants

logger = logging.getLogger("crd_helpers")


def is_conflict_error(exception: Exception) -> bool:
    """Return True if the exception is a Kubernetes 409 (conflict) error."""
    return (
        isinstance(exception, kubernetes.client.ApiException)
        and exception.status == 409
    )


def conflict_retry():
    """Tenacity retry decorator for optimistic-concurrency (409) conflicts."""
    return retry(
        retry=retry_if_exception(is_conflict_error),
        stop=stop_after_attempt(CrdConstants.MAX_RETRY_ATTEMPTS),
        wait=wait_exponential(
            multiplier=CrdConstants.RETRY_MULTIPLIER,
            min=CrdConstants.RETRY_MIN_WAIT,
            max=CrdConstants.RETRY_MAX_WAIT,
        )
        + wait_random(0, CrdConstants.RETRY_JITTER),
        before_sleep=before_sleep_log(logger, logging.WARNING),
        reraise=True,
    )
