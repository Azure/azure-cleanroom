#!/usr/bin/env python3
"""CI gate for a TPC-DS run metrics JSON.

Checks:
1) Every requested (query x format x iteration) produced at least one row.
2) Success rate over query/format cells is >= --min-success-rate. A cell passes
    if at least one attempt COMPLETED, so a query that failed once but succeeded
    on a rerun counts as a pass; "queries failed" = cells with no successful
    attempt.
3) Optional per-(scale, query, format) thresholds can mark unstable cases as
    non-blocking and apply duration caps to historically stable cases.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from collections import Counter, defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "lib"))

from format_utils import (
    exclude_duration_outliers,
    extract_scale_factor,
    parse_tpcds_query_id,
    rank_percentile,
)

_STRESS_DIR = Path(__file__).resolve().parent.parent
_WORKLOADS_GENERATED = (
    _STRESS_DIR / ".." / ".." / ".." / "workloads" / "generated"
).resolve()
_WORKLOAD_SAMPLES_AKS_GENERATED = (
    _STRESS_DIR / ".." / ".." / ".." / "workload-samples-aks" / "generated"
).resolve()


def _parse_tokens(raw: str, *, sep: str) -> list[str]:
    if sep == " ":
        return [token for token in re.split(r"[\s,]+", raw.strip()) if token]
    return [token.strip() for token in raw.split(sep) if token.strip()]


def _result_query_id(row: dict) -> str:
    for key in ("friendly_id", "_friendly_id"):
        val = row.get(key)
        if val:
            return str(val)
    query_id = str(row.get("query_id") or "")
    if not query_id:
        return ""
    friendly, _, _ = parse_tpcds_query_id(query_id)
    return friendly or query_id


def _result_iteration(row: dict) -> int | None:
    val = row.get("iteration")
    if isinstance(val, int):
        return val
    if isinstance(val, str) and val.isdigit():
        return int(val)
    return None


def _result_duration_seconds(row: dict) -> float | None:
    val = (
        (row.get("prep_phase") or {})
        .get("timeline_offsets_sec", {})
        .get("process_exit")
    )
    if isinstance(val, (int, float)):
        return float(val)
    return None


def _load_threshold_index(path: Path) -> dict[tuple[int, str, str], dict]:
    with open(path, encoding="utf-8") as f:
        doc = json.load(f)

    index: dict[tuple[int, str, str], dict] = {}
    for entry in doc.get("thresholds") or []:
        sf = entry.get("scale_factor")
        query = str(entry.get("query") or "")
        fmt = str(entry.get("format") or "")
        if not isinstance(sf, int) or not query or not fmt:
            continue
        index[(sf, query, fmt)] = entry
    return index


def _resolve_thresholds_path(path_arg: str, scale_factor: int | None) -> Path | None:
    if not path_arg:
        return None
    candidate = Path(path_arg)
    if not candidate.is_dir():
        return candidate
    if scale_factor is None:
        return None
    return candidate / f"sf{scale_factor}.json"


def _run_parallel(metrics: dict) -> int | None:
    config = metrics.get("config")
    if isinstance(config, dict):
        val = config.get("parallel")
        if isinstance(val, bool):
            return None
        if isinstance(val, int):
            return val
        if isinstance(val, str) and val.isdigit():
            return int(val)
    return None


def _select_baseline_p95(entry: dict, run_parallel: int | None) -> float | None:
    # Per-concurrency baselines: high-parallel slots (e.g. sf100 p16) drive the
    # cluster to its pod ceiling, so queries queue for executor slots and their
    # wall-clock p95 inflates well beyond the single-query baseline. When the
    # thresholds entry carries a "baseline_p95_by_parallel" map, pick the
    # baseline calibrated for this run's --parallel (exact key, else the largest
    # calibrated concurrency <= run_parallel). Falls back to the flat
    # "baseline_p95_seconds" when no by-parallel calibration applies.
    by_par = entry.get("baseline_p95_by_parallel")
    if isinstance(by_par, dict) and run_parallel is not None:
        exact = by_par.get(str(run_parallel))
        if isinstance(exact, (int, float)) and not isinstance(exact, bool):
            return float(exact)
        applicable = [
            (int(k), v)
            for k, v in by_par.items()
            if str(k).isdigit()
            and int(k) <= run_parallel
            and isinstance(v, (int, float))
            and not isinstance(v, bool)
        ]
        if applicable:
            return float(max(applicable, key=lambda kv: kv[0])[1])
    base = entry.get("baseline_p95_seconds")
    if isinstance(base, (int, float)) and not isinstance(base, bool):
        return float(base)
    return None


def _append_summary(text: str) -> None:
    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if not summary_path:
        return
    with open(summary_path, "a", encoding="utf-8") as f:
        f.write(text)


def _render_per_query_table(
    query_ids: list[str],
    formats: list[str],
    per_cell_totals: dict[tuple[str, str], int],
    per_cell_completed: dict[tuple[str, str], int],
    per_cell_states: dict[tuple[str, str], dict[str, int]],
    durations_by_key: dict[tuple[str, str], list[float]],
    duration_limits: dict[tuple[str, str], float],
    threshold_non_blocking_keys: set[tuple[str, str]],
    grace_pct: float,
) -> list[str]:
    if not query_ids or not formats:
        return []
    grace_factor = 1.0 + grace_pct / 100.0
    lines: list[str] = []
    for fmt in formats:
        rows: list[str] = []
        for qid in query_ids:
            key = (qid, fmt)
            total = per_cell_totals.get(key, 0)
            if total == 0:
                continue
            done = per_cell_completed.get(key, 0)
            durs = durations_by_key.get(key) or []
            p95 = rank_percentile(exclude_duration_outliers(durs), 95)
            limit = duration_limits.get(key)
            effective = limit * grace_factor if limit is not None else None
            states = per_cell_states.get(key) or {}
            states_str = ", ".join(f"{s}={n}" for s, n in sorted(states.items())) or "-"
            if key in threshold_non_blocking_keys:
                status = "SKIP"
            elif done == 0:
                status = "FAIL"
            elif effective is not None and p95 is not None:
                status = "FAIL" if p95 > effective else "PASS"
            else:
                status = "PASS"
            rows.append(
                f"| `{qid}` | {done}/{total} | {states_str} | "
                f"{format(p95, '.1f') if p95 is not None else '-'} | "
                f"{format(limit, '.1f') if limit is not None else '-'} | "
                f"{format(effective, '.1f') if effective is not None else '-'} | "
                f"{status} |"
            )
        if not rows:
            continue
        lines.extend(
            [
                "",
                f"### Per query results - `{fmt}`",
                "",
                f"_Effective = Baseline p95 x (1 + {grace_pct:.0f}% grace); FAIL iff p95 > Effective._",
                "",
                "| Query | Passed | States | p95 (s) | Baseline p95 (s) | Effective (s) | Status |",
                "|---|---:|---|---:|---:|---:|:---:|",
            ]
        )
        lines.extend(rows)
    return lines


def _infer_scale_from_thresholds_arg(path_arg: str) -> int | None:
    if not path_arg:
        return None
    candidate = Path(path_arg)
    if candidate.is_dir():
        return None
    match = re.search(r"sf(\d+)\.json$", candidate.name)
    if not match:
        return None
    return int(match.group(1))


def _discover_metrics_file(preferred_scale: int | None = None) -> Path | None:
    candidates = []
    patterns = [
        _STRESS_DIR
        / "runner"
        / "generated"
        / "results"
        / "tpcds_submission_metrics_sf*.json",
        _WORKLOADS_GENERATED
        / "tpcds-analytics"
        / "results"
        / "tpcds_submission_metrics_sf*.json",
        _WORKLOAD_SAMPLES_AKS_GENERATED
        / "tpcds-analytics"
        / "results"
        / "tpcds_submission_metrics_sf*.json",
        _STRESS_DIR / "generated" / "results" / "tpcds_submission_metrics_sf*.json",
    ]
    for pattern in patterns:
        candidates.extend(pattern.parent.glob(pattern.name))
    if not candidates:
        return None
    if preferred_scale is not None:
        token = f"_sf{preferred_scale}_"
        scale_candidates = [c for c in candidates if token in c.name]
        if scale_candidates:
            candidates = scale_candidates
    return max(candidates, key=lambda p: p.stat().st_mtime)


_ANALYTICS_NS = "analytics"

# (regex, nature, human label) — first match wins. Nature drives recoverability:
#   Transient  = auto-retried / self-heals (harness resubmits or polling recovers)
#   Terminating = attempt-fatal (the driver/executor died; may recover only on retry)
#   Blocker    = needs quota / capacity / config fix (retries won't help)
_FAILURE_SIGNATURES: list[tuple[re.Pattern[str], str, str]] = [
    (
        re.compile(r"no space left on device|ephemeral.?storage", re.I),
        "Terminating",
        "Ephemeral storage exhausted (No space left on device)",
    ),
    (
        re.compile(
            r"oomkilled|outofmemory|sparkexitcode\.oom|exit ?code:? ?52\b", re.I
        ),
        "Terminating",
        "Executor OOM (52)",
    ),
    (
        re.compile(r"\b137\b|oom-?killed", re.I),
        "Terminating",
        "OOM-killed (137)",
    ),
    (
        re.compile(r"maxnumfailures|exit ?code:? ?11\b|failed with exitcode: 11", re.I),
        "Terminating",
        "Driver abort — executor failure cascade (11)",
    ),
    (
        re.compile(r"stuck.?in.?init|stuckininit", re.I),
        "Transient",
        "Stuck in init (CACI cold-start)",
    ),
    (
        re.compile(r"timeout|timed ?out|10000ms|nostacktracetimeout", re.I),
        "Transient",
        "Submission / API timeout",
    ),
    (
        re.compile(
            r"upstream connect error|connection reset|remotedisconnected|\b503\b|\b500\b",
            re.I,
        ),
        "Transient",
        "Transient network (HTTP 503/500 / connection reset)",
    ),
    (
        re.compile(
            r"failedcreatepodsandbox|not available in the location|resource is not available",
            re.I,
        ),
        "Blocker",
        "Regional CACI capacity (FailedCreatePodSandBox)",
    ),
    (
        re.compile(
            r"no nodes available with capacity|cleanroom-spark-pod-scheduler|"
            r"per-node pod limit|podcountconstraint|pod_count",
            re.I,
        ),
        "Blocker",
        "VN2 pod-slot capacity — executor scheduling denied (POD_COUNT ceiling)",
    ),
    (
        re.compile(r"verifysnpattestationfailed|attestation claims do not match", re.I),
        "Blocker",
        "Attestation / policy mismatch",
    ),
    (
        re.compile(r"confidentialcores|confidentialcontainergroups|\bquota\b", re.I),
        "Blocker",
        "Confidential-ACI quota",
    ),
]

_SPARKAPP_QF_RX = re.compile(r"sf\d+-(query[0-9a-z]+)-(csv|pqt|parquet)\b", re.I)


def _classify_failure(text: str) -> tuple[str, str]:
    for rx, nature, label in _FAILURE_SIGNATURES:
        if rx.search(text or ""):
            return nature, label
    return "Terminating", "Unclassified failure"


def _matches_known_signature(text: str) -> bool:
    return any(rx.search(text or "") for rx, _, _ in _FAILURE_SIGNATURES)


def _parse_sparkapp_qf(name: str) -> tuple[str, str]:
    # cl-spark-sf1000-query14a-csv-<hash>[-exec-N|-driver] -> (friendly qid, format)
    m = _SPARKAPP_QF_RX.search(name or "")
    if not m:
        return "", ""
    friendly, _, _ = parse_tpcds_query_id(m.group(1))
    fmt = m.group(2).lower()
    if fmt == "pqt":
        fmt = "parquet"
    return (friendly or m.group(1)), fmt


def _kubectl_json(kube_config: str, args: list[str]) -> dict | None:
    cmd = ["kubectl"]
    if kube_config:
        cmd += ["--kubeconfig", kube_config]
    cmd += ["-n", _ANALYTICS_NS, *args, "-o", "json"]
    try:
        out = subprocess.run(cmd, capture_output=True, text=True, timeout=120)
    except Exception:
        return None
    if out.returncode != 0 or not out.stdout.strip():
        return None
    try:
        return json.loads(out.stdout)
    except Exception:
        return None


def _collect_execution_findings(
    results: list[dict],
    failed_cells: list[tuple[str, str]],
    kube_config: str,
) -> list[dict]:
    """Gather Spark-execution failures/OOM/timeouts from the run metrics and the
    live cluster (best-effort). Each finding records whether it caused the query
    to fail (the cell had no successful attempt) vs. was absorbed (retried or
    another attempt still COMPLETED)."""
    failed_set = set(failed_cells)
    findings: list[dict] = []
    seen: set[tuple] = set()

    def add(
        query: str, fmt: str, label: str, nature: str, caused: str, source: str
    ) -> None:
        key = (query, fmt, label, source)
        if key in seen:
            return
        seen.add(key)
        findings.append(
            {
                "query": query,
                "fmt": fmt,
                "label": label,
                "nature": nature,
                "caused": caused,
                "source": source,
            }
        )

    # 1) From the run metrics: any non-COMPLETED attempt or recorded error.
    for row in results:
        state = str(row.get("state") or "")
        err = str(row.get("error") or "")
        if state not in ("FAILED", "ABORTED") and not err:
            continue
        qid = _result_query_id(row)
        fmt = str(row.get("data_format") or "")
        nature, label = _classify_failure(f"{state} {err}")
        if state == "ABORTED" and not _matches_known_signature(err):
            nature, label = "Transient", "Aborted (retries exhausted / stuck-in-init)"
        caused = "Yes" if (qid, fmt) in failed_set else "No"
        add(
            qid, fmt, f"{state}: {label}" if state else label, nature, caused, "metrics"
        )

    # 2) From the live cluster (kubeconfig) — best-effort; skipped if unreachable.
    def caused_for(q: str, fmt: str) -> str:
        if (q, fmt) in failed_set:
            return "Yes"
        return "No" if q else "—"

    apps = _kubectl_json(kube_config, ["get", "sparkapplication"])
    for it in (apps or {}).get("items", []):
        app_state = ((it.get("status") or {}).get("applicationState") or {}).get(
            "state"
        ) or ""
        msg = ((it.get("status") or {}).get("applicationState") or {}).get(
            "errorMessage"
        ) or ""
        name = (it.get("metadata") or {}).get("name") or ""
        if app_state.upper() not in ("FAILED", "UNKNOWN") and not msg:
            continue
        q, fmt = _parse_sparkapp_qf(name)
        nature, label = _classify_failure(f"{app_state} {msg}")
        add(
            q,
            fmt,
            f"SparkApplication {app_state}: {label}",
            nature,
            caused_for(q, fmt),
            "k8s/sparkapp",
        )

    pods = _kubectl_json(kube_config, ["get", "pods"])
    for it in (pods or {}).get("items", []):
        name = (it.get("metadata") or {}).get("name") or ""
        for cs in (it.get("status") or {}).get("containerStatuses") or []:
            term = (cs.get("lastState") or {}).get("terminated") or {}
            waiting = (cs.get("state") or {}).get("waiting") or {}
            reason = term.get("reason") or ""
            exit_code = term.get("exitCode")
            wreason = waiting.get("reason") or ""
            oom = reason == "OOMKilled"
            bad_exit = isinstance(exit_code, int) and exit_code != 0
            sandbox = "FailedCreatePodSandBox" in wreason
            if not (oom or bad_exit or sandbox):
                continue
            blob = (
                f"{reason} exitCode {exit_code} {wreason} {waiting.get('message', '')}"
            )
            q, fmt = _parse_sparkapp_qf(name)
            nature, label = _classify_failure(blob)
            tag = reason or wreason or f"exit {exit_code}"
            add(q, fmt, f"pod {tag}: {label}", nature, caused_for(q, fmt), "k8s/pod")

    events = _kubectl_json(
        kube_config, ["get", "events", "--field-selector", "type=Warning"]
    )
    for it in (events or {}).get("items", []):
        msg = it.get("message") or ""
        if not _matches_known_signature(msg):
            continue
        obj = (it.get("involvedObject") or {}).get("name") or ""
        q, fmt = _parse_sparkapp_qf(obj)
        nature, label = _classify_failure(msg)
        add(q, fmt, label, nature, caused_for(q, fmt), "k8s/event")

    return findings


def _render_execution_findings(findings: list[dict]) -> list[str]:
    out = ["", "### Spark execution — failures / OOM / timeouts"]
    if not findings:
        out += [
            "",
            "✅ No Spark execution failures, OOM, or timeouts observed "
            "(all attempts completed on first submit).",
        ]
        return out
    out += [
        "",
        "| Query | Format | Issue | Nature | Caused query failure? | Source |",
        "|---|---|---|:--:|:--:|---|",
    ]
    order = {"Blocker": 0, "Terminating": 1, "Transient": 2}
    for f in sorted(
        findings,
        key=lambda f: (f["caused"] != "Yes", order.get(f["nature"], 9), f["query"]),
    ):
        out.append(
            f"| `{f['query'] or '—'}` | `{f['fmt'] or '—'}` | {f['label']} | "
            f"{f['nature']} | {f['caused']} | {f['source']} |"
        )
    out += [
        "",
        "_Nature: **Transient** = auto-retried / self-heals; **Terminating** = "
        "attempt-fatal (may recover on retry); **Blocker** = needs quota / capacity / "
        'config fix. "Caused query failure?" = **Yes** if the query had no '
        "successful attempt, **No** if a retry or another attempt still COMPLETED, "
        "**—** if not attributable to a single query._",
    ]
    return out


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify TPC-DS run metrics.")
    parser.add_argument(
        "--metrics-file",
        default="",
        help=(
            "Path to metrics JSON. Optional: if omitted, auto-discovers the "
            "newest tpcds_submission_metrics_sf*.json in common results dirs."
        ),
    )
    parser.add_argument("--query-ids", required=True)
    parser.add_argument("--formats", required=True)
    parser.add_argument("--iterations", required=True, type=int)
    parser.add_argument("--min-success-rate", required=True, type=float)
    parser.add_argument("--thresholds", default="")
    parser.add_argument(
        "--duration-threshold-grace-pct",
        default=100.0,
        type=float,
        help=(
            "Extra percentage margin applied to each duration threshold before "
            "failing (default: 100.0)."
        ),
    )
    parser.add_argument(
        "--kube-config",
        default=os.environ.get("KUBECONFIG_PATH", ""),
        help=(
            "Kubeconfig for the run's AKS cluster. Used to pull live Spark "
            "execution failures / OOM / timeouts (SparkApplication states, pod "
            "exit codes, Warning events) into the report. Defaults to "
            "$KUBECONFIG_PATH; best-effort — skipped if unreachable."
        ),
    )
    args = parser.parse_args()

    preferred_scale = _infer_scale_from_thresholds_arg(args.thresholds)
    metrics_path = (
        Path(args.metrics_file)
        if args.metrics_file
        else _discover_metrics_file(preferred_scale)
    )
    if metrics_path is None:
        print(
            "[error] Metrics file not found. Pass --metrics-file explicitly "
            "or run submit_tpcds_job.py first."
        )
        return 2
    if not metrics_path.exists():
        print(f"[error] Metrics file not found: {metrics_path}")
        return 2

    with open(metrics_path, encoding="utf-8") as f:
        metrics = json.load(f)

    run_parallel = _run_parallel(metrics)

    query_ids = _parse_tokens(args.query_ids, sep=" ")
    formats = _parse_tokens(args.formats, sep=",")
    results = metrics.get("results") or []
    scale_factor = extract_scale_factor(metrics, results, metrics_path)

    expected = {
        (qid, fmt, itr)
        for qid in query_ids
        for fmt in formats
        for itr in range(1, args.iterations + 1)
    }

    threshold_index: dict[tuple[int, str, str], dict] = {}
    thresholds_path = _resolve_thresholds_path(args.thresholds, scale_factor)
    if thresholds_path is not None and thresholds_path.exists():
        threshold_index = _load_threshold_index(thresholds_path)

    threshold_missing_keys: set[tuple[str, str]] = set()
    threshold_non_blocking_keys: set[tuple[str, str]] = set()
    duration_limits: dict[tuple[str, str], float] = {}
    gated_expected: set[tuple[str, str, int]] = set()

    for qid, fmt, itr in expected:
        entry = None
        if scale_factor is not None:
            entry = threshold_index.get((scale_factor, qid, fmt))

        if entry is None:
            gated_expected.add((qid, fmt, itr))
            if threshold_index:
                threshold_missing_keys.add((qid, fmt))
            continue

        if entry.get("allow_failure"):
            threshold_non_blocking_keys.add((qid, fmt))
            continue

        gated_expected.add((qid, fmt, itr))
        baseline_p95 = _select_baseline_p95(entry, run_parallel)
        if baseline_p95 is not None:
            duration_limits[(qid, fmt)] = baseline_p95

    present: set[tuple[str, str, int]] = set()
    scored_targeted = 0
    scored_completed_targeted = 0
    durations_by_key: dict[tuple[str, str], list[float]] = defaultdict(list)
    per_cell_totals: dict[tuple[str, str], int] = defaultdict(int)
    per_cell_completed: dict[tuple[str, str], int] = defaultdict(int)
    per_cell_states: dict[tuple[str, str], Counter] = defaultdict(Counter)

    for row in results:
        qid = _result_query_id(row)
        fmt = str(row.get("data_format") or "")
        itr = _result_iteration(row)
        if qid and fmt and itr is not None:
            present.add((qid, fmt, itr))

        if (qid, fmt, itr) not in expected:
            continue
        if row.get("expected_failure", False):
            continue
        if (qid, fmt, itr) not in gated_expected:
            continue
        scored_targeted += 1
        per_cell_totals[(qid, fmt)] += 1
        state = str(row.get("state") or "UNKNOWN")
        per_cell_states[(qid, fmt)][state] += 1
        if state == "COMPLETED":
            scored_completed_targeted += 1
            per_cell_completed[(qid, fmt)] += 1
            duration = _result_duration_seconds(row)
            if duration is not None:
                durations_by_key[(qid, fmt)].append(duration)

    missing = sorted(gated_expected - present)
    # Score by logical query/format cell, not per attempt: a cell passes if at
    # least one attempt COMPLETED. A query that failed once (e.g. transient
    # driver/executor placement churn under parallel load) but succeeded on a
    # rerun is therefore counted as a pass. "Queries failed" = cells with no
    # successful attempt at all.
    targeted_cells = set(per_cell_totals.keys())
    failed_cells = sorted(
        cell for cell in targeted_cells if per_cell_completed.get(cell, 0) == 0
    )
    cells_total = len(targeted_cells)
    cells_passed = cells_total - len(failed_cells)
    success_rate = (100.0 * cells_passed / cells_total) if cells_total else 100.0

    duration_failures = []
    for key, limit in sorted(duration_limits.items()):
        observed = rank_percentile(
            exclude_duration_outliers(durations_by_key.get(key) or []),
            95,
        )
        if observed is None:
            continue
        effective_limit = limit * (1.0 + (args.duration_threshold_grace_pct / 100.0))
        if observed > effective_limit:
            duration_failures.append((key[0], key[1], observed, limit, effective_limit))

    passed = (
        (not missing)
        and (success_rate >= args.min_success_rate)
        and (not duration_failures)
    )

    verdict = "PASS" if passed else "FAIL"
    lines = [
        f"## TPC-DS Verification - {verdict}",
        "",
        f"- Metrics file: `{metrics_path}`",
        f"- Expected rows: {len(expected)}",
        f"- Gated rows: {len(gated_expected)}",
        f"- Present unique rows: {len(present)}",
        (
            f"- Queries failed (no successful attempt): "
            f"**{len(failed_cells)}** of {cells_total}"
        ),
        (
            f"- Success rate (queries with a successful attempt): "
            f"{cells_passed}/{cells_total} = "
            f"**{success_rate:.2f}%** (threshold {args.min_success_rate:.2f}%)"
        ),
        (
            f"- Attempts completed (informational): "
            f"{scored_completed_targeted}/{scored_targeted}"
        ),
        (f"- Duration threshold grace: {args.duration_threshold_grace_pct:.2f}%"),
    ]

    if thresholds_path is not None:
        if thresholds_path.exists():
            lines.append(f"- Thresholds file: `{thresholds_path}`")
            if scale_factor is not None:
                lines.append(f"- Scale factor: `{scale_factor}`")
            if threshold_non_blocking_keys:
                lines.append(
                    "- Non-blocking query/format pairs: "
                    + ", ".join(
                        f"`{qid}/{fmt}`"
                        for qid, fmt in sorted(threshold_non_blocking_keys)
                    )
                )
            if threshold_missing_keys:
                lines.append(
                    "- Missing thresholds (strict fallback): "
                    + ", ".join(
                        f"`{qid}/{fmt}`" for qid, fmt in sorted(threshold_missing_keys)
                    )
                )
        else:
            lines.append(f"- Thresholds file: `{thresholds_path}` (not found; ignored)")

    if failed_cells:
        lines.extend(["", "### Failed queries (no successful attempt)"])
        lines.extend([f"- `{qid}` / `{fmt}`" for qid, fmt in failed_cells])

    if missing:
        lines.extend(["", "### Missing (query, format, iteration)"])
        lines.extend([f"- `{qid}` / `{fmt}` / `{itr}`" for qid, fmt, itr in missing])

    if duration_failures:
        lines.extend(["", "### Duration Threshold Failures"])
        lines.extend(
            [
                (
                    f"- `{qid}` / `{fmt}` observed p95 `{observed:.2f}s` "
                    f"exceeds effective `{effective_limit:.2f}s` "
                    f"(base `{limit:.2f}s` + {args.duration_threshold_grace_pct:.2f}%)"
                )
                for qid, fmt, observed, limit, effective_limit in duration_failures
            ]
        )

    lines.extend(
        _render_per_query_table(
            query_ids=query_ids,
            formats=formats,
            per_cell_totals=per_cell_totals,
            per_cell_completed=per_cell_completed,
            per_cell_states=per_cell_states,
            durations_by_key=durations_by_key,
            duration_limits=duration_limits,
            threshold_non_blocking_keys=threshold_non_blocking_keys,
            grace_pct=args.duration_threshold_grace_pct,
        )
    )

    # Bottom-of-report: Spark execution failures / OOM / timeouts, pulled from the
    # run metrics and (best-effort) the live cluster via the kubeconfig.
    lines.extend(
        _render_execution_findings(
            _collect_execution_findings(results, failed_cells, args.kube_config)
        )
    )

    summary_block = "\n".join(lines) + "\n"
    print(summary_block)
    _append_summary(summary_block)

    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
