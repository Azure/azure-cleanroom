"""General helper utilities."""

import hashlib
import re
from datetime import datetime, timezone


def utc_now() -> datetime:
    """Return the current UTC time with microseconds truncated."""
    return datetime.now(timezone.utc).replace(microsecond=0)


def generate_query_id(query: str) -> str:
    """
    Generate a unique query_id from a SQL query string using SHA-256 hash.
    """
    return hashlib.sha256(query.encode()).hexdigest()[:32]


def to_spark_app_name(name: str) -> str:
    """
    Convert a name to a valid Spark application name.
    Spark application names must be lowercase and can only contain
    alphanumeric characters and hyphens.
    """
    name = "cl-spark-" + re.sub(r"[^a-z0-9-]", "-", name.lower())
    return name[:63]
