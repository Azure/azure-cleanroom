#!/usr/bin/env python3

from __future__ import annotations

import sys as _sys
from pathlib import Path as _Path

_sys.path.insert(0, str(_Path(__file__).resolve().parent.parent / "lib"))

import argparse
import json
import os
import re
import subprocess
import sys
import time
from collections.abc import Callable
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime
from pathlib import Path
from typing import Any

from format_utils import parse_tpcds_query_id
from kubectl_proxy import (
    active_kubectl_proxy,
    cycle_kubectl_proxy,
    ensure_kubectl_proxy_alive,
    start_kubectl_proxy,
    stop_kubectl_proxy,
)
from tpcds_helpers import (
    ANALYTICS_NAMESPACE,
    EXPECTED_GAUGE_COUNT,
    GAUGE_TO_SUMMARY_KEY,
    KUBECTL_PROXY_PORT,
    MAX_STUCK_INIT_RETRIES,
    POLL_ERROR_ABORT_THRESHOLD,
    STATUS_CHECK_INTERVAL_SECONDS,
    STATUS_CHECK_MAX_INTERVAL_SECONDS,
    STUCK_IN_INIT_TIMEOUT_SECONDS,
    StuckInInitError,
    TransientPollError,
    audit_events_to_list,
    collect_prep_phase_breakdown,
    collect_spark_metrics,
    default_kube_config_path,
    default_submit_config_path,
    diagnose_submitted_phase,
    format_audit_events,
    get_audit_events,
    get_driver_failure_detail,
    get_timestamp,
    load_spark_metrics,
    log_event,
    log_subevent,
    merge_prep_into_metrics_file,
    metrics_output_dir,
    missing_critical_gauges,
    poll_job_status,
    prepare_results_file,
    prime_sparkapp_cores,
    print_prep_phase_breakdown,
    resolve_query_ids,
    save_spark_metrics,
    submit_query,
    summary_field_definitions,
    wait_for_analytics_endpoint,
    write_metrics_atomically,
)

_GREEN = "\033[92m"
_RED = "\033[91m"
_YELLOW = "\033[93m"
_RESET = "\033[0m"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Submit TPC-DS queries to Cleanroom analytics."
    )
    parser.add_argument(
        "--config",
        default=default_submit_config_path(),
        help=(
            "Path to submitSqlJobConfig.json from run-scenario.ps1. "
            "Defaults to the newest discovered file in common run output "
            "locations (including workloads/generated/tpcds-analytics) "
            "if present."
        ),
    )
    parser.add_argument(
        "--query-ids",
        nargs="*",
        default=["query1", "query14", "query24", "query64", "query72"],
        help=(
            "Query document IDs to submit "
            "(default: query1 query14 query24 query64 query72)."
        ),
    )
    parser.add_argument(
        "--kube-config",
        default=default_kube_config_path(),
        help=(
            "Path to kubeconfig for kubectl proxy. Defaults to "
            "test/onebox/workloads/generated/cl-cluster/k8s-credentials.yaml "
            "relative to the repo root if present."
        ),
    )
    parser.add_argument(
        "--parallel",
        type=int,
        default=5,
        help="Number of parallel query submissions (default: 5).",
    )
    parser.add_argument(
        "--iterations",
        type=int,
        default=1,
        help="Number of times to execute each query (default: 1).",
    )
    parser.add_argument(
        "--metrics-file",
        default="",
        help="Path to write JSON metrics summary.",
    )
    parser.add_argument(
        "--proxy-port",
        type=int,
        default=KUBECTL_PROXY_PORT,
        help=(f"Local port for ``kubectl proxy``. Default: {KUBECTL_PROXY_PORT}."),
    )
    parser.add_argument(
        "--data-format",
        default="csv,parquet",
        help=(
            "Data format(s) for resolving query IDs. "
            "Comma-separated for multiple (e.g. csv,parquet). "
            "Default: csv,parquet."
        ),
    )
    parser.add_argument(
        "--expect-failure",
        action="store_true",
        help="If set, expect jobs to fail (for policy violation tests).",
    )
    parser.add_argument(
        "--expected-events-json",
        default="",
        help=(
            "JSON string with expected event counts, e.g. "
            '\'{"operational":{"DatasetLoadStarted":2},'
            '"audit":{"DATASET_LOAD_COMPLETED":2}}\''
        ),
    )
    parser.add_argument(
        "--thresholds",
        default="",
        help=(
            "Path to thresholds JSON file or thresholds directory. "
            "When provided (or auto-detected), allow_failure query/format pairs "
            "are treated as non-blocking in submit exit status."
        ),
    )
    return parser.parse_args()


def validate_driver_pod_termination(
    job_status: dict[str, Any],
    kube_config: str,
) -> None:
    driver_info = job_status.get("status", {}).get("driverInfo", {})
    driver_pod = driver_info.get("podName", "")
    if driver_pod and kube_config:
        print(
            f"{get_timestamp()} Checking driver pod '{driver_pod}' exited gracefully..."
        )
        script_dir = Path(__file__).parent
        wait_script = str(
            script_dir.parent.parent.parent
            / "big-data-query-analytics"
            / "wait-for-spark-driver-pod-termination.ps1"
        )
        if Path(wait_script).exists():
            cmd = [
                "pwsh",
                wait_script,
                "-podName",
                driver_pod,
                "-namespace",
                ANALYTICS_NAMESPACE,
                "-kubeConfig",
                kube_config,
            ]
            result = subprocess.run(cmd, capture_output=True, text=True)
            if result.returncode != 0:
                print(f"Script {wait_script} failed: {result.stderr}")
                raise RuntimeError(
                    f"Script {wait_script} exited with code {result.returncode}"
                )
        else:
            print(
                f"{get_timestamp()} [skip] wait-for-spark-driver-pod-termination.ps1 not found."
            )

    executor_state = job_status.get("status", {}).get("executorState", {})
    for pod_name, pod_state in executor_state.items():
        print(f"  Executor pod: {pod_name}, State: {pod_state}")
        if pod_state not in ["COMPLETED", "FAILED"]:
            log_subevent(
                "warn",
                f"Executor pod '{pod_name}' is not terminated "
                f"(state: {pod_state}). May be in transitional state.",
            )


SUBMIT_MAX_ATTEMPTS = 3
SUBMIT_COLD_START_BACKOFF_SECONDS = 120


def submit_query_with_logging(
    query_id: str,
    run_id_prefix: str = "",
) -> str:
    ensure_kubectl_proxy_alive()
    last_err: RuntimeError | None = None
    for attempt in range(1, SUBMIT_MAX_ATTEMPTS + 1):
        try:
            return submit_query(query_id, run_id_prefix=run_id_prefix)
        except RuntimeError as e:
            last_err = e
            msg = str(e)
            low = msg.lower()
            is_transient = isinstance(e, TransientPollError)
            proxy_lost = (
                "client connection lost" in low
                or "http2" in low
                or "eof" in low
                or "connection reset" in low
                or "connection aborted" in low
                or "remotedisconnected" in low
                or "remote end closed connection without response" in low
                or "protocolerror" in low
            )
            if not (is_transient or proxy_lost):
                print(f"{get_timestamp()} [error] az spark-sql execute failed: {e}")
                raise
            if attempt == SUBMIT_MAX_ATTEMPTS:
                print(
                    f"{get_timestamp()} [error] az spark-sql execute failed "
                    f"after {SUBMIT_MAX_ATTEMPTS} attempts: {e}"
                )
                raise
            if proxy_lost:
                print(
                    f"{get_timestamp()} [warn] submit hit transient proxy "
                    f"failure ({msg.split(chr(10))[0][:200]}); cycling "
                    f"kubectl proxy (attempt {attempt}/{SUBMIT_MAX_ATTEMPTS})."
                )
                cycle_kubectl_proxy()
            else:
                print(
                    f"{get_timestamp()} [warn] submit hit transient agent "
                    f"failure ({msg.split(chr(10))[0][:200]}); sleeping "
                    f"{SUBMIT_COLD_START_BACKOFF_SECONDS}s for cold-start "
                    f"(attempt {attempt}/{SUBMIT_MAX_ATTEMPTS})."
                )
                time.sleep(SUBMIT_COLD_START_BACKOFF_SECONDS)
    raise last_err  # unreachable; satisfies type-checkers


def validate_operational_events(
    events: list,
    expected_operational_events: dict[str, int],
) -> None:
    event_counts: dict[str, int] = {}
    for event in events:
        reason = event.get("reason", "Unknown")
        count = event.get("count", 1)
        event_counts[reason] = event_counts.get(reason, 0) + count

    mismatches = []
    for expected_reason, expected_count in expected_operational_events.items():
        actual_count = event_counts.get(expected_reason, 0)
        if actual_count != expected_count:
            mismatches.append(
                f"Event '{expected_reason}': expected {expected_count}, got {actual_count}"
            )

    unmatched_reasons = [
        f"{r} (count: {event_counts[r]})"
        for r in event_counts
        if r not in expected_operational_events
        and not r.startswith(("SparkApplication", "SparkDriver", "SparkExecutor"))
    ]

    if unmatched_reasons:
        log_subevent(
            "warn",
            "Unmatched event reasons:\n    " + "\n    ".join(unmatched_reasons),
        )

    if mismatches:
        error_msg = "Operational event validation failed:\n" + "\n".join(mismatches)
        log_subevent("error", error_msg)
        raise RuntimeError(error_msg)


def validate_audit_events(
    audit_events: list,
    expected_audit_events: dict[str, int],
) -> None:
    event_counts: dict[str, int] = {}
    for event in audit_events:
        message = event.get("data", {}).get("message", "Unknown")
        event_id = message.split("|")[0].strip()
        event_counts[event_id] = event_counts.get(event_id, 0) + 1

    mismatches = []
    for expected_reason, expected_count in expected_audit_events.items():
        match = [x for x in event_counts if re.search(expected_reason, x)]
        if not match:
            mismatches.append(f"Event '{expected_reason}': expected but not found")
            continue
        if event_counts[match[0]] != expected_count:
            mismatches.append(
                f"Event '{expected_reason}': expected {expected_count},"
                f" got {event_counts[match[0]]}"
            )

    unmatched_reasons = []
    for actual_reason in event_counts:
        is_expected = [re.search(exp, actual_reason) for exp in expected_audit_events]
        if not any(is_expected):
            unmatched_reasons.append(
                f"{actual_reason} (count: {event_counts[actual_reason]})"
            )

    if unmatched_reasons:
        log_subevent(
            "warn",
            "Unmatched audit event reasons:\n    " + "\n    ".join(unmatched_reasons),
        )

    if mismatches:
        error_msg = "Audit event validation failed:\n" + "\n".join(mismatches)
        log_subevent("error", error_msg)
        raise RuntimeError(error_msg)


def _dedup_events(
    events: list[dict[str, Any]],
) -> list[dict[str, Any]]:
    by_reason: dict[str, dict[str, Any]] = {}
    for ev in events:
        reason = ev.get("reason", "unknown")
        msg = ev.get("message", "")
        count = ev.get("count", 1)
        existing = by_reason.get(reason)
        if existing is None:
            by_reason[reason] = {
                "reason": reason,
                "count": count,
                "message": msg,
            }
        else:
            existing["count"] += count
            if len(msg) > len(existing["message"]):
                existing["message"] = msg
    return list(by_reason.values())


def _rename_metrics_with_query(metrics_path: Path, query_id: str) -> Path | None:
    friendly, _suffix, fmt = parse_tpcds_query_id(query_id or "")
    if not friendly or not fmt:
        return None
    new_name = f"spark_metrics_{friendly}-{fmt}_{metrics_path.stem.removeprefix('spark_metrics_')}.json"
    if new_name == metrics_path.name:
        return None
    new_path = metrics_path.with_name(new_name)
    try:
        metrics_path.rename(new_path)
    except OSError as e:
        print(
            f"{get_timestamp()} [warn] rename {metrics_path.name} -> {new_name} failed: {e}"
        )
        return None
    return new_path


def _attach_runtime_metrics(
    result: dict[str, Any],
    job_id: str,
    config: dict[str, Any],
    tag: str,
) -> None:
    prom_gauges, _ = _collect_prometheus_metrics(job_id, config, tag)
    result.update(prom_gauges)


def _collect_prometheus_metrics(
    job_id: str,
    config: dict[str, Any],
    query_label: str,
) -> tuple[dict[str, Any], bool]:
    proxy = active_kubectl_proxy()
    if proxy is None:
        return {}, True

    def _log_warmup_failure(msg: str) -> None:
        print(f"{get_timestamp()} [warn] {msg}")

    try:
        gauges, transport_failed = collect_spark_metrics(
            job_id,
            proxy.port,
            proxy.address,
            warmup_failure_logger=_log_warmup_failure,
            kube_config=config.get("_kube_config", ""),
        )
        if transport_failed:
            return {}, True

        if gauges:
            metrics_path, doc = load_spark_metrics(metrics_output_dir(config), job_id)
            summary = doc.get("summary") or {}
            for canonical, val in gauges.items():
                summary_key = GAUGE_TO_SUMMARY_KEY.get(canonical)
                if summary_key:
                    summary[summary_key] = val
            doc["summary"] = summary
            doc["summary_field_definitions"] = summary_field_definitions()
            doc["collection_state"] = "prometheus"
            save_spark_metrics(metrics_path, doc)

            missing = missing_critical_gauges(gauges)
            metrics_file = Path(metrics_path).name
            if missing:
                preview = ", ".join(missing[:3])
                if len(missing) > 3:
                    preview += f", +{len(missing) - 3} more"
                print(
                    f"{_YELLOW}{get_timestamp()} metrics {query_label} "
                    f"\u2192 {len(gauges)}/{EXPECTED_GAUGE_COUNT} gauges "
                    f"(missing critical: {preview}) [{metrics_file}]{_RESET}"
                )
            else:
                print(
                    f"{_GREEN}{get_timestamp()} metrics {query_label} "
                    f"\u2192 {len(gauges)}/{EXPECTED_GAUGE_COUNT} gauges "
                    f"[{metrics_file}]{_RESET}"
                )
        else:
            short_id = job_id.replace("cl-spark-", "")
            print(
                f"{_YELLOW}{get_timestamp()} No spark_final_* metrics "
                f"in Prometheus for job_id={short_id}.{_RESET}"
            )
        return gauges, False
    except Exception as e:
        print(f"{get_timestamp()} [warn] Prometheus query failed: {e}")
        return {}, True


def _handle_completed_state(
    *,
    query_id: str,
    job_id: str,
    tag: str,
    start_time: float,
    running_since: float | None,
    job_status: dict[str, Any],
    config: dict[str, Any],
    kube_config: str,
    expected_events: dict[str, dict[str, int]] | None,
) -> dict[str, Any]:
    operational_events = job_status.get("events", [])
    duration = time.time() - start_time
    print(
        f"{_GREEN}{get_timestamp()} Job {job_id} ({tag}) COMPLETED in {duration:.1f}s{_RESET}"
    )

    if kube_config:
        validate_driver_pod_termination(job_status, kube_config)

    if expected_events and expected_events.get("operational"):
        validate_operational_events(
            operational_events,
            expected_events["operational"],
        )

    _end_time = time.time()
    completed_result: dict[str, Any] = {
        "query_id": query_id,
        "job_id": job_id,
        "state": "COMPLETED",
        "event_count": len(operational_events),
        "_submit_time": start_time,
        "_running_since": running_since,
        "_end_time": _end_time,
    }
    if operational_events:
        completed_result["operational_events"] = _dedup_events(operational_events)

    _attach_runtime_metrics(completed_result, job_id, config, tag)

    return completed_result


def _handle_failed_state(
    *,
    query_id: str,
    job_id: str,
    tag: str,
    start_time: float,
    running_since: float | None,
    job_status: dict[str, Any],
    config: dict[str, Any],
    kube_config: str,
    expect_failure: bool,
) -> dict[str, Any]:
    operational_events = job_status.get("events", [])
    duration = time.time() - start_time
    spark_status = job_status.get("status", {}) or {}
    app_state = spark_status.get("applicationState", {}) or {}
    error_msg = app_state.get("errorMessage", "")
    driver_info = spark_status.get("driverInfo", {})
    executor_state = spark_status.get("executorState", {})
    spark_cr_status = {
        "applicationState": app_state.get("state"),
        "submissionAttempts": spark_status.get("submissionAttempts"),
        "executionAttempts": spark_status.get("executionAttempts"),
        "lastSubmissionAttemptTime": spark_status.get("lastSubmissionAttemptTime"),
        "terminationTime": spark_status.get("terminationTime"),
        "executor_state": executor_state or None,
    }
    spark_cr_status = {k: v for k, v in spark_cr_status.items() if v is not None}
    pod_diagnostics = get_driver_failure_detail(job_id, kube_config)
    if expect_failure:
        print(
            f"{_YELLOW}{get_timestamp()} Job {job_id} ({tag}) FAILED as expected in {duration:.1f}s{_RESET}"
        )
    else:
        print(
            f"{_RED}{get_timestamp()} Job {job_id} ({tag}) FAILED in {duration:.1f}s{_RESET}"
        )
    if error_msg:
        print(f"{_RED}  Error: {error_msg}{_RESET}")
    if pod_diagnostics and pod_diagnostics.get("driver_container"):
        dc = pod_diagnostics["driver_container"]
        print(
            f"{_RED}  Driver container terminated: "
            f"reason={dc.get('reason')} exit_code={dc.get('exit_code')}{_RESET}"
        )
    if pod_diagnostics and pod_diagnostics.get("mount_errors"):
        print(f"{_RED}  Mount/auth errors (root cause):{_RESET}")
        for line in pod_diagnostics["mount_errors"][:3]:
            print(f"{_RED}    {line}{_RESET}")
    elif pod_diagnostics and pod_diagnostics.get("driver_log_tail"):
        print(f"{_RED}  Driver error log:{_RESET}")
        for line in pod_diagnostics["driver_log_tail"][:3]:
            print(f"{_RED}    {line}{_RESET}")
    if driver_info:
        print(f"  Driver pod: {driver_info.get('podName', 'N/A')}")
    if executor_state:
        for pod, st in executor_state.items():
            print(f"  Executor: {pod} -> {st}")
    if operational_events:
        print(f"  Operational events ({len(operational_events)}):")
        for ev in operational_events:
            reason = ev.get("reason", "?")
            msg = ev.get("message", "")
            count = ev.get("count", 1)
            print(f"    [{reason}] (x{count}) {msg}")

    _end_time = time.time()
    failed_result: dict[str, Any] = {
        "query_id": query_id,
        "job_id": job_id,
        "state": "FAILED",
        "event_count": len(operational_events),
        "error_message": error_msg,
        "expected_failure": expect_failure,
        "_submit_time": start_time,
        "_running_since": running_since,
        "_end_time": _end_time,
    }
    if spark_cr_status:
        failed_result["spark_cr_status"] = spark_cr_status
    if pod_diagnostics:
        failed_result["pod_diagnostics"] = pod_diagnostics
    if operational_events:
        failed_result["operational_events"] = _dedup_events(operational_events)

    _attach_runtime_metrics(failed_result, job_id, config, tag)

    return failed_result


def wait_for_completion(
    query_id: str,
    job_id: str,
    config: dict[str, Any],
    expect_failure: bool = False,
    kube_config: str = "",
    expected_events: dict[str, dict[str, int]] | None = None,
    display_id: str = "",
) -> dict[str, Any]:
    tag = display_id or query_id
    start_time = time.time()
    running_since: float | None = None
    last_state: str | None = None
    last_phase_detail: str = ""
    same_state_count = 0
    poll_interval = STATUS_CHECK_INTERVAL_SECONDS
    last_sub_phase: str = ""
    sub_phase_since: float = start_time
    consecutive_poll_errors = 0

    while True:
        try:
            ensure_kubectl_proxy_alive()
            job_status = poll_job_status(query_id, job_id)
            consecutive_poll_errors = 0

            state = job_status["status"]["applicationState"]["state"]

            if state == "COMPLETED":
                return _handle_completed_state(
                    query_id=query_id,
                    job_id=job_id,
                    tag=tag,
                    start_time=start_time,
                    running_since=running_since,
                    job_status=job_status,
                    config=config,
                    kube_config=kube_config,
                    expected_events=expected_events,
                )

            if state == "FAILED":
                return _handle_failed_state(
                    query_id=query_id,
                    job_id=job_id,
                    tag=tag,
                    start_time=start_time,
                    running_since=running_since,
                    job_status=job_status,
                    config=config,
                    kube_config=kube_config,
                    expect_failure=expect_failure,
                )

            prev_state = last_state
            if state == prev_state:
                same_state_count += 1
                poll_interval = min(
                    STATUS_CHECK_INTERVAL_SECONDS + same_state_count * 5,
                    STATUS_CHECK_MAX_INTERVAL_SECONDS,
                )
            else:
                same_state_count = 0
                poll_interval = STATUS_CHECK_INTERVAL_SECONDS

            phase_detail = ""
            sub_phase_key = ""
            if state == "SUBMITTED" and kube_config:
                phase_detail, sub_phase_key = diagnose_submitted_phase(
                    job_id, kube_config
                )

            elapsed = time.time() - start_time
            should_log_transition = state != prev_state or (
                phase_detail and phase_detail != last_phase_detail
            )
            if should_log_transition:
                print(
                    f"{get_timestamp()} Job {job_id} ({tag}) state: {state} "
                    f"({elapsed:.0f}s elapsed)"
                    f"{phase_detail}"
                )
            last_state = state
            last_phase_detail = phase_detail

            if state == "RUNNING" and running_since is None:
                running_since = time.time()

            if sub_phase_key:
                if sub_phase_key != last_sub_phase:
                    last_sub_phase = sub_phase_key
                    sub_phase_since = time.time()
                else:
                    stuck_for = time.time() - sub_phase_since
                    if stuck_for > STUCK_IN_INIT_TIMEOUT_SECONDS:
                        raise StuckInInitError(job_id, sub_phase_key, stuck_for)
            else:
                last_sub_phase = ""
                sub_phase_since = time.time()

        except StuckInInitError:
            raise
        except (subprocess.CalledProcessError, TransientPollError) as e:
            stderr = getattr(e, "stderr", "") or ""
            err_text = stderr or str(e)
            transient = "503" in err_text and "upstream connect error" in err_text
            level = "warn" if transient else "error"
            one_line = " ".join(err_text.split())[:300]
            print(f"{get_timestamp()} [{level}] poll {job_id}: {one_line}")
            if transient:
                consecutive_poll_errors = 0
            else:
                consecutive_poll_errors += 1
                if consecutive_poll_errors >= POLL_ERROR_ABORT_THRESHOLD:
                    abort = RuntimeError(
                        f"Job {job_id} aborted after "
                        f"{consecutive_poll_errors} consecutive "
                        f"non-transient poll errors. Last error: "
                        f"{one_line}"
                    )
                    raise abort from e

        time.sleep(poll_interval)


def _stresstest_run_id_prefix(query_id: str, config: dict[str, Any]) -> str:
    friendly, suffix, fmt = parse_tpcds_query_id(query_id)
    sf = config.get("scaleFactor")
    parts = []
    if sf is not None:
        parts.append(f"sf{sf}")
    if friendly:
        parts.append(f"{friendly}{suffix}")
    if fmt:
        parts.append({"parquet": "pqt"}.get(fmt, fmt))
    return "-".join(parts)


def run_single_query(
    query_id: str,
    iteration: int,
    config: dict[str, Any],
    expect_failure: bool,
) -> dict[str, Any]:
    total_iters = int(config.get("_iterations_total", 0) or 0)
    iter_tag = f"i={iteration}/{total_iters}" if total_iters else f"i={iteration}"
    display_id = f"{query_id} {iter_tag}"
    print(f"\n{'=' * 60}\n{get_timestamp()} {display_id}\n{'=' * 60}")
    _kc = config.get("_kube_config", "")
    attempt = 0
    run_id_prefix = _stresstest_run_id_prefix(query_id, config)
    while True:
        job_id = submit_query_with_logging(query_id, run_id_prefix=run_id_prefix)
        prime_sparkapp_cores(job_id, kube_config=_kc)
        try:
            result = wait_for_completion(
                query_id,
                job_id,
                config,
                expect_failure,
                kube_config=_kc,
                expected_events=config.get("_expected_events"),
                display_id=display_id,
            )
            break
        except StuckInInitError as e:
            attempt += 1
            print(
                f"{get_timestamp()} {e} "
                f"Aborting and retrying (attempt {attempt}/"
                f"{MAX_STUCK_INIT_RETRIES})."
            )
            if attempt > MAX_STUCK_INIT_RETRIES:
                exc = TimeoutError(
                    f"Job {query_id} stuck in init container after "
                    f"{MAX_STUCK_INIT_RETRIES} retries (last sub-phase: "
                    f"{e.sub_phase})."
                )
                raise exc from e
    result["iteration"] = iteration

    audit_events: list[dict[str, Any]] = []
    try:
        contract_id = config.get("contractId", "")
        # If the job reached COMPLETED, the driver should have emitted the
        # dataset-load and dataset-write terminal events. CCF commits them
        # asynchronously, so wait briefly for them to show up before the
        # prep breakdown reads from this list (else dataset_loading /
        # query_exec_and_write surface as n/a).
        wait_for_names: set[str] | None = (
            {"DATASET_LOAD_COMPLETED", "DATASET_WRITE_COMPLETED"}
            if result.get("state") == "COMPLETED"
            else None
        )
        audit_events = get_audit_events(
            contract_id,
            job_id,
            cgs_client=config.get("consumerCgsClient")
            or config.get("publisherCgsClient"),
            wait_for_names=wait_for_names,
        )
        if audit_events:
            print(format_audit_events(audit_events))

            expected_audit = config.get("_expected_events", {}).get("audit")
            if expected_audit:
                validate_audit_events(audit_events, expected_audit)

            result["audit_events"] = audit_events_to_list(audit_events)
    except Exception as e:
        print(f"{get_timestamp()} Warning: Failed to fetch audit events: {e}")

    try:
        prep = collect_prep_phase_breakdown(
            job_id=job_id,
            submit_time=result.get("_submit_time", 0.0),
            running_since=result.get("_running_since"),
            end_time=result.get("_end_time", time.time()),
            audit_events=audit_events,
            kube_config=_kc,
        )
        print_prep_phase_breakdown(prep, label=f"{display_id} ({job_id})")
        # Keep the slow tail (>= 5s, top 5) for post-mortem; drop the rest.
        all_inits = prep.get("init_containers") or []
        slow_inits = sorted(
            (ic for ic in all_inits if (ic.get("duration_seconds") or 0) >= 5.0),
            key=lambda ic: -(ic.get("duration_seconds") or 0),
        )
        if slow_inits:
            prep["init_containers"] = slow_inits[:5]
        else:
            prep.pop("init_containers", None)
        result["prep_phase"] = prep
        if not merge_prep_into_metrics_file(metrics_output_dir(config), job_id, prep):
            result["spark_metrics_write_failed"] = True
    except Exception as e:
        log_event(
            "warn",
            f"prep-phase breakdown failed for {job_id}: {e}",
        )

    try:
        metrics_path, doc = load_spark_metrics(
            metrics_output_dir(config), job_id, default_state="prep_phase_only"
        )
        doc["query_doc_id"] = query_id
        if result.get("state"):
            doc["state"] = result["state"]
        if result.get("expected_failure"):
            doc["expected_failure"] = True
        if result.get("error_message"):
            err = result["error_message"]
            doc["error_message"] = (
                err.splitlines() if isinstance(err, str) and "\n" in err else err
            )
        if result.get("driver_logs"):
            doc["driver_logs"] = result["driver_logs"]
        if result.get("executor_logs"):
            doc["executor_logs"] = result["executor_logs"]
        if result.get("pod_diagnostics"):
            doc["pod_diagnostics"] = result["pod_diagnostics"]
        if result.get("spark_cr_status"):
            doc["spark_cr_status"] = result["spark_cr_status"]
        if result.get("operational_events"):
            doc["operational_events"] = result["operational_events"]
        if result.get("audit_events"):
            doc["audit_events"] = result["audit_events"]
        save_spark_metrics(metrics_path, doc)
        renamed = _rename_metrics_with_query(metrics_path, query_id)
        if renamed is not None:
            metrics_path = renamed
            result["spark_metrics_path"] = str(renamed)
    except Exception as e:
        log_event(
            "warn",
            f"per-job metric enrichment failed for {job_id}: {e}",
        )

    # Heavy blobs live in the per-query spark_metrics file; drop from row.
    for k in (
        "_submit_time",
        "_running_since",
        "_end_time",
        "driver_logs",
        "executor_logs",
        "pod_diagnostics",
        "audit_events",
    ):
        result.pop(k, None)
    return result


def _run_format_queries(
    resolved_ids: list[str],
    iterations: int,
    parallel: int,
    config: dict[str, Any],
    expect_failure: bool,
    on_result: Callable[[dict[str, Any]], None] | None = None,
) -> tuple[list[dict[str, Any]], float]:
    work_items = [(qid, i) for i in range(1, iterations + 1) for qid in resolved_ids]

    config["_iterations_total"] = iterations

    def _stamped_run(qid: str, it: int, enqueue_ts: float) -> dict[str, Any]:
        submit_ts = time.time()
        try:
            r = run_single_query(qid, it, config, expect_failure)
        except Exception as exc:
            print(f"Error running {qid} iteration {it}: {exc}")
            r = {
                "query_id": qid,
                "iteration": it,
                "state": "ABORTED",
                "error": str(exc),
            }
        r["submitted_at"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(submit_ts))
        r["queue_wait_s"] = round(submit_ts - enqueue_ts, 3)
        r["scale_factor"] = config.get("scaleFactor")
        return r

    results: list[dict[str, Any]] = []
    wall_start = time.time()

    def _record(r: dict[str, Any]) -> None:
        results.append(r)
        if on_result is not None:
            try:
                on_result(r)
            except Exception as cb_err:
                print(
                    f"{get_timestamp()} [warn] partial-flush callback failed: {cb_err}"
                )

    if parallel <= 1:
        for query_id, iteration in work_items:
            now = time.time()
            _record(_stamped_run(query_id, iteration, now))
    else:
        with ThreadPoolExecutor(max_workers=parallel) as executor:
            futures: dict[Any, tuple[str, int, float]] = {}
            for idx, (qid, it) in enumerate(work_items):
                if idx > 0:
                    # Stagger consecutive spark-submit calls to avoid bursting
                    # the k8s apiserver, which can exceed the fabric8 client's
                    # default 10s POST timeout when many drivers submit at once.
                    # TODO: remove this workaround once the admission webhook
                    # logic is fixed to handle concurrent driver-pod creates
                    # without serializing them under a 10s apiserver timeout.
                    time.sleep(0.5)
                enq = time.time()
                fut = executor.submit(_stamped_run, qid, it, enq)
                futures[fut] = (qid, it, enq)
            for future in as_completed(futures):
                qid, it, enqueue_ts = futures[future]
                try:
                    _record(future.result())
                except Exception as e:
                    now = time.time()
                    print(f"Executor error for {qid} iteration {it}: {e}")
                    err_row: dict[str, Any] = {
                        "query_id": qid,
                        "iteration": it,
                        "state": "ABORTED",
                        "error": str(e),
                        "submitted_at": time.strftime(
                            "%Y-%m-%dT%H:%M:%SZ", time.gmtime(now)
                        ),
                        "queue_wait_s": round(now - enqueue_ts, 3),
                    }
                    _record(err_row)
    wall_clock_s = round(time.time() - wall_start, 3)
    return results, wall_clock_s


def _resolve_metrics_file(args: argparse.Namespace, config: dict[str, Any]) -> str:
    if args.metrics_file:
        return args.metrics_file
    sf_for_path = config.get("scaleFactor", "unknown")
    contract_for_path = config.get("contractId", "unknown")
    # Keep run outputs under the stresstest/runner tree so all generated
    # artefacts (summary JSON + per-job spark_metrics_*.json under
    # spark-metrics/sf<SF>/) live next to this script rather than being
    # scattered into wherever submitSqlJobConfig.json happens to be.
    default_metrics_dir = str(Path(__file__).resolve().parent / "generated" / "results")
    os.makedirs(default_metrics_dir, exist_ok=True)
    return os.path.join(
        default_metrics_dir,
        f"tpcds_submission_metrics_sf{sf_for_path}_{contract_for_path}.json",
    )


def _tag_result(r: dict[str, Any], fmt: str, friendly_ids: list[str]) -> None:
    r["data_format"] = fmt
    qid = r.get("query_id", "")
    for fid in friendly_ids:
        if fid == qid or re.match(rf"^tpcds-{re.escape(fid)}[a-z]?-", qid):
            r["friendly_id"] = fid
            break


def _build_run_metrics(
    *,
    args: argparse.Namespace,
    config: dict[str, Any],
    friendly_ids: list[str],
    formats: list[str],
    all_results: list[dict[str, Any]],
    wall_clock_by_format: dict[str, float],
    partial: bool = False,
) -> dict[str, Any]:
    completed_now = [r for r in all_results if r.get("state") == "COMPLETED"]
    failed_now = [r for r in all_results if r.get("state") == "FAILED"]
    aborted_now = [r for r in all_results if r.get("state") == "ABORTED"]
    return {
        "timestamp": datetime.now().isoformat(),
        "partial": partial,
        "config": {
            "contract_id": config.get("contractId"),
            "scale_factor": config.get("scaleFactor"),
            "query_ids": friendly_ids,
            "formats": formats,
            "iterations": args.iterations,
            "parallel": args.parallel,
        },
        "wall_clock_by_format_seconds": {
            k: round(v, 3) for k, v in wall_clock_by_format.items()
        },
        "summary": {
            "total": len(all_results),
            "completed": len(completed_now),
            "failed": len(failed_now),
            "aborted": len(aborted_now),
        },
        "run_outcomes": _build_run_outcomes(all_results),
        "results": list(all_results),
    }


def _build_run_outcomes(all_results: list[dict[str, Any]]) -> list[dict[str, Any]]:
    outcomes: list[dict[str, Any]] = []
    for r in all_results:
        qid = r.get("query_id") or ""
        friendly = r.get("friendly_id")
        fmt = r.get("data_format")
        if not friendly or not fmt:
            parsed_friendly, _, parsed_fmt = parse_tpcds_query_id(qid)
            friendly = friendly or parsed_friendly or qid or "unknown"
            fmt = fmt or parsed_fmt or "unknown"
        state = (r.get("state") or "UNKNOWN").upper()
        outcomes.append(
            {
                "query": friendly,
                "format": fmt,
                "iteration": r.get("iteration"),
                "state": state,
            }
        )
    outcomes.sort(
        key=lambda o: (
            o["query"],
            o["format"],
            o["iteration"] if o["iteration"] is not None else 0,
        )
    )
    return outcomes


def _execute_all_formats(
    *,
    args: argparse.Namespace,
    config: dict[str, Any],
    friendly_ids: list[str],
    formats: list[str],
    all_results: list[dict[str, Any]],
) -> dict[str, float]:
    wall_clock_by_format: dict[str, float] = {}

    def _on_iteration(fmt: str, r: dict[str, Any]) -> None:
        _tag_result(r, fmt, friendly_ids)
        all_results.append(r)
        if args.metrics_file:
            write_metrics_atomically(
                Path(args.metrics_file),
                _build_run_metrics(
                    args=args,
                    config=config,
                    friendly_ids=friendly_ids,
                    formats=formats,
                    all_results=all_results,
                    wall_clock_by_format=wall_clock_by_format,
                    partial=True,
                ),
            )

    for fmt in formats:
        resolved_ids = resolve_query_ids(friendly_ids, config, fmt)
        _, fmt_wall = _run_format_queries(
            resolved_ids,
            args.iterations,
            args.parallel,
            config,
            args.expect_failure,
            on_result=lambda r, fmt=fmt: _on_iteration(fmt, r),
        )
        wall_clock_by_format[fmt] = fmt_wall

    return wall_clock_by_format


def _persist_job_ids_to_config(
    config_path: str,
    results: list[dict[str, Any]],
    *,
    verbose: bool = False,
) -> None:
    path = Path(config_path) if config_path else None
    if path is None or not path.exists():
        return

    job_ids = sorted(
        {
            job_id.strip()
            for r in results
            for job_id in [str(r.get("job_id") or "")]
            if job_id.strip()
        }
    )
    if not job_ids:
        return

    try:
        with open(path, encoding="utf-8") as f:
            payload = json.load(f)
        if payload.get("jobIds") == job_ids:
            return

        payload["jobIds"] = job_ids
        with open(path, "w", encoding="utf-8") as f:
            json.dump(payload, f, indent=2)
            f.write("\n")

        if verbose:
            print(f"{get_timestamp()} [info] Wrote {len(job_ids)} jobIds to {path}.")
    except Exception as e:
        print(f"{get_timestamp()} [warn] Unable to persist jobIds to {path}: {e}")


def _resolve_thresholds_path(path_arg: str, scale_factor: int | None) -> Path | None:
    if not path_arg:
        return None

    candidate = Path(path_arg)
    if not candidate.exists():
        return None
    if candidate.is_file():
        return candidate
    if scale_factor is None:
        return None
    sf_path = candidate / f"sf{scale_factor}.json"
    return sf_path if sf_path.exists() else None


def _load_non_blocking_pairs_from_thresholds(
    thresholds_path: Path | None,
    scale_factor: int | None,
) -> set[tuple[str, str]]:
    if thresholds_path is None:
        return set()

    try:
        with open(thresholds_path, encoding="utf-8") as f:
            doc = json.load(f)
    except Exception as e:
        print(
            f"{get_timestamp()} [warn] Unable to read thresholds file "
            f"{thresholds_path}: {e}"
        )
        return set()

    pairs: set[tuple[str, str]] = set()
    for entry in doc.get("thresholds") or []:
        if not entry.get("allow_failure"):
            continue
        sf = entry.get("scale_factor")
        query = str(entry.get("query") or "")
        fmt = str(entry.get("format") or "")
        if not query or not fmt:
            continue
        if scale_factor is not None and sf != scale_factor:
            continue
        pairs.add((query, fmt))
    return pairs


def _result_pair(row: dict[str, Any]) -> tuple[str, str]:
    qid = str(row.get("friendly_id") or row.get("_friendly_id") or "")
    if not qid:
        query_id = str(row.get("query_id") or "")
        match = re.search(r"(query\d+)", query_id)
        qid = match.group(1) if match else query_id
    fmt = str(row.get("data_format") or "")
    return qid, fmt


def _print_local_summary(
    all_results: list[dict[str, Any]],
    wall_clock_by_format: dict[str, float],
    non_blocking_pairs: set[tuple[str, str]],
) -> None:
    if not all_results:
        return

    outcomes = _build_run_outcomes(all_results)

    color = {
        "COMPLETED": _GREEN,
        "FAILED": _RED,
        "ABORTED": _RED,
    }
    counts: dict[str, int] = {}
    for o in outcomes:
        counts[o["state"]] = counts.get(o["state"], 0) + 1

    bar = "=" * 78
    print(f"\n{bar}\n  TPC-DS Local Run Summary\n{bar}")
    totals = ", ".join(f"{k}={v}" for k, v in sorted(counts.items()))
    print(f"Total: {len(outcomes)} job(s) - {totals}")
    if wall_clock_by_format:
        wc = ", ".join(f"{k}={v:.1f}s" for k, v in sorted(wall_clock_by_format.items()))
        print(f"Wall clock: {wc}")

    print(f"\n  {'Query':<14} {'Format':<10} {'Iter':>4}  {'State':<10}")
    print(f"  {'-' * 14} {'-' * 10} {'-' * 4}  {'-' * 10}")
    for o in outcomes:
        q, f, it, st = o["query"], o["format"], o["iteration"], o["state"]
        non_blocking = (
            "*" if (q, f) in non_blocking_pairs and st != "COMPLETED" else " "
        )
        c = color.get(st, "")
        reset = _RESET if c else ""
        it_str = str(it) if it is not None else "-"
        print(f"  {q:<14} {f:<10} {it_str:>4}  {c}{st:<10}{reset}{non_blocking}")
    if any(
        (o["query"], o["format"]) in non_blocking_pairs and o["state"] != "COMPLETED"
        for o in outcomes
    ):
        print("\n  * = non-blocking failure (allowed by thresholds)")
    print(f"{bar}\n")


def main() -> None:
    args = parse_args()

    if not args.config:
        print(
            f"{get_timestamp()} [error] --config is required and the "
            f"default config file was not "
            f"found. Run run-scenario.ps1 first or pass --config "
            f"explicitly."
        )
        sys.exit(2)

    with open(args.config, encoding="utf-8") as f:
        config = json.load(f)

    if not os.environ.get("CLEANROOM_COLLABORATION_CONFIG_FILE"):
        collab_config = config.get("collaborationConfigFile")
        if collab_config and os.path.exists(collab_config):
            os.environ["CLEANROOM_COLLABORATION_CONFIG_FILE"] = collab_config

    friendly_ids = list(args.query_ids)
    if not friendly_ids:
        print(f"{get_timestamp()} [error] Specify --query-ids.")
        sys.exit(1)

    formats = [f.strip() for f in args.data_format.split(",") if f.strip()]

    scale_factor = None
    sf_raw = config.get("scaleFactor")
    if isinstance(sf_raw, int):
        scale_factor = sf_raw
    elif isinstance(sf_raw, str) and sf_raw.isdigit():
        scale_factor = int(sf_raw)

    thresholds_arg = args.thresholds
    if not thresholds_arg:
        default_threshold_dir = Path(__file__).parent.parent / "analysis" / "thresholds"
        if default_threshold_dir.exists():
            thresholds_arg = str(default_threshold_dir)

    thresholds_path = _resolve_thresholds_path(thresholds_arg, scale_factor)
    non_blocking_pairs = _load_non_blocking_pairs_from_thresholds(
        thresholds_path,
        scale_factor,
    )
    if non_blocking_pairs:
        print(
            f"{get_timestamp()} [info] Non-blocking failures from thresholds: "
            + ", ".join(f"{qid}/{fmt}" for qid, fmt in sorted(non_blocking_pairs))
        )

    if args.kube_config:
        config["_kube_config"] = args.kube_config
    if args.expected_events_json:
        config["_expected_events"] = json.loads(args.expected_events_json)

    args.metrics_file = _resolve_metrics_file(args, config)
    if args.metrics_file:
        config["_metrics_dir"] = os.path.dirname(args.metrics_file)
        prepare_results_file(
            Path(args.metrics_file),
            spark_metrics_dir=metrics_output_dir(config),
        )

    try:
        if args.kube_config:
            proxy_address = start_kubectl_proxy(args.kube_config, args.proxy_port)
            wait_for_analytics_endpoint(proxy_address, args.proxy_port)

        all_results: list[dict[str, Any]] = []
        overall_wall_start = time.time()
        config["_run_start_ts"] = overall_wall_start

        wall_clock_by_format: dict[str, float] = {}
        try:
            wall_clock_by_format = _execute_all_formats(
                args=args,
                config=config,
                friendly_ids=friendly_ids,
                formats=formats,
                all_results=all_results,
            )
        except Exception as fatal:
            if args.metrics_file:
                try:
                    fatal_metrics = _build_run_metrics(
                        args=args,
                        config=config,
                        friendly_ids=friendly_ids,
                        formats=formats,
                        all_results=all_results,
                        wall_clock_by_format=wall_clock_by_format,
                        partial=True,
                    )
                    fatal_metrics["fatal_error"] = str(fatal)
                    write_metrics_atomically(Path(args.metrics_file), fatal_metrics)
                    print(
                        f"{get_timestamp()} [warn] Fatal error captured; partial metrics persisted to {args.metrics_file}"
                    )
                except Exception as persist_err:
                    print(
                        f"{get_timestamp()} [warn] Unable to persist partial metrics on fatal error: {persist_err}"
                    )
            raise

        failed = [r for r in all_results if r.get("state") == "FAILED"]
        errors = [r for r in all_results if r.get("state") == "ABORTED"]
        blocking_failed = [
            r for r in failed if _result_pair(r) not in non_blocking_pairs
        ]
        blocking_errors = [
            r for r in errors if _result_pair(r) not in non_blocking_pairs
        ]
        has_failures = bool(blocking_failed or blocking_errors)

        if args.metrics_file:
            metrics = _build_run_metrics(
                args=args,
                config=config,
                friendly_ids=friendly_ids,
                formats=formats,
                all_results=all_results,
                wall_clock_by_format=wall_clock_by_format,
                partial=False,
            )
            write_metrics_atomically(Path(args.metrics_file), metrics)

        _persist_job_ids_to_config(args.config, all_results, verbose=has_failures)

        _print_local_summary(all_results, wall_clock_by_format, non_blocking_pairs)

        if args.expect_failure:
            completed = sum(1 for r in all_results if r.get("state") == "COMPLETED")
            if completed > 0:
                print(
                    f"{get_timestamp()} [error] --expect-failure was set but "
                    f"{completed} job(s) COMPLETED successfully. This indicates "
                    f"a governance/policy regression."
                )
                sys.exit(2)
        else:
            ignored_failures = len(failed) - len(blocking_failed)
            ignored_errors = len(errors) - len(blocking_errors)
            ignored_total = ignored_failures + ignored_errors
            if ignored_total:
                print(
                    f"{get_timestamp()} [info] Ignoring {ignored_total} non-blocking "
                    f"job(s) from thresholds "
                    f"(failed={ignored_failures}, errors={ignored_errors})."
                )
            if blocking_failed or blocking_errors:
                print(
                    f"{get_timestamp()} [warn] "
                    "Blocking FAILED/ERROR rows detected. "
                    "Submit step will still exit 0; verify_run.py "
                    "is the single pass/fail gate."
                )
    finally:
        stop_kubectl_proxy()


if __name__ == "__main__":
    main()
