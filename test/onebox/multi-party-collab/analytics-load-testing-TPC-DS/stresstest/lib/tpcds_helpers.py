#!/usr/bin/env python3
# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.


from __future__ import annotations

import json
import os
import re
import subprocess
import time
import urllib.parse
import urllib.request
import uuid
from collections.abc import Callable
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from format_utils import reorder_metrics_top_keys

DEFAULT_POLL_INTERVAL = 15
KUBECTL_PROXY_PORT = 8181
ANALYTICS_NAMESPACE = "analytics"

PROMETHEUS_NAMESPACE = os.environ.get("PROMETHEUS_NAMESPACE", "observability")
PROMETHEUS_SERVICE = os.environ.get("PROMETHEUS_SERVICE", "cleanroom-prometheus-server")

STATUS_CHECK_INTERVAL_SECONDS = 60
STATUS_CHECK_MAX_INTERVAL_SECONDS = 300
STUCK_IN_INIT_TIMEOUT_SECONDS = 15 * 60
MAX_STUCK_INIT_RETRIES = 2
POLL_ERROR_ABORT_THRESHOLD = 8
ANALYTICS_ENDPOINT_READY_TIMEOUT_SECONDS = 300


class StuckInInitError(RuntimeError):
    def __init__(self, job_id: str, sub_phase: str, elapsed: float):
        super().__init__(
            f"Job {job_id} stuck in '{sub_phase}' for "
            f"{elapsed:.0f}s (>{STUCK_IN_INIT_TIMEOUT_SECONDS}s)."
        )
        self.job_id = job_id
        self.sub_phase = sub_phase
        self.elapsed = elapsed


SPARK_GAUGE_MAP: list[tuple[str, str]] = [
    ("spark_final_input_bytes", "input_bytes"),
    ("spark_final_output_bytes", "output_bytes"),
    ("spark_final_files_read_ratio", "files_read"),
    ("spark_final_records_read_ratio", "records_read"),
    ("spark_final_records_written_ratio", "records_written"),
    ("spark_final_shuffle_read_bytes", "shuffle_read_bytes"),
    ("spark_final_shuffle_write_bytes", "shuffle_write_bytes"),
    ("spark_final_shuffle_read_records_ratio", "shuffle_read_records"),
    ("spark_final_shuffle_write_records_ratio", "shuffle_write_records"),
    ("spark_final_total_disk_bytes_spilled", "total_disk_bytes_spilled"),
    ("spark_final_total_memory_bytes_spilled", "total_memory_bytes_spilled"),
    ("spark_final_task_cpu_efficiency_pct_ratio", "task_cpu_efficiency_pct"),
    ("spark_final_cluster_cpu_utilization_pct_ratio", "cluster_cpu_utilization_pct"),
    ("spark_final_executor_gc_pct_of_run_time_ratio", "executor_gc_pct_of_run_time"),
    ("spark_final_gc_time_ms_milliseconds", "gc_time_seconds"),
    ("spark_final_executor_cpu_time_ms_milliseconds", "executor_cpu_time_seconds"),
    ("spark_final_executor_run_time_ms_milliseconds", "executor_run_time_seconds"),
    ("spark_final_wall_clock_duration_ms_milliseconds", "wall_clock_duration_seconds"),
    (
        "spark_final_sql_execution_duration_ms_milliseconds",
        "sql_execution_duration_seconds",
    ),
    (
        "spark_final_stage_queue_wait_max_ms_milliseconds",
        "stage_queue_wait_max_seconds",
    ),
    (
        "spark_final_stage_queue_wait_mean_ms_milliseconds",
        "stage_queue_wait_mean_seconds",
    ),
    ("spark_final_total_tasks_ratio", "total_tasks"),
    ("spark_final_total_tasks_failed_ratio", "total_tasks_failed"),
    ("spark_final_peak_executor_memory_bytes", "peak_executor_memory_bytes"),
    ("spark_final_peak_unified_memory_bytes", "peak_unified_memory_bytes"),
    ("spark_final_peak_driver_memory_bytes", "peak_driver_memory_bytes"),
    ("spark_final_total_stages_ratio", "total_stages"),
    ("spark_final_total_stage_attempts_ratio", "total_stage_attempts"),
    ("spark_final_total_jobs_ratio", "total_jobs"),
    ("spark_final_executors_used_ratio", "executors_used"),
    # Run-level success/failure signals: status=0 OK, 1 FAILED.
    ("spark_final_status_ratio", "status"),
    ("spark_final_jobs_failed_ratio", "jobs_failed"),
    ("spark_final_sql_executions_failed_ratio", "sql_executions_failed"),
]

PEAK_GAUGE_QUERY_MAP: dict[str, str] = {
    "executor_cpu_max_rate_5m_cores": (
        "max(max_over_time((rate(spark_stage_executor_cpu_time_nanoseconds_total"
        + '{job=~".*{short_id}.*"}[5m]) / 1000000000)[5m:30s]))'
    ),
    "executor_cpu_sum_rate_5m_cores": (
        "max(max_over_time((sum(rate(spark_stage_executor_cpu_time_nanoseconds_total"
        + '{job=~".*{short_id}.*"}[5m])) / 1000000000)[5m:30s]))'
    ),
    "executor_gc_max_rate_5m_ms_per_sec": (
        "max(max_over_time(rate(spark_executor_gc_time_milliseconds_total"
        + '{job=~".*{short_id}.*"}[5m])[5m:30s]))'
    ),
    "driver_cpu_max_rate_5m_cores": (
        "max(max_over_time((rate(spark_driver_jvm_cpu_time_nanoseconds_total"
        + '{job=~".*{short_id}.*driver.*"}[5m]) / 1000000000)[5m:30s]))'
    ),
    "driver_gc_max_rate_5m_ms_per_sec": (
        "max(max_over_time(rate(spark_driver_executor_gc_time_milliseconds_total"
        + '{job=~".*{short_id}.*driver.*"}[5m])[5m:30s]))'
    ),
}

GAUGE_TO_SUMMARY_KEY: dict[str, str] = {
    "input_bytes": "total_input_bytes",
    "output_bytes": "total_output_bytes",
    "files_read": "total_files_read",
    "records_read": "total_records_read",
    "records_written": "total_records_written",
    "shuffle_read_bytes": "total_shuffle_read_bytes",
    "shuffle_write_bytes": "total_shuffle_write_bytes",
    "shuffle_read_records": "total_shuffle_read_records",
    "shuffle_write_records": "total_shuffle_write_records",
    "total_disk_bytes_spilled": "total_disk_bytes_spilled",
    "total_memory_bytes_spilled": "total_memory_bytes_spilled",
    "gc_time_seconds": "total_gc_time_seconds",
    "executor_cpu_time_seconds": "total_executor_cpu_time_seconds",
    "executor_run_time_seconds": "total_executor_run_time_seconds",
    "wall_clock_duration_seconds": "wall_clock_duration_seconds",
    "sql_execution_duration_seconds": "sql_execution_duration_seconds",
    "stage_queue_wait_max_seconds": "stage_queue_wait_max_seconds",
    "stage_queue_wait_mean_seconds": "stage_queue_wait_mean_seconds",
    "total_tasks": "total_tasks",
    "total_tasks_failed": "total_tasks_failed",
    "peak_executor_memory_bytes": "peak_executor_memory_bytes",
    "peak_unified_memory_bytes": "peak_unified_memory_bytes",
    "peak_driver_memory_bytes": "peak_driver_memory_bytes",
    "total_stages": "total_stages",
    "total_stage_attempts": "total_stage_attempts",
    "total_jobs": "total_jobs",
    "status": "status",
    "jobs_failed": "jobs_failed",
    "sql_executions_failed": "sql_executions_failed",
    "task_cpu_efficiency_pct": "task_cpu_efficiency_pct",
    "cluster_cpu_utilization_pct": "cluster_cpu_utilization_pct",
    "executor_gc_pct_of_run_time": "executor_gc_pct_of_run_time",
    "executors_used": "executors_used",
    "executor_cores": "executor_cores",
    "driver_cores": "driver_cores",
    "max_executors": "max_executors",
    "executor_cpu_max_pct_of_executor_5m": "executor_cpu_max_pct_of_executor_5m",
    "executor_cpu_max_pct_of_cluster_5m": "executor_cpu_max_pct_of_cluster_5m",
    "driver_cpu_max_pct_of_driver_5m": "driver_cpu_max_pct_of_driver_5m",
    "executor_gc_max_pct_of_wallclock_5m": "executor_gc_max_pct_of_wallclock_5m",
    "driver_gc_max_pct_of_wallclock_5m": "driver_gc_max_pct_of_wallclock_5m",
    # Derived from existing gauges (computed in collect_spark_metrics).
    "stage_retry_count": "stage_retry_count",
    "stage_retry_pct": "stage_retry_pct",
}


# Gauges whose absence on a COMPLETED job signals a genuinely incomplete
# capture (vs. short-query gauges that are legitimately empty).
CRITICAL_GAUGES: frozenset[str] = frozenset(
    {
        "wall_clock_duration_seconds",
        "sql_execution_duration_seconds",
        "executor_cpu_time_seconds",
        "executor_run_time_seconds",
        "executors_used",
        "total_tasks",
        "total_stages",
        "total_jobs",
        "status",
        "task_cpu_efficiency_pct",
        "cluster_cpu_utilization_pct",
    }
)


# Count of gauges derived inside collect_spark_metrics (not queried from
# Prometheus directly): 2 gc_pct_of_wallclock + 3 cores/max_executors
# + 2 executor cpu_pct + 1 driver cpu_pct + 2 stage retry.
_DERIVED_GAUGE_COUNT: int = 10

# Upper bound on len(gauges) a healthy run can produce. Used as the
# denominator in `metrics ... -> N/M gauges` log lines so the numerator
# can never exceed it.
EXPECTED_GAUGE_COUNT: int = (
    len(SPARK_GAUGE_MAP) + len(PEAK_GAUGE_QUERY_MAP) + _DERIVED_GAUGE_COUNT
)


SUMMARY_FIELD_DEFINITIONS: dict[str, str] = {
    "executor_cpu_max_pct_of_executor_5m": (
        "Busiest executor CPU rate over a 5-minute window, normalized by "
        "single-executor CPU capacity (executor_cores). May exceed 100 when an "
        "executor bursts past its requested cores (a request, not a hard limit)."
    ),
    "executor_cpu_max_pct_of_cluster_5m": (
        "Sum of executor CPU rate over a 5-minute window, normalized by "
        "provisioned pool capacity (max_executors * executor_cores). May exceed "
        "100 when executors burst past their requested cores."
    ),
    "driver_cpu_max_pct_of_driver_5m": (
        "Driver CPU rate over a 5-minute window, normalized by the driver's own "
        "requested CPU capacity (driver_cores). May exceed 100 when the driver "
        "bursts past its request (driver_cores is a request, not a hard limit)."
    ),
    "executor_gc_pct_of_run_time": (
        "Cumulative executor GC time / total executor run time, as a percent. "
        "Executor-only (driver excluded). Steady-state GC-pressure health gauge; "
        "distinct from the windowed executor_gc_max_pct_of_wallclock_5m peak."
    ),
    "executor_gc_max_pct_of_wallclock_5m": (
        "Executor GC ms/sec over a 5-minute window converted to wall-clock percent. "
        "May exceed 100 under parallel/concurrent GC (multiple GC threads)."
    ),
    "driver_gc_max_pct_of_wallclock_5m": (
        "Driver GC ms/sec over a 5-minute window converted to wall-clock percent. "
        "May exceed 100 under parallel/concurrent GC (multiple GC threads)."
    ),
}


def summary_field_definitions() -> dict[str, str]:
    return dict(SUMMARY_FIELD_DEFINITIONS)


def missing_critical_gauges(gauges: dict[str, object]) -> list[str]:
    return sorted(n for n in CRITICAL_GAUGES if n not in gauges)


def collect_spark_metrics(
    job_id: str,
    proxy_port: int,
    proxy_host: str = "127.0.0.1",
    *,
    warmup_failure_logger: Callable[[str], None] | None = None,
    kube_config: str = "",
) -> tuple[dict[str, Any], bool]:
    if not proxy_port:
        return {}, True

    base = (
        f"http://{proxy_host}:{proxy_port}"
        f"/api/v1/namespaces/{PROMETHEUS_NAMESPACE}"
        f"/services/http:{PROMETHEUS_SERVICE}:80/proxy"
    )
    short_id = job_id.replace("cl-spark-", "")

    try:
        with urllib.request.urlopen(
            f"{base}/api/v1/query?query=up", timeout=10
        ) as resp:
            resp.read(64)
    except (OSError, ValueError) as e:
        if warmup_failure_logger is not None:
            warmup_failure_logger(f"Prometheus warmup failed: {e}")
        return {}, True

    def _query_one(promql: str) -> tuple[bool, float | None]:
        q = promql
        url = f"{base}/api/v1/query?query={urllib.parse.quote(q)}"
        try:
            with urllib.request.urlopen(url, timeout=10) as resp:
                data = json.loads(resp.read().decode())
        except (OSError, ValueError):
            return False, None
        results = data.get("data", {}).get("result", [])
        if not results:
            return True, None
        raw = results[0].get("value", [None, None])
        return True, (float(raw[1]) if raw[1] is not None else None)

    gauges: dict[str, Any] = {}
    for prom_name, canonical in SPARK_GAUGE_MAP:
        ok, val = _query_one(f'{prom_name}{{job_id="{short_id}"}}')
        if not ok:
            time.sleep(0.5)
            ok, val = _query_one(f'{prom_name}{{job_id="{short_id}"}}')
            if not ok:
                return {}, True
        if val is not None:
            if prom_name.endswith("_ms_milliseconds"):
                val = round(val / 1000.0, 3)
            gauges[canonical] = val

    for canonical, promql_tmpl in PEAK_GAUGE_QUERY_MAP.items():
        # str.replace (not str.format): templates contain literal PromQL
        # `{job=~"..."}` braces that str.format would reject.
        promql = promql_tmpl.replace("{short_id}", short_id)
        ok, val = _query_one(promql)
        if not ok:
            time.sleep(0.5)
            ok, val = _query_one(promql)
            if not ok:
                return {}, True
        if val is not None:
            gauges[canonical] = val

    gc_rate = gauges.get("executor_gc_max_rate_5m_ms_per_sec")
    if gc_rate is not None:
        # Unclamped: parallel/concurrent GC (multiple GC threads) can exceed
        # 1000 ms/sec; >100 flags GC thrash.
        gauges["executor_gc_max_pct_of_wallclock_5m"] = round(
            max(0.0, float(gc_rate) / 1000.0 * 100.0), 1
        )
    driver_gc_rate = gauges.get("driver_gc_max_rate_5m_ms_per_sec")
    if driver_gc_rate is not None:
        gauges["driver_gc_max_pct_of_wallclock_5m"] = round(
            max(0.0, float(driver_gc_rate) / 1000.0 * 100.0), 1
        )

    cores = get_sparkapp_cores(job_id, kube_config=kube_config)
    if cores:
        gauges["executor_cores"] = cores["executor_cores"]
        gauges["driver_cores"] = cores["driver_cores"]
        # DRA cap, or static `instances` when DRA is off.
        max_executors = cores.get("max_executors") or cores.get("instances")
        if max_executors:
            gauges["max_executors"] = max_executors
        exec_cores = float(cores["executor_cores"])
        # Provisioned ceiling: fixed by config, independent of executor churn,
        # so dead executors can't shrink the denominator and inflate the pct.
        provisioned_capacity = float(max_executors or 0) * exec_cores
        exec_peak = gauges.get("executor_cpu_max_rate_5m_cores")
        if exec_peak is not None and exec_cores > 0:
            # Unclamped: executor_cores is a CPU request, not a limit, so an
            # executor can burst above it; >100 flags over-subscription.
            gauges["executor_cpu_max_pct_of_executor_5m"] = round(
                max(0.0, float(exec_peak) / exec_cores * 100.0), 1
            )
        exec_sum = gauges.get("executor_cpu_sum_rate_5m_cores")
        if exec_sum is not None and provisioned_capacity > 0:
            gauges["executor_cpu_max_pct_of_cluster_5m"] = round(
                max(0.0, float(exec_sum) / provisioned_capacity * 100.0), 1
            )
        driver_peak_cores = gauges.get("driver_cpu_max_rate_5m_cores")
        driver_cores = float(cores["driver_cores"])
        if driver_peak_cores is not None and driver_cores > 0:
            # Unclamped: driver_cores is a CPU request, not a limit, so the
            # driver can burst above it; >100 flags an under-provisioned driver.
            gauges["driver_cpu_max_pct_of_driver_5m"] = round(
                max(0.0, float(driver_peak_cores) / driver_cores * 100.0), 1
            )

    # Derived: stage retries (a high retry rate indicates lost executors /
    # shuffle fetch failures even when the job ultimately COMPLETED).
    total_stages = gauges.get("total_stages")
    total_attempts = gauges.get("total_stage_attempts")
    if total_stages is not None and total_attempts is not None:
        retries = max(0.0, float(total_attempts) - float(total_stages))
        gauges["stage_retry_count"] = retries
        if float(total_stages) > 0:
            gauges["stage_retry_pct"] = round(retries / float(total_stages) * 100.0, 1)

    return gauges, False


# Cache populated by prime_sparkapp_cores; the operator deletes the CR
# shortly after termination so we read it while it's guaranteed to exist.
_SPARKAPP_CORES_CACHE: dict[str, dict[str, float]] = {}
_SPARKAPP_CORES_MISSING_LOGGED: set[str] = set()
_SPARKAPP_CORES_LAST_STDERR: dict[str, str] = {}


def _fetch_sparkapp_cores_from_kubectl(
    sparkapp_name: str,
    namespace: str,
    kube_config: str = "",
) -> dict[str, float] | None:
    # Pipe-delimited so missing optional fields keep positional alignment.
    jsonpath = (
        "jsonpath={.spec.driver.cores}|{.spec.executor.cores}"
        "|{.spec.dynamicAllocation.minExecutors}"
        "|{.spec.dynamicAllocation.maxExecutors}"
        "|{.spec.dynamicAllocation.initialExecutors}"
        "|{.spec.executor.instances}"
    )
    cmd = [
        "kubectl",
        "get",
        "sparkapplication",
        sparkapp_name,
        "-n",
        namespace,
        "-o",
        jsonpath,
    ]
    if kube_config:
        cmd.extend(["--kubeconfig", kube_config])
    try:
        result = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            timeout=10,
        )
    except (subprocess.TimeoutExpired, FileNotFoundError) as e:
        _SPARKAPP_CORES_LAST_STDERR[sparkapp_name] = f"{type(e).__name__}: {e}"
        return None
    if result.returncode != 0:
        _SPARKAPP_CORES_LAST_STDERR[sparkapp_name] = (
            result.stderr or ""
        ).strip() or f"rc={result.returncode}"
        return None
    parts = result.stdout.strip().split("|")
    if len(parts) < 2:
        return None
    try:
        out: dict[str, float] = {
            "driver_cores": float(parts[0]),
            "executor_cores": float(parts[1]),
        }
    except ValueError:
        return None
    for key, idx in (
        ("min_executors", 2),
        ("max_executors", 3),
        ("initial_executors", 4),
        ("instances", 5),
    ):
        if idx >= len(parts):
            continue
        raw = parts[idx].strip()
        if not raw:
            continue
        try:
            out[key] = float(raw)
        except ValueError:
            continue
    return out


def get_sparkapp_cores(
    sparkapp_name: str,
    namespace: str = ANALYTICS_NAMESPACE,
    kube_config: str = "",
) -> dict[str, float] | None:
    cached = _SPARKAPP_CORES_CACHE.get(sparkapp_name)
    if cached is not None:
        return cached
    cores = _fetch_sparkapp_cores_from_kubectl(sparkapp_name, namespace, kube_config)
    if cores is not None:
        _SPARKAPP_CORES_CACHE[sparkapp_name] = cores
        return cores
    if sparkapp_name not in _SPARKAPP_CORES_MISSING_LOGGED:
        _SPARKAPP_CORES_MISSING_LOGGED.add(sparkapp_name)
        stderr = _SPARKAPP_CORES_LAST_STDERR.get(sparkapp_name, "")
        reason = f" (kubectl: {stderr[:200]})" if stderr else ""
        print(
            f"{get_timestamp()} [warn] SparkApplication {sparkapp_name} "
            f"unavailable; cores/max_executors will be missing from "
            f"metrics.{reason}"
        )
    return None


def prime_sparkapp_cores(
    sparkapp_name: str,
    namespace: str = ANALYTICS_NAMESPACE,
    *,
    retries: int = 120,
    backoff_seconds: float = 1.5,
    kube_config: str = "",
) -> dict[str, float] | None:
    # Best-effort: ~180s budget; the operator may take >60s to make the CR
    # visible under N-way parallel submits when the K8s API is hot. Silent
    # on failure (get_sparkapp_cores logs the user-visible warning).
    for attempt in range(1, max(1, retries) + 1):
        cores = _fetch_sparkapp_cores_from_kubectl(
            sparkapp_name, namespace, kube_config
        )
        if cores is not None:
            _SPARKAPP_CORES_CACHE[sparkapp_name] = cores
            return cores
        if attempt < retries:
            time.sleep(backoff_seconds)
    return None


def diagnose_submitted_phase(
    job_id: str,
    kube_config: str,
    namespace: str = ANALYTICS_NAMESPACE,
) -> tuple[str, str]:
    driver_pod = f"{job_id}-driver"
    try:
        cmd = [
            "kubectl",
            "get",
            "pod",
            driver_pod,
            "-n",
            namespace,
            "-o",
            "json",
        ]
        if kube_config:
            cmd.extend(["--kubeconfig", kube_config])
        result = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            timeout=10,
        )
        if result.returncode != 0:
            return " [pod not found yet]", ""

        pod = json.loads(result.stdout)
        phase = pod.get("status", {}).get("phase", "Unknown")

        conditions = pod.get("status", {}).get("conditions", [])
        for cond in conditions:
            if cond.get("type") == "PodScheduled" and cond.get("status") == "False":
                reason = cond.get("reason", "Unknown")
                msg = cond.get("message", "")
                return (
                    f" [scheduling: {reason} - {msg[:80]}]",
                    f"scheduling:{reason}",
                )

        init_statuses = pod.get("status", {}).get("initContainerStatuses", [])
        if init_statuses:
            for ics in init_statuses:
                name = ics.get("name", "?")
                state_dict = ics.get("state", {})
                if "waiting" in state_dict:
                    reason = state_dict["waiting"].get("reason", "?")
                    return (
                        f" [init:{name} waiting: {reason}]",
                        f"init:{name}:waiting:{reason}",
                    )
                if "running" in state_dict:
                    return (
                        f" [init:{name} running]",
                        f"init:{name}:running",
                    )

        container_statuses = pod.get("status", {}).get("containerStatuses", [])
        for cs in container_statuses:
            name = cs.get("name", "?")
            state_dict = cs.get("state", {})
            if "waiting" in state_dict:
                reason = state_dict["waiting"].get("reason", "?")
                return (
                    f" [{name} waiting: {reason}]",
                    f"container:{name}:waiting:{reason}",
                )

        return f" [pod phase: {phase}]", f"phase:{phase}"
    except Exception:
        return "", ""


def wait_for_analytics_endpoint(
    proxy_host: str,
    proxy_port: int,
    timeout_seconds: int = ANALYTICS_ENDPOINT_READY_TIMEOUT_SECONDS,
    poll_interval_seconds: int = 3,
) -> None:
    ready_url = (
        f"http://{proxy_host}:{proxy_port}/api/v1/namespaces/"
        f"cleanroom-spark-analytics-agent/services/"
        f"https:cleanroom-spark-analytics-agent:443/proxy/ready"
    )
    deadline = time.time() + timeout_seconds
    last_error: str = "no response received"
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(ready_url, timeout=5) as resp:
                if resp.status == 200:
                    return
                last_error = f"HTTP {resp.status}"
        except Exception as e:
            last_error = f"{type(e).__name__}: {e}"
        time.sleep(poll_interval_seconds)
    raise TimeoutError(
        f"Hit timeout waiting for analytics endpoint to be ready "
        f"after {timeout_seconds}s. Last error: {last_error} at {ready_url}"
    )


_STRESS_DIR = Path(__file__).resolve().parent.parent
_WORKLOADS_GENERATED = (
    _STRESS_DIR / ".." / ".." / ".." / "workloads" / "generated"
).resolve()


def default_submit_config_path() -> str:
    candidates = [
        _WORKLOADS_GENERATED / "tpcds-analytics" / "submitSqlJobConfig.json",
        _STRESS_DIR
        / ".."
        / ".."
        / ".."
        / "workload-samples-aks"
        / "generated"
        / "tpcds-analytics"
        / "submitSqlJobConfig.json",
        _STRESS_DIR / "generated" / "submitSqlJobConfig.json",
        _STRESS_DIR / "runner" / "generated" / "submitSqlJobConfig.json",
        _STRESS_DIR / "runner" / "generated" / "run" / "submitSqlJobConfig.json",
    ]
    existing = [p for p in candidates if p.exists()]
    if not existing:
        return ""
    newest = max(existing, key=lambda p: p.stat().st_mtime)
    return str(newest)


def default_kube_config_path() -> str:
    candidate = _WORKLOADS_GENERATED / "cl-cluster" / "k8s-credentials.yaml"
    return str(candidate) if candidate.exists() else ""


def metrics_output_dir(config: dict[str, Any]) -> Path:
    sf = config.get("scaleFactor", "")
    sf_suffix = f"sf{sf}" if sf else "sfunknown"
    override = config.get("_metrics_dir", "")
    base = Path(override) if override else _STRESS_DIR / "generated"
    return base / "spark-metrics" / sf_suffix


def prepare_results_file(
    results_file: Path,
    *,
    spark_metrics_dir: Path,
) -> Path:
    from spark_cleanup import sweep_stale_spark_snapshots

    sweep_stale_spark_snapshots(spark_metrics_dir)
    return results_file


def write_metrics_atomically(target: Path, payload: dict[str, Any]) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    tmp = target.with_suffix(target.suffix + ".tmp")
    with tmp.open("w", encoding="utf-8") as f:
        json.dump(payload, f, indent=2)
    tmp.replace(target)


def load_spark_metrics(
    metrics_dir: Path,
    job_id: str,
    default_state: str = "",
) -> tuple[Path, dict[str, Any]]:
    metrics_dir.mkdir(parents=True, exist_ok=True)
    metrics_path = metrics_dir / f"spark_metrics_{job_id}.json"
    doc: dict[str, Any] = {}
    if metrics_path.exists():
        try:
            with metrics_path.open("r", encoding="utf-8") as f:
                doc = json.load(f) or {}
        except (OSError, json.JSONDecodeError):
            doc = {}
    if not doc:
        doc = {
            "job_id": job_id,
            "collected_at": time.strftime("%Y-%m-%dT%H:%M:%S"),
        }
        if default_state:
            doc["collection_state"] = default_state
    return metrics_path, doc


def save_spark_metrics(
    metrics_path: Path,
    doc: dict[str, Any],
) -> None:
    doc = reorder_metrics_top_keys(doc)
    with metrics_path.open("w", encoding="utf-8") as f:
        json.dump(doc, f, indent=2)


def merge_prep_into_metrics_file(
    metrics_dir: Path,
    job_id: str,
    prep: dict[str, Any],
) -> bool:
    try:
        metrics_path, doc = load_spark_metrics(
            metrics_dir, job_id, default_state="prep_phase_only"
        )
    except Exception as e:
        print(f"{get_timestamp()} [warn] spark_metrics dir failed for {job_id}: {e}")
        return False
    for k in ("phase_timings", "duration_seconds"):
        doc.pop(k, None)
    doc["prep_phase"] = prep
    try:
        save_spark_metrics(metrics_path, doc)
    except OSError as e:
        print(f"{get_timestamp()} [warn] spark_metrics write failed for {job_id}: {e}")
        return False
    return True


def parse_iso_ts(ts: str | None) -> float | None:
    if not ts:
        return None
    try:
        normalized = ts.rstrip("Z") + "+00:00" if ts.endswith("Z") else ts
        return datetime.fromisoformat(normalized).timestamp()
    except (ValueError, TypeError):
        return None


def fetch_driver_pod_snapshot(
    job_id: str,
    kube_config: str,
    namespace: str = ANALYTICS_NAMESPACE,
) -> dict[str, Any] | None:
    if not kube_config:
        return None
    driver_pod = f"{job_id}-driver"
    try:
        cmd = [
            "kubectl",
            "get",
            "pod",
            driver_pod,
            "-n",
            namespace,
            "-o",
            "json",
            "--kubeconfig",
            kube_config,
        ]
        result = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            timeout=10,
        )
        if result.returncode != 0:
            return None
        return json.loads(result.stdout)
    except (subprocess.TimeoutExpired, json.JSONDecodeError, OSError):
        return None


def _fetch_driver_warning_events(
    job_id: str,
    kube_config: str,
    namespace: str,
) -> list[dict[str, Any]]:
    if not kube_config:
        return []
    driver_pod = f"{job_id}-driver"
    try:
        result = subprocess.run(
            [
                "kubectl",
                "get",
                "events",
                "-n",
                namespace,
                "--field-selector",
                f"involvedObject.name={driver_pod},type=Warning",
                "-o",
                "json",
                "--kubeconfig",
                kube_config,
            ],
            capture_output=True,
            text=True,
            timeout=10,
        )
        if result.returncode != 0:
            return []
        items = json.loads(result.stdout).get("items", [])
    except (subprocess.TimeoutExpired, json.JSONDecodeError, OSError):
        return []
    return [
        {
            k: v
            for k, v in {
                "reason": it.get("reason"),
                "message": it.get("message"),
                "count": it.get("count"),
                "last_seen": it.get("lastTimestamp"),
            }.items()
            if v is not None
        }
        for it in items
    ]


_LOG_ERROR_PATTERN = re.compile(
    r"Traceback|ERROR|Exception|Failure|LOG_ERR|AADSTS|Blobfuse exited",
    re.IGNORECASE,
)


def _fetch_container_log_errors(
    pod: str,
    container: str,
    kube_config: str,
    namespace: str,
    tail: int = 200,
) -> list[str]:
    if not kube_config:
        return []
    try:
        result = subprocess.run(
            [
                "kubectl",
                "logs",
                pod,
                "-c",
                container,
                "--tail",
                str(tail),
                "-n",
                namespace,
                "--kubeconfig",
                kube_config,
            ],
            capture_output=True,
            text=True,
            timeout=15,
        )
    except (subprocess.TimeoutExpired, OSError):
        return []
    if result.returncode != 0:
        return []
    errors: list[str] = []
    for line in result.stdout.splitlines():
        line = line.strip()
        if (
            line
            and _LOG_ERROR_PATTERN.search(line)
            and (not errors or errors[-1] != line)
        ):
            errors.append(line)
    return errors[-40:]


def get_driver_failure_detail(
    job_id: str,
    kube_config: str,
    namespace: str = ANALYTICS_NAMESPACE,
) -> dict[str, Any] | None:
    # The Spark CR only records ExitCode/Reason=Error; the driver container's
    # terminated.reason distinguishes OOMKilled from generic failures, and
    # Warning events surface node MemoryPressure / evictions / OOMKilling.
    detail: dict[str, Any] = {}
    driver_pod = f"{job_id}-driver"
    pod = fetch_driver_pod_snapshot(job_id, kube_config, namespace)
    if pod is not None:
        for cs in pod.get("status", {}).get("containerStatuses", []) or []:
            if cs.get("name") not in ("spark-kubernetes-driver", "spark-driver"):
                continue
            term = (
                (cs.get("state") or {}).get("terminated")
                or (cs.get("lastState") or {}).get("terminated")
                or {}
            )
            if term:
                detail["driver_container"] = {
                    k: v
                    for k, v in {
                        "reason": term.get("reason"),
                        "exit_code": term.get("exitCode"),
                        "signal": term.get("signal"),
                        "message": term.get("message"),
                        "finished_at": term.get("finishedAt"),
                    }.items()
                    if v is not None
                }
            break
    warnings = _fetch_driver_warning_events(job_id, kube_config, namespace)
    if warnings:
        detail["warning_events"] = warnings
    # Driver stdout carries the application traceback (e.g. the
    # MountPointUnavailableFailure that the exit_code=1 hides).
    driver_errs = _fetch_container_log_errors(
        driver_pod, "spark-kubernetes-driver", kube_config, namespace
    )
    if driver_errs:
        detail["driver_log_tail"] = driver_errs
    # blobfuse init-container logs hold the real mount/auth cause; their exit
    # status is a misleading 0 (the launcher catches the error and unmounts),
    # so only the logs reveal it. The root cause is shared across mounts, so
    # stop at the first failing container to bound kubectl calls.
    if pod is not None:
        for c in pod.get("spec", {}).get("initContainers", []) or []:
            if "blobfuse" not in c.get("name", ""):
                continue
            errs = _fetch_container_log_errors(
                driver_pod, c["name"], kube_config, namespace
            )
            if errs:
                detail["mount_errors"] = errs
                break
    return detail or None


def format_audit_events(events: list) -> str:
    if not events:
        return "No audit events"
    sorted_events = sorted(events, key=lambda e: e.get("timestamp") or "")
    formatted = ["\n" + "=" * 80]
    for i, event in enumerate(sorted_events, 1):
        timestamp_iso = event.get("timestampIso") or "N/A"
        message = event.get("data", {}).get("message", "N/A")
        source = event.get("data", {}).get("source", "N/A")
        formatted.append(f"Audit Event #{i}:")
        formatted.append(f"  Timestamp: {timestamp_iso}")
        formatted.append(f"  Source:    {source}")
        formatted.append(f"  Message:   {message}")
        formatted.append("-" * 80)
    return "\n".join(formatted)


def audit_events_to_list(events: list) -> list[dict]:
    """Return audit events as a JSON-viewer-friendly list of objects."""
    if not events:
        return []
    sorted_events = sorted(events, key=lambda e: e.get("timestamp") or "")
    return [
        {
            "timestamp": e.get("timestampIso") or "N/A",
            "source": e.get("data", {}).get("source", "N/A"),
            "message": e.get("data", {}).get("message", "N/A"),
        }
        for e in sorted_events
    ]


def get_audit_events(
    contract_id: str,
    job_id: str,
    cgs_client: str,
    wait_for_names: set[str] | None = None,
    max_retries: int = 3,
    retry_delay_seconds: float = 5.0,
) -> list:
    # CCF audit writes are committed asynchronously, so a query right after
    # the driver pod completes can race the terminal events. If callers
    # know which event names must be present (e.g. DATASET_LOAD_COMPLETED
    # for a COMPLETED job), retry briefly until they show up or budget
    # is exhausted.
    job_id_stripped = job_id.replace("cl-spark-", "")
    job_filter = f"job id: {job_id_stripped}"

    def _fetch() -> list:
        result = subprocess.run(
            [
                "az",
                "cleanroom",
                "governance",
                "contract",
                "event",
                "list",
                "--contract-id",
                contract_id,
                "--all",
                "--governance-client",
                cgs_client,
            ],
            capture_output=True,
            text=True,
            check=True,
        )
        events = json.loads(result.stdout)
        return [
            x
            for x in events.get("value", [])
            if re.search(job_filter, x.get("data", {}).get("message", ""))
        ]

    matched = _fetch()
    if not wait_for_names:
        return matched
    for attempt in range(max_retries):
        present = {extract_event_name(ev) for ev in matched}
        if wait_for_names.issubset(present):
            return matched
        time.sleep(retry_delay_seconds)
        matched = _fetch()
    return matched


# Audit messages are emitted by AuditRecord.get_message() (see
# src/workloads/analytics/contracts/.../audit/audit_record.py) as
# `Event_<NAME>_<id> | <message>`. The schema lives in
# src/workloads/analytics/contracts/.../audit/audit_records.json -- relevant
# names: DATASET_LOAD_COMPLETED, DATASET_WRITE_COMPLETED, QUERY_COMPLETED.
_EVENT_NAME_RE = re.compile(r"Event_([A-Z][A-Z0-9_]*?)_\d+\s*\|")


def extract_event_name(ev: dict[str, Any]) -> str:
    msg = ev.get("data", {}).get("message", "") or ""
    m = _EVENT_NAME_RE.match(msg)
    return m.group(1) if m else ""


# Audit messages carry the dataset name as `Dataset: <name> load completed`
# (load) or `| dataset: <name> |` (write); both reduce to `dataset:\s+<name>`.
_DATASET_RE = re.compile(r"dataset:\s+(\S+)", re.I)


def extract_dataset_name(ev: dict[str, Any]) -> str:
    msg = ev.get("data", {}).get("message", "") or ""
    m = _DATASET_RE.search(msg)
    return m.group(1) if m else ""


def collect_prep_phase_breakdown(
    job_id: str,
    submit_time: float,
    running_since: float | None,
    end_time: float,
    audit_events: list[dict[str, Any]],
    kube_config: str,
) -> dict[str, Any]:
    def _utc(t: float | None) -> str | None:
        if t is None or t <= 0:
            return None
        return (
            datetime.fromtimestamp(t, tz=timezone.utc)
            .isoformat(timespec="milliseconds")
            .replace("+00:00", "Z")
        )

    out: dict[str, Any] = {
        "submit_time": _utc(submit_time),
        "running_since": _utc(running_since),
        "end_time": _utc(end_time),
        "init_containers": [],
    }

    pod = fetch_driver_pod_snapshot(job_id, kube_config)
    scheduled: float | None = None
    initialized: float | None = None
    if pod is not None:
        status = pod.get("status", {})
        conds_by_type = {
            c.get("type"): parse_iso_ts(c.get("lastTransitionTime"))
            for c in status.get("conditions", [])
        }
        scheduled = conds_by_type.get("PodScheduled")
        initialized = conds_by_type.get("Initialized")

        def _delta(a: float | None, b: float | None) -> float | None:
            if a is None or b is None:
                return None
            return max(0.0, round(b - a, 3))

        # K8s 1.29+ native sidecars (otel-collector, skr, ...) are declared as
        # init containers but keep running until the pod exits, so their
        # terminated.finishedAt reflects pod end -- making duration_seconds span
        # the whole job. effective_init_seconds clamps finished to the
        # Initialized boundary to report actual init-phase contribution.
        def _effective(a: float | None, b: float | None) -> float | None:
            if a is None or b is None:
                return None
            end = min(b, initialized) if initialized is not None else b
            return max(0.0, round(end - a, 3))

        for ics in status.get("initContainerStatuses", []) or []:
            name = ics.get("name", "?")
            term = (ics.get("state") or {}).get("terminated") or {}
            started = parse_iso_ts(term.get("startedAt"))
            finished = parse_iso_ts(term.get("finishedAt"))
            out["init_containers"].append(
                {
                    "name": name,
                    "started_at": term.get("startedAt"),
                    "finished_at": term.get("finishedAt"),
                    "duration_seconds": _delta(started, finished),
                    "effective_init_seconds": _effective(started, finished),
                    "exit_code": term.get("exitCode"),
                }
            )

    by_name: dict[str, list[tuple[float, dict[str, Any]]]] = {}
    for ev in audit_events or []:
        name = extract_event_name(ev)
        ts = parse_iso_ts(ev.get("timestampIso"))
        if not name or ts is None:
            continue
        by_name.setdefault(name, []).append((ts, ev))
    for evs in by_name.values():
        evs.sort(key=lambda x: x[0])

    load_completes = by_name.get("DATASET_LOAD_COMPLETED", [])
    write_completes = by_name.get("DATASET_WRITE_COMPLETED", [])
    query_completes = by_name.get("QUERY_COMPLETED", [])

    in_pod_start = running_since
    if pod is not None:
        cstat = pod.get("status", {}).get("containerStatuses") or []
        for cs in cstat:
            if cs.get("name") in ("spark-kubernetes-driver", "spark-driver"):
                run_state = (cs.get("state") or {}).get("running") or {}
                term_state = (cs.get("lastState") or {}).get("terminated") or {}
                started = parse_iso_ts(run_state.get("startedAt")) or parse_iso_ts(
                    term_state.get("startedAt")
                )
                if started is not None:
                    in_pod_start = started
                break

    # `data_load` covers driver_running -> last load complete, so all
    # concurrent dataset loads (asyncio.gather in run_query.py) are
    # accounted for rather than only the first to finish.
    first_load_end = load_completes[0][0] if load_completes else None
    last_load_end = load_completes[-1][0] if load_completes else None
    first_write_end = write_completes[0][0] if write_completes else None
    query_end = query_completes[-1][0] if query_completes else end_time

    # Only COMPLETED audit events expose dataset timing (STARTED is non-audit).
    per_dataset = [
        {
            "dataset": extract_dataset_name(ev),
            "completed_at": ev.get("timestampIso"),
        }
        for _ts, ev in load_completes
    ]
    if per_dataset:
        out["dataset_loads"] = per_dataset

    # `containers_ready` omitted: K8s 1.29+ native sidecars stay not-ready
    # until they exit, which lands after first_load_complete -- inspect
    # `init_containers` directly for slow-sidecar signal.
    timeline: dict[str, float] = {}

    def _off(t: float | None) -> float | None:
        if t is None:
            return None
        return round(max(0.0, t - submit_time), 3)

    for key, val in [
        ("pod_scheduled", _off(scheduled)),
        ("pod_initialized", _off(initialized)),
        ("driver_running", _off(in_pod_start)),
        ("first_load_complete", _off(first_load_end)),
        ("last_load_complete", _off(last_load_end)),
        ("write_complete", _off(first_write_end)),
        ("query_end", _off(query_end)),
        ("process_exit", _off(end_time)),
    ]:
        if val is not None:
            timeline[key] = val
    out["timeline_offsets_sec"] = timeline

    # Phase semantics:
    #   dataset_loading  -- driver_running -> LAST DATASET_LOAD_COMPLETED.
    #                       Covers all concurrent loads (asyncio.gather), not
    #                       just the first to finish.
    #   query_exec_write -- last load complete -> DATASET_WRITE_COMPLETED.
    #                       Covers Catalyst planning + Spark execution +
    #                       result write. QUERY_COMPLETED audit lands
    #                       within this window so it is not split out.
    phase_pairs: tuple[tuple[str, str | None, str], ...] = (
        ("scheduling", None, "pod_scheduled"),
        ("init_containers", "pod_scheduled", "pod_initialized"),
        ("driver_startup", "pod_initialized", "driver_running"),
        ("dataset_loading", "driver_running", "last_load_complete"),
        ("query_exec_write", "last_load_complete", "write_complete"),
    )
    durations: dict[str, float] = {}
    for label, earlier, later in phase_pairs:
        end_v = timeline.get(later)
        start_v = 0.0 if earlier is None else timeline.get(earlier)
        if end_v is None or start_v is None:
            continue
        durations[label] = round(max(0.0, end_v - start_v), 3)
    if durations:
        out["phase_durations_sec"] = durations
    return out


def print_prep_phase_breakdown(
    prep: dict[str, Any],
    label: str = "",
) -> None:
    if not prep:
        return
    hdr = f" {label}" if label else ""
    durations = prep.get("phase_durations_sec") or {}
    queue = (prep.get("timeline_offsets_sec") or {}).get("driver_running")

    slowest_tag = ""
    inits = prep.get("init_containers") or []
    if inits:
        # effective_init_seconds is clamped to the Initialized boundary at
        # collection time, so native sidecars (otel-collector, ...) report their
        # actual init-phase contribution rather than their full pod lifetime.
        def _eff(ic: dict[str, Any]) -> float:
            v = ic.get("effective_init_seconds")
            if v is None:
                v = ic.get("duration_seconds")
            return float(v or 0)

        slowest_ic = max(inits, key=_eff)
        dur = _eff(slowest_ic)
        starts: list[float] = []
        finishes_eff: list[float] = []
        for ic in inits:
            s = parse_iso_ts(ic.get("started_at"))
            if s is None:
                continue
            starts.append(s)
            finishes_eff.append(s + _eff(ic))
        parallel = ""
        if starts and finishes_eff:
            wall_s = max(finishes_eff) - min(starts)
            total_s = sum(_eff(ic) for ic in inits)
            if wall_s > 0 and total_s > wall_s * 1.5:
                parallel = ",parallel"
        slowest_tag = f"(slowest={slowest_ic.get('name', '?')}: {dur:.0f}s{parallel})"

    def _f(key: str) -> str:
        v = durations.get(key)
        return f"{v:.1f}s" if isinstance(v, (int, float)) else "n/a"

    # (label, value, optional inline detail)
    rows: list[tuple[str, str, str]] = []
    if isinstance(queue, (int, float)):
        rows.append(("pod_pending_queue", f"{queue:.1f}s", ""))
    rows.append(("init_containers", _f("init_containers"), slowest_tag))
    rows.append(("driver_spark_startup", _f("driver_startup"), ""))
    rows.append(("dataset_loading", _f("dataset_loading"), ""))
    rows.append(("query_exec_and_write", _f("query_exec_write"), ""))

    width = max(len(name) for name, _, _ in rows)
    lines = [f"\n  ── prep{hdr} ──"]
    for name, value, detail in rows:
        suffix = f"  {detail}" if detail else ""
        lines.append(f"      {name.ljust(width)} = {value}{suffix}")
    print("\n".join(lines))


class TransientPollError(RuntimeError):
    pass


def _is_transient_cli_error(text: str) -> bool:
    if not text:
        return False
    low = text.lower()
    if "503" in text and "upstream connect error" in low:
        return True
    if "error trying to reach service" in low:
        return True
    if "proxy error from" in low and ("503" in text or "code 500" in low):
        return True
    if '"reason":"serviceunavailable"' in low:
        return True
    if "connection reset" in low:
        return True
    if "unexpected eof" in low or "http2: server sent goaway" in low:
        return True
    if "ssl connection could not be established" in low:
        return True
    if "httprequestexception" in low and "status: 500" in low:
        return True
    # Agent HttpClient default 100s timeout (cold image pull / JVM init).
    if "taskcanceledexception" in low or "httpclient.timeout" in low:
        return True
    return "status: 502" in low or "status: 504" in low


def parse_tpcds_create_tables(
    sql_path: str,
) -> dict[str, list[tuple[str, str]]]:
    with open(sql_path, encoding="utf-8") as f:
        sql_text = f.read()

    table_block_pattern = re.compile(
        r"(?is)create\s+table\s+([a-zA-Z_]\w*)\s*\((.*?)\)\s*;"
    )

    schemas: dict[str, list[tuple[str, str]]] = {}
    for table_name, block in table_block_pattern.findall(sql_text):
        columns: list[tuple[str, str]] = []
        for raw_line in block.splitlines():
            line = raw_line.strip().rstrip(",")
            if not line or line.lower().startswith("primary key"):
                continue

            col_match = re.match(
                r"^([a-zA-Z_]\w*)\s+" r"([a-zA-Z]+(?:\(\d+(?:\s*,\s*\d+)?\))?)",
                line,
                flags=re.IGNORECASE,
            )
            if col_match:
                columns.append((col_match.group(1), col_match.group(2)))

        if columns:
            schemas[table_name.lower()] = columns

    return schemas


def get_timestamp() -> str:
    return datetime.now().strftime("[%m/%d/%y %H:%M:%S]")


def log_event(level: str, msg: str) -> None:
    print(f"{get_timestamp()} [{level}] {msg}", flush=True)


def log_subevent(level: str, msg: str) -> None:
    print(f"  {get_timestamp()} [{level}] {msg}", flush=True)


def resolve_query_ids(
    friendly_ids: list[str],
    config: dict[str, Any],
    data_format: str,
) -> list[str]:
    queries_map = config.get("queries", {})
    if not queries_map:
        print("  [warn] No queries map in config; using names as-is.")
        return list(friendly_ids)

    resolved: list[str] = []
    for qid in friendly_ids:
        key = f"{qid}_{data_format}"
        if key in queries_map:
            resolved.append(queries_map[key])
        elif qid in queries_map:
            resolved.append(queries_map[qid])
        else:
            expanded = sorted(
                k
                for k in queries_map
                if re.match(
                    rf"^{re.escape(qid)}[a-z]_{re.escape(data_format)}$",
                    k,
                )
            )
            if expanded:
                resolved.extend(queries_map[k] for k in expanded)
            else:
                resolved.append(qid)
    return resolved


def submit_query(
    query_doc_id: str,
    run_id: str = "",
    run_id_prefix: str = "",
    scale_sku: str = "",
) -> dict[str, Any]:
    if not run_id:
        suffix = str(uuid.uuid4())[:8]
        prefix = re.sub(r"[^a-z0-9-]", "-", run_id_prefix.lower()).strip("-")
        run_id = f"{prefix}-{suffix}" if prefix else suffix
    body: dict[str, Any] = {"runId": run_id}
    if scale_sku:
        body["scaleSku"] = scale_sku
    params = json.dumps(body)
    try:
        result = subprocess.run(
            [
                "az",
                "cleanroom",
                "collaboration",
                "spark-sql",
                "execute",
                "--application-name",
                query_doc_id,
                "--application-parameters",
                params,
            ],
            capture_output=True,
            text=True,
            timeout=120,
        )
    except subprocess.TimeoutExpired as e:
        raise TransientPollError(
            f"Submit failed for {query_doc_id}: az CLI timed out after "
            f"120s (likely stale kubectl proxy http2 connection)"
        ) from e
    if result.returncode != 0:
        err = (result.stderr or "").strip()
        if _is_transient_cli_error(err):
            raise TransientPollError(f"Submit failed for {query_doc_id}: {err}")
        raise RuntimeError(f"Submit failed for {query_doc_id}: {err}")
    try:
        response = json.loads(result.stdout)
        response["id"]
        return response
    except (json.JSONDecodeError, KeyError) as e:
        raise RuntimeError(
            f"Submit for {query_doc_id} succeeded (rc=0) but stdout is "
            f"not parsable JSON with an 'id' field: {result.stdout[:200]!r}"
        ) from e


def poll_job_status(
    query_doc_id: str,
    job_id: str,
) -> dict[str, Any]:
    try:
        result = subprocess.run(
            [
                "az",
                "cleanroom",
                "collaboration",
                "spark-sql",
                "get-execution-status",
                "--application-name",
                query_doc_id,
                "--job-id",
                job_id,
            ],
            capture_output=True,
            text=True,
            timeout=60,
        )
    except subprocess.TimeoutExpired as e:
        raise TransientPollError(
            "Status check failed: az CLI timed out after 60s "
            "(likely stale kubectl proxy http2 connection)"
        ) from e
    if result.returncode != 0:
        err = (result.stderr or "").strip()
        if _is_transient_cli_error(err):
            raise TransientPollError(f"Status check failed: {err}")
        raise RuntimeError(f"Status check failed: {err}")
    try:
        return json.loads(result.stdout)
    except json.JSONDecodeError as e:
        raise RuntimeError(
            f"Status check for {job_id} returned rc=0 but stdout is not "
            f"parsable JSON: {result.stdout[:200]!r}"
        ) from e
