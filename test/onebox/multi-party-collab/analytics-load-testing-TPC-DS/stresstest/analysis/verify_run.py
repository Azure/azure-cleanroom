#!/usr/bin/env python3
"""CI gate for a TPC-DS run metrics JSON.

Checks:
1) Every requested (query x format x iteration) produced at least one row.
2) Success rate over blocking rows is >= --min-success-rate.
3) Optional per-(scale, query, format) thresholds can mark unstable cases as
    non-blocking and apply duration caps to historically stable cases.
"""

from __future__ import annotations

import argparse
import json
import os
import re
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
            elif done < total:
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
        baseline_p95 = entry.get("baseline_p95_seconds")
        if isinstance(baseline_p95, (int, float)):
            duration_limits[(qid, fmt)] = float(baseline_p95)

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
    success_rate = (
        (100.0 * scored_completed_targeted / scored_targeted)
        if scored_targeted
        else 100.0
    )

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
            f"- Success rate (targeted rows): "
            f"{scored_completed_targeted}/{scored_targeted} = "
            f"**{success_rate:.2f}%** (threshold {args.min_success_rate:.2f}%)"
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

    summary_block = "\n".join(lines) + "\n"
    print(summary_block)
    _append_summary(summary_block)

    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
