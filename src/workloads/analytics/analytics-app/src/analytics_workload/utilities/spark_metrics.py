import json
import os
import urllib.request
from datetime import datetime

# Spark SQL plan helpers for dump_spark_metrics: leaf-scan input bytes from
# the executed plan rather than summing per-stage inputBytes (over-counts).

_SIZE_UNIT_FACTORS = {
    "b": 1,
    "bytes": 1,
    "k": 1024,
    "kb": 1024,
    "kib": 1024,
    "m": 1024**2,
    "mb": 1024**2,
    "mib": 1024**2,
    "g": 1024**3,
    "gb": 1024**3,
    "gib": 1024**3,
    "t": 1024**4,
    "tb": 1024**4,
    "tib": 1024**4,
}


def _parse_spark_size(raw) -> int:
    """Parse a Spark SQL metric size value, e.g. '1.5 GiB', '1024 B', '1,234'."""
    parts = str(raw or "").replace(",", "").split()
    if not parts:
        return 0
    try:
        val = float(parts[0])
    except ValueError:
        return 0
    factor = _SIZE_UNIT_FACTORS.get(parts[1].lower(), 0) if len(parts) > 1 else 1
    return int(val * factor)


def _parse_spark_count(raw) -> int:
    """Parse a Spark SQL metric count value -- handles '287,999,764' or '... total (... distinct)'."""
    head = str(raw or "").replace(",", "").split()
    if not head:
        return 0
    try:
        return int(float(head[0]))
    except ValueError:
        return 0


def _is_scan_node(node_name: str) -> bool:
    n = (node_name or "").lower()
    return n.startswith("scan ") or any(
        s in n for s in ("filescan", "filesourcescan", "batchscan")
    )


def _collect_leaf_scan_metrics(api_get, sql_execs):
    """Aggregate leaf-scan (FileSourceScan) metrics across completed SQL
    executions. Returns ``None`` if none completed, else a totals dict with
    ``bytes_read``/``rows_read``/``files_read`` and a per-scan ``scans`` list.
    """
    completed = [e for e in sql_execs if e.get("status") == "COMPLETED"]
    if not completed:
        return None

    totals = {"bytes_read": 0, "rows_read": 0, "files_read": 0, "scans": []}
    seen: set[tuple] = set()
    for ex in completed:
        detail = api_get(f"/sql/{ex['id']}")
        if not detail:
            continue
        for node in detail.get("nodes", []):
            name = node.get("nodeName", "")
            if not _is_scan_node(name):
                continue
            m = {x.get("name"): x.get("value") for x in node.get("metrics", [])}
            location = (
                m.get("files location") or m.get("Location") or m.get("location") or ""
            )
            key = (
                ("loc", str(location))
                if location
                else ("node", str(ex.get("id")), node.get("nodeId"))
            )
            if key in seen:
                continue
            seen.add(key)
            n_bytes = _parse_spark_size(m.get("size of files read"))
            n_rows = _parse_spark_count(m.get("number of output rows"))
            n_files = _parse_spark_count(m.get("number of files read"))
            totals["bytes_read"] += n_bytes
            totals["rows_read"] += n_rows
            totals["files_read"] += n_files
            totals["scans"].append(
                {
                    "exec": ex.get("id"),
                    "node": name,
                    "bytes": n_bytes,
                    "rows": n_rows,
                    "files": n_files,
                    "location": str(location)[:120],
                }
            )
    return totals


def dump_spark_metrics(spark, job_id, logger):
    """Capture final Spark metrics via OTLP gauges before the driver exits."""
    try:
        ui_url = spark.sparkContext.uiWebUrl
        app_id = spark.sparkContext.applicationId
        if not ui_url:
            logger.warning("Spark UI URL not available, skipping metrics dump.")
            return
        api = f"{ui_url}/api/v1/applications/{app_id}"

        def get(path, timeout=10):
            try:
                with urllib.request.urlopen(f"{api}{path}", timeout=timeout) as r:
                    return json.loads(r.read().decode())
            except (OSError, ValueError) as e:
                logger.warning(f"Spark REST {path} failed: {e}")
                return None

        stages_all = get("/stages") or []
        all_execs = get("/allexecutors") or []
        execs = [e for e in all_execs if e.get("id") != "driver"]
        jobs = get("/jobs") or []
        sql_execs = get("/sql") or []

        # Keep only the latest COMPLETE attempt per stageId (avoid retry inflation).
        latest_by_id: dict[int, dict] = {}
        for s in stages_all:
            if (s.get("status") or "").upper() != "COMPLETE":
                continue
            sid = s.get("stageId")
            if sid is None:
                continue
            cur = latest_by_id.get(sid)
            if cur is None or (s.get("attemptId", 0) or 0) > (
                cur.get("attemptId", 0) or 0
            ):
                latest_by_id[sid] = s
        stages = list(latest_by_id.values())

        try:
            from opentelemetry import metrics as otel_metrics
        except ImportError as e:
            logger.warning(f"OpenTelemetry not available, skipping export: {e}")
            return
        meter = otel_metrics.get_meter("spark-final-metrics")
        attrs = {
            "job_id": job_id,
            "query_id": os.getenv("QUERY_ID", "unknown"),
        }

        def emit(name, value, unit="1"):
            if value is None:
                return
            meter.create_gauge(f"spark.final.{name}", unit=unit).set(value, attrs)

        def stotal(key):
            return sum(s.get(key, 0) for s in stages)

        def etotal(key):
            return sum(e.get(key, 0) for e in execs)

        # input_bytes / records_read from leaf FileSourceScan nodes; stage-sum
        # over-counts CTE re-scans and AQE retries. Both logged for comparison.
        leaf = _collect_leaf_scan_metrics(get, sql_execs)
        stage_input_bytes = stotal("inputBytes")
        stage_input_records = stotal("inputRecords")
        if leaf is not None and leaf["scans"]:
            ratio = stage_input_bytes / leaf["bytes_read"] if leaf["bytes_read"] else 0
            logger.info(
                f"input_bytes leaf-scan={leaf['bytes_read']:,} "
                f"stage-sum={stage_input_bytes:,} (ratio={ratio:.1f}x); "
                f"using leaf-scan as authoritative."
            )
            for s in leaf["scans"]:
                logger.info(
                    f"  scan exec={s['exec']} bytes={s['bytes']:,} "
                    f"rows={s['rows']:,} files={s['files']} :: {s['node']}"
                )
            emit("input_bytes", leaf["bytes_read"], "By")
            emit("records_read", leaf["rows_read"], "1")
            emit("files_read", leaf["files_read"], "1")
        else:
            logger.info(
                f"No SQL execution plan available; falling back to stage-sum "
                f"input_bytes={stage_input_bytes:,} input_records={stage_input_records:,}."
            )
            emit("input_bytes", stage_input_bytes, "By")
            emit("records_read", stage_input_records, "1")

        # I/O counters from stages (input_bytes / records_read handled above).
        for name, key, unit in [
            ("output_bytes", "outputBytes", "By"),
            ("shuffle_read_bytes", "shuffleReadBytes", "By"),
            ("shuffle_write_bytes", "shuffleWriteBytes", "By"),
            ("shuffle_read_records", "shuffleReadRecords", "1"),
            ("shuffle_write_records", "shuffleWriteRecords", "1"),
            ("total_disk_bytes_spilled", "diskBytesSpilled", "By"),
            ("total_memory_bytes_spilled", "memoryBytesSpilled", "By"),
            ("total_tasks", "numCompleteTasks", "1"),
        ]:
            emit(name, stotal(key), unit)

        # Failed tasks span all stage attempts (incl. aborted/failed stages that
        # never produced a COMPLETE attempt), not just the COMPLETE attempts
        # summed by stotal().
        emit(
            "total_tasks_failed",
            sum(s.get("numFailedTasks", 0) for s in stages_all),
            "1",
        )

        # Executor counters.
        emit("gc_time_ms", etotal("totalGCTime"), "ms")
        # total_stages = unique successful stages. total_stage_attempts counts
        # only attempts that actually ran (COMPLETE/FAILED); SKIPPED stages (AQE/
        # shuffle reuse) are excluded so the derived stage-retry rate reflects
        ran_attempts = sum(
            1
            for s in stages_all
            if (s.get("status") or "").upper() in ("COMPLETE", "FAILED")
        )
        emit("total_stages", len(stages))
        emit("total_stage_attempts", ran_attempts)
        emit("total_jobs", len(jobs))
        emit("records_written", stotal("outputRecords"))

        # Failure signal: any FAILED job or non-COMPLETE SQL exec. status 0=OK/1=FAIL.
        jobs_failed = sum(
            1 for j in jobs if (j.get("status") or "").upper() == "FAILED"
        )
        sql_failed = sum(
            1
            for e in sql_execs
            if (e.get("status") or "").upper() not in ("COMPLETED", "RUNNING")
        )
        emit("jobs_failed", jobs_failed)
        emit("sql_executions_failed", sql_failed)
        emit("status", 1 if (jobs_failed or sql_failed) else 0)

        # wall_clock = stage-timeline span; sql_execution = sum of /sql durations;
        # stage_queue_wait = per-stage wait from submission to first task launch.
        def _iso_to_ms(s):
            if not s:
                return None
            try:
                return (
                    datetime.strptime(
                        s.replace("GMT", "+0000"),
                        "%Y-%m-%dT%H:%M:%S.%f%z",
                    ).timestamp()
                    * 1000
                )
            except ValueError:
                return None

        sub_times = [_iso_to_ms(s.get("submissionTime")) for s in stages]
        comp_times = [_iso_to_ms(s.get("completionTime")) for s in stages]
        first_times = [_iso_to_ms(s.get("firstTaskLaunchedTime")) for s in stages]
        submitted = [t for t in sub_times if t is not None]
        completed = [t for t in comp_times if t is not None]
        wall_ms = int(max(completed) - min(submitted)) if submitted and completed else 0
        if wall_ms:
            emit("wall_clock_duration_ms", wall_ms, "ms")

        sql_dur_total_ms = sum(
            int(ex["duration"])
            for ex in sql_execs
            if (ex.get("status") or "").upper() == "COMPLETED"
            and isinstance(ex.get("duration"), (int, float))
            and ex["duration"] > 0
        )
        if sql_dur_total_ms:
            emit("sql_execution_duration_ms", sql_dur_total_ms, "ms")

        per_stage_delays_ms: list[float] = []
        for sub, first in zip(sub_times, first_times):
            if sub is not None and first is not None and first > sub:
                per_stage_delays_ms.append(first - sub)
        if per_stage_delays_ms:
            emit("stage_queue_wait_max_ms", int(max(per_stage_delays_ms)), "ms")
            emit(
                "stage_queue_wait_mean_ms",
                int(sum(per_stage_delays_ms) / len(per_stage_delays_ms)),
                "ms",
            )

        # Peaks: executor/unified = MAX across executors.
        peak_jvm_per_exec = [
            e.get("peakMemoryMetrics", {}).get("JVMHeapMemory", 0) for e in execs
        ]
        emit(
            "peak_executor_memory_bytes",
            max(peak_jvm_per_exec, default=0),
            "By",
        )
        emit(
            "peak_unified_memory_bytes",
            max(
                (
                    e.get("peakMemoryMetrics", {}).get("OnHeapUnifiedMemory", 0)
                    for e in execs
                ),
                default=0,
            ),
            "By",
        )

        # Driver peak JVM heap (driver OOM is the dominant driver failure mode).
        driver_entry = next((e for e in all_execs if e.get("id") == "driver"), {})
        emit(
            "peak_driver_memory_bytes",
            driver_entry.get("peakMemoryMetrics", {}).get("JVMHeapMemory", 0),
            "By",
        )

        # CPU: task_cpu_efficiency = cpu/run time; cluster_cpu_utilization =
        # cpu / (executors_used * executor_cores * wall_clock).
        cpu_ns = stotal("executorCpuTime")
        run_ms = stotal("executorRunTime")
        emit("executor_cpu_time_ms", round(cpu_ns / 1e6, 1) if cpu_ns else 0.0, "ms")
        emit("executor_run_time_ms", run_ms, "ms")
        emit(
            "task_cpu_efficiency_pct",
            round(cpu_ns / 1e6 / run_ms * 100, 1) if run_ms > 0 else 0.0,
        )
        used = sum(1 for e in execs if e.get("totalTasks", 0) > 0)
        try:
            exec_cores = int(spark.conf.get("spark.executor.cores", "1"))
        except Exception:
            exec_cores = 1
        cluster_core_ms = used * exec_cores * wall_ms
        if cluster_core_ms > 0:
            emit(
                "cluster_cpu_utilization_pct",
                round(cpu_ns / 1e6 / cluster_core_ms * 100, 1),
            )

        # Executor GC % of run time = total executor GC time / total executor
        # run time. Driver is excluded (execs filters id != "driver").
        gc_ms = etotal("totalGCTime")
        emit(
            "executor_gc_pct_of_run_time",
            round(gc_ms / run_ms * 100, 1) if run_ms > 0 else 0.0,
        )

        # Executors that actually ran tasks.
        emit("executors_used", used)

        try:
            from opentelemetry.sdk.metrics import MeterProvider

            provider = otel_metrics.get_meter_provider()
            if isinstance(provider, MeterProvider):
                # shutdown() blocks until the exporter has fully drained,
                # unlike force_flush() which can return before the async
                # remote-write queue is emptied, dropping the final gauges.
                provider.shutdown()
        except Exception:
            pass
        logger.info("Spark final metrics exported via OTLP.")
    except Exception as e:
        logger.warning(f"Spark metrics dump failed: {e}")
