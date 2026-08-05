#!/usr/bin/env python3
# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.


from __future__ import annotations

import math
import re
from pathlib import Path
from typing import Any

_METRICS_TOP_KEY_ORDER: tuple[str, ...] = (
    "query_doc_id",
    "state",
    "query_id",
    "app_id",
    "app_name",
    "job_id",
    "collected_at",
    "collection_state",
    "summary",
    "summary_field_definitions",
    "executor_aggregates",
    "prep_phase",
    "resource_snapshot",
    "pod_lifecycle",
)


def reorder_metrics_top_keys(doc: Any) -> Any:
    if not isinstance(doc, dict):
        return doc
    front = {k: doc[k] for k in _METRICS_TOP_KEY_ORDER if k in doc}
    rest = {k: v for k, v in doc.items() if k not in front}
    front.update(rest)
    # Apply nested orderings so the per-section keys also scan logically.
    if isinstance(front.get("summary"), dict):
        front["summary"] = _ordered(front["summary"], _SUMMARY_KEY_ORDER)
    if isinstance(front.get("prep_phase"), dict):
        front["prep_phase"] = _ordered(front["prep_phase"], _PREP_PHASE_KEY_ORDER)
    return front


# Order within `summary`: status -> wall-clock -> task-time -> executor totals
# -> topology -> CPU% -> GC% -> memory -> derived rates -> I/O -> shuffle ->
# stage/job counts. Goal: a human scanning the block sees pass/fail first,
# then time, then resource usage, then volume.
_SUMMARY_KEY_ORDER: tuple[str, ...] = (
    # Run outcome.
    "status",
    "jobs_failed",
    "sql_executions_failed",
    "total_tasks_failed",
    "stage_retry_count",
    "stage_retry_pct",
    # Wall clock & task time.
    "wall_clock_duration_seconds",
    "sql_execution_duration_seconds",
    "stage_queue_wait_mean_seconds",
    "stage_queue_wait_max_seconds",
    # Executor time accumulators.
    "total_executor_run_time_seconds",
    "total_executor_cpu_time_seconds",
    "total_gc_time_seconds",
    # Topology.
    "executor_cores",
    "driver_cores",
    "max_executors",
    "executors_used",
    # CPU utilisation rollups.
    "task_cpu_efficiency_pct",
    "cluster_cpu_utilization_pct",
    "executor_cpu_max_pct_of_executor_5m",
    "executor_cpu_max_pct_of_cluster_5m",
    "driver_cpu_max_pct_of_driver_5m",
    # GC rollups.
    "executor_gc_max_pct_of_wallclock_5m",
    "driver_gc_max_pct_of_wallclock_5m",
    "executor_gc_pct_of_run_time",
    # Memory peaks.
    "peak_executor_memory_bytes",
    "peak_unified_memory_bytes",
    "peak_driver_memory_bytes",
    # Spill.
    "total_disk_bytes_spilled",
    "total_memory_bytes_spilled",
    # I/O.
    "total_input_bytes",
    "total_output_bytes",
    "total_files_read",
    "total_records_read",
    "total_records_written",
    # Shuffle.
    "total_shuffle_read_bytes",
    "total_shuffle_write_bytes",
    "total_shuffle_read_records",
    "total_shuffle_write_records",
    # Counts.
    "total_jobs",
    "total_stages",
    "total_stage_attempts",
    "total_tasks",
)


# Within `prep_phase` we put the compact summaries (durations + offsets)
# before the long arrays so the block remains readable at a glance.
_PREP_PHASE_KEY_ORDER: tuple[str, ...] = (
    "submit_time",
    "running_since",
    "end_time",
    "phase_durations_sec",
    "timeline_offsets_sec",
    "dataset_loads",
    "init_containers",
)


def _ordered(d: dict[str, Any], order: tuple[str, ...]) -> dict[str, Any]:
    front = {k: d[k] for k in order if k in d}
    rest = {k: v for k, v in d.items() if k not in front}
    front.update(rest)
    return front


_QID_RE = re.compile(
    r"^tpcds-(?:(?P<friendly>query\d+)(?P<suffix>[a-z]?)"
    r"|(?P<friendly_alt>[A-Za-z][A-Za-z0-9_]*))"
    r"-(?P<fmt>[^-]+)-"
)


def parse_tpcds_query_id(qid: str) -> tuple[str | None, str | None, str | None]:
    m = _QID_RE.match(qid or "")
    if not m:
        return None, None, None
    friendly = m.group("friendly") or m.group("friendly_alt")
    suffix = m.group("suffix") or ""
    return friendly, suffix, m.group("fmt")


def extract_scale_factor(
    metrics: dict[str, Any], results: list[dict], path: Path
) -> int | None:
    def _coerce(value: Any) -> int | None:
        if isinstance(value, int):
            return value
        if isinstance(value, str) and value.isdigit():
            return int(value)
        return None

    sf = _coerce((metrics.get("config") or {}).get("scale_factor"))
    if sf is not None:
        return sf
    for row in results:
        sf = _coerce(row.get("scale_factor"))
        if sf is not None:
            return sf
    match = re.search(r"sf(\d+)", path.name)
    return int(match.group(1)) if match else None


def exclude_duration_outliers(
    values: list[float],
    *,
    multiplier: float = 3.0,
) -> list[float]:
    if not values:
        return []
    ordered = sorted(values)
    mid = len(ordered) // 2
    if len(ordered) % 2:
        median = ordered[mid]
    else:
        median = (ordered[mid - 1] + ordered[mid]) / 2.0
    cutoff = median * multiplier
    filtered = [value for value in values if value <= cutoff]
    return filtered or values


def rank_percentile(values: list[float], p: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    index = max(0, math.ceil((p / 100.0) * len(ordered)) - 1)
    return ordered[index]
