# TPC-DS Cleanroom Analytics — Observations & Tuning Notes

Findings from running the TPC-DS stress-test suite (known failure modes, spill
behaviour, and shuffle-partition tuning). For setup and how to run the harness,
see [`README.md`](README.md).

## Dataset I/O Profile (shuffle partitions = 200)

Per-query I/O totals captured from Spark metrics for each scale factor. **Rows
Read (M)** is the records scanned from input tables (millions); **Rows
Generated** is the raw count of records written to the output sink
(format-independent). **Input GB** and **Output** are the bytes read/written for
each storage format (CSV vs parquet). **Tables** is the number of input datasets
loaded for the query.

### SF10

| Query | Tables | Rows Read (M) | Input GB (CSV) | Input GB (parquet) | Output (CSV) | Output (parquet) | Rows Generated |
|-------|--------|---------------|----------------|--------------------|--------------|------------------|----------------|
| `query1` | 4 | 3.45 | 0.41 | 0.20 | 1.3 MB | 365.4 KB | 74,087 |
| `query14` | 6 | 50.57 | 8.52 | 3.27 | 1.3 MB | 340.4 KB | 37,089 |
| `query24` | 6 | 32.53 | 4.43 | 1.68 | 68 B | 1.4 KB | 1 |
| `query64` | 13 | 50.36 | 7.75 | 3.03 | 2.9 KB | 8.6 KB | 20 |
| `query72` | 9 | 151.05 | 5.92 | 1.82 | 5.6 MB | 1.7 MB | 42,891 |

### SF100

| Query | Tables | Rows Read (M) | Input GB (CSV) | Input GB (parquet) | Output (CSV) | Output (parquet) | Rows Generated |
|-------|--------|---------------|----------------|--------------------|--------------|------------------|----------------|
| `query1` | 4 | 30.87 | 3.71 | 1.91 | 3.6 MB | 1.1 MB | 210,105 |
| `query14` | 6 | 1,813.42 | 87.04 | 33.63 | 2.0 MB | 536.3 KB | 54,202 |
| `query24` | 6 | 320.00 | 44.57 | 17.30 | 353 B | 1.7 KB | 13 |
| `query64` | 13 | 480.39 | 77.83 | 30.82 | 42.9 KB | 27.1 KB | 311 |
| `query72` | 9 | 559.94 | 41.49 | 15.07 | 78.9 MB | 9.6 MB | 604,624 |

### SF400

| Query | Tables | Rows Read (M) | Input GB (CSV) | Input GB (parquet) | Output (CSV) | Output (parquet) | Rows Generated |
|-------|--------|---------------|----------------|--------------------|--------------|------------------|----------------|
| `query1` | 4 | 116.37 | 13.79 | 6.94 | 14 B | 326 B | — |
| `query14` | 6 | 2,110.10 | 342.33 | 128.85 | 701.6 KB | 173.4 KB | 17,143 |
| `query24` | 6 | 1,268.85 | 173.84 | 65.04 | 700 B | 2.0 KB | 26 |
| `query64` | 13 | 1,904.44 | 304.49 | 117.88 | 53.6 MB | 978.8 KB | 404,228 |
| `query72` | 9 | 649.97 | 130.94 | 52.89 | 28.1 MB | 5.8 MB | 218,593 |

### SF700

| Query | Tables | Rows Read (M) | Input GB (CSV) | Input GB (parquet) | Output (CSV) | Output (parquet) | Rows Generated |
|-------|--------|---------------|----------------|--------------------|--------------|------------------|----------------|
| `query1` | 4 | 205.77 | 24.83 | 12.26 | 43.4 MB | 11.4 MB | 2,550,585 |
| `query14` | 6 | 3,528.09 | 609.05 | 226.78 | 913.6 KB | 241.1 KB | 22,276 |
| `query24` | 6 | 2,223.75 | 309.92 | 114.52 | 1.9 KB | 3.1 KB | 74 |
| `query64` | 13 | 3,334.54 | 542.04 | 207.41 | 31.4 MB | 1.2 MB | 234,372 |
| `query72` | 9 | 1,140.59 | 232.71 | 92.99 | 59.4 MB | 11.4 MB | 456,746 |

### SF1000

| Query | Tables | Rows Read (M) | Input GB (CSV) | Input GB (parquet) | Output (CSV) | Output (parquet) | Rows Generated |
|-------|--------|---------------|----------------|--------------------|--------------|------------------|----------------|
| `query1` | 4 | 300.07 | 36.96 | 18.82 | 29.4 MB | 8.7 MB | 1,731,642 |
| `query14` | 6 | 5,040.34 | 889.58 | 340.84 | 2.7 MB | 736.3 KB | 66,373 |
| `query24` | 6 | 3,186.29 | 453.46 | 174.02 | 4.3 KB | 5.1 KB | 172 |
| `query64` | 13 | 4,772.26 | 792.95 | 312.43 | 965.7 KB | 293.1 KB | 6,982 |
| `query72` | 9 | 2,369.28 | 355.91 | 142.85 | 741.3 MB | 50.4 MB | 5,670,928 |

> CSV inputs are ~2.5× larger than parquet for the same data (uncompressed text
> vs. columnar compression), so CSV runs scan more bytes for identical row
> counts. `query24`/`query64` are analytical filters that emit only a handful of
> summary rows. `query1` at SF400 returns an empty result set (0 rows, header
> file only) — confirmed across both the 200- and 2000-partition runs — so it is
> shown as `—`.

## Known Bugs / Limitations

**Where the resource values live:** 
driver/executor cores, memory, and
executor instance counts are configured in
[`spark-frontend/values.app.yaml`](../../../../../src/cleanroom-cluster/cleanroom-cluster-provider-client/spark-frontend/values.app.yaml)

| Symptom | Exit code | Likely cause | Notes / workaround |
|---------|-----------|--------------|--------------------|
| **Driver OOM-killed** | `137` | Container exceeded its cgroup memory limit and was `SIGKILL`'d. Common on scan/shuffle-heavy queries at higher scale. | Raise `sql.driver.memory` in [`values.app.yaml`](../../../../../src/cleanroom-cluster/cleanroom-cluster-provider-client/spark-frontend/values.app.yaml)|
| **Driver abort (executor failure cascade)** | `11` | The driver exits `11` when executors keep crashing under memory pressure: once the number of executor failures reaches `spark.executor.maxNumFailures` (default `numExecutors * 2, with minimum of 3` on Kubernetes), Spark fails the application. So driver `11` is usually a *symptom* of the executor OOM below, not an independent driver fault. | Fix the executor memory pressure (see Executor OOM row); raise `spark.executor.maxNumFailures` only to tolerate more transient losses, not as a fix. |
| **Executor OOM** | `52` | Spark-specific exit code `SparkExitCode.OOM`: "the default uncaught exception handler was reached, and the uncaught exception was an `OutOfMemoryError`" — the executor JVM's heap/GC-overhead was exhausted, often when spill can't keep up. | Raise `sql.executor.memory`, add executors, or lower per-task data volume (more / smaller shuffle partitions). |
| **Front-end pod restart (CrashLoopBackOff)** | `137` | The `cleanroom-spark-frontend` pod is OOM-killed and enters `CrashLoopBackOff`, restarting ~every 40 min. It runs with **no memory limits** on Virtual Kubelet (CACI); when the OTLP log exporter gets `RESOURCE_EXHAUSTED` back from the `otel-collector`, it buffers log records **unboundedly** in memory until the pod is OOM-killed. Spark jobs themselves still complete, but status polling returns intermittent `503`/`500` for the ~30–90 s restart window, so at high concurrency the runner misreports job states and accumulates poll errors. | Job may need re-submission; check pod `RESTARTS` count. Mitigate by capping the OTLP exporter queue / addressing the `otel-collector` back-pressure. |
| **Ephemeral storage exhaustion — `No space left on device` (C-ACI)** | — | On Kubernetes, Spark spills to "temporary scratch space" on an `emptyDir` volume mounted for each `spark.local.dir` directory (`/tmp/spark-*` by default), which uses the executor's ephemeral storage. CACI caps this at **~50 GB per executor**. When Spark's `BlockManager` evicts cached/shuffle blocks to that scratch space, large **disk spill** overflows it — either the executor is evicted for exceeding `ephemeral-storage`, or it crashes mid-stage with `java.io.IOException: No space left on device`. Wide queries (multi-join + group-by, e.g. `query14a`, `query64`) trigger this at **SF1000 (parquet)**. | Add more executors so each holds less data. |
| **`spark-submit` timeout under concurrency** | — | Job submission fails *before the driver starts*: `spark-submit` errors with `io.fabric8.kubernetes.client.KubernetesClientException: The timeout period of 10000ms has been exceeded while executing POST /api/v1/namespaces/analytics/pods` (root cause `io.vertx.core.impl.NoStackTraceTimeoutException`), sometimes preceded by `ERROR Client: Please check "kubectl auth can-i create pod" first`. The fabric8 K8s client's default 10 s request timeout is exceeded when the API server / Virtual Kubelet is slow to admit the driver pod under load. **Intermittent at 5 concurrent jobs, consistent at 10 concurrent** (SF1000). | Reduce submission concurrency or stagger submissions. |

### Memory spill vs. disk spill

Spark processes shuffles, joins, aggregations and sorts in each executor's
**execution memory**. When a task's working set no longer fits there, Spark
*spills* to make room — this is normal back-pressure, not a crash, but it is
the usual precursor to the failures above:

- **Memory spill** (`memoryBytesSpilled`) — Spark defines this as "the number
  of in-memory bytes spilled by this task": the in-memory size of records
  evicted from execution memory to relieve pressure. High memory spill means
  tasks are churning data through the heap; it drives up GC and, if it can't
  keep up, ends in an executor **OOM (exit 52)**.
- **Disk spill** (`diskBytesSpilled`) — Spark defines this as "the number of
  on-disk bytes spilled by this task": the on-disk size of those spilled
  records after they are written to the executor's local scratch directory
  (`spark.local.dir` on the `emptyDir` volume). This is what consumes
  **ephemeral storage**; on C-ACI it counts against the **~50 GB per executor**
  cap, so heavy disk spill on any single executor is what triggers its eviction
  (or a `No space left on device` crash). Add more executors so each holds
  less.

## Shuffle partitions: 200 vs 2000

The suite was run end-to-end with `spark.sql.shuffle.partitions` set to **200**
and to **2000**, across **5 queries × 5 scale factors (sf10 → sf1000) × 2
formats (csv, parquet)**. Numbers below are parquet unless noted; a **positive
delta means 2000 is worse** (slower / more spill / more GC).

**Headline:** raising the partition count from 200 → 2000 **did not help** — it
was neutral-to-worse almost everywhere. Spark's Adaptive Query Execution (AQE)
already coalesces post-shuffle partitions, so the higher ceiling barely changed
task counts (`total_tasks` moves <1% on the heavy queries) but added scheduler
and GC overhead, and for the spill-heavy join it made spill dramatically worse.

### Why the task count barely moves

With AQE enabled, `spark.sql.shuffle.partitions` is an *upper bound*: small
post-shuffle partitions are coalesced back down. For example query14 at sf1000
runs **99,052** tasks at 200 vs **98,568** at 2000 — a 0.5% difference. So the
"2000" setting is rarely materialized as extra parallelism; you mostly pay its
overhead (more, shorter tasks → more GC; more concurrent tasks per executor →
more spill fragmentation) without a throughput gain.

### Per-query analysis

Each table shows **wall-clock seconds (200 → 2000, Δ%)** per scale factor for
parquet, followed by the notable secondary effects.

#### query1 — light scan/aggregate, no spill

| sf10 | sf100 | sf400 | sf700 | sf1000 |
|------|-------|-------|-------|--------|
| 135 → 201 (+49%) | 217 → 147 (−32%) | 176 → 228 (+30%) | 268 → 278 (+4%) | 395 → 308 (−22%) |

Shuffle is tiny and there is **zero spill** at every scale. 2000 partitions is
pure overhead; the large ± swings are run-to-run noise on a sub-5-minute query.
**200 wins.**

#### query14 — heavy multi-join + group-by (the spill query)

| sf10 | sf100 | sf400 | sf700 | sf1000 |
|------|-------|-------|-------|--------|
| 335 → 362 (+8%) | 501 → 584 (+17%) | 1560 → 1636 (+5%) | 3038 → 3081 (+1%) | 5700 → 6785 (+19%) |

This is where partition count matters most, and **2000 is clearly harmful at
scale**:

- **Disk spill sf1000:** 3.6 GB → **30.4 GB (+747%)** parquet; 3.6 → 36.1 GB csv.
- **Memory spill sf1000:** 570 GB → **1805 GB (+216%)** parquet.
- **GC time sf1000:** +63% parquet / +99% csv.

More partitions means more concurrent tasks contending for the same per-executor
execution memory, so more of the working set spills, driving GC up and wall
clock with it. This is the query most likely to hit the **~50 GB/executor**
`No space left on device` limit — and 2000 pushes it right up against the cap.
**200 wins decisively at high SF.**

#### query24 — medium join/aggregate, GC-sensitive, no spill

| sf10 | sf100 | sf400 | sf700 | sf1000 |
|------|-------|-------|-------|--------|
| 289 → 273 (−6%) | 369 → 290 (−21%) | 544 → 671 (+23%) | 933 → 1343 (+44%) | 1971 → 2148 (+9%) |

No spill, but 2000 explodes GC in the mid-to-high range: **GC +74% at sf400,
+107% at sf700, +99% at sf1000**. The many short tasks created by 2000 churn
the heap with no parallelism payoff. **200 wins from sf400 up.**

#### query64 — join-heavy; stability regression at sf1000

| sf10 | sf100 | sf400 | sf700 | sf1000 |
|------|-------|-------|-------|--------|
| 526 → 508 (−3%) | 606 → 544 (−10%) | 1099 → 1321 (+20%) | 1883 → 1966 (+4%) | 3325 → 3010 (−9%) |

Roughly a wash on time, **but 2000 destabilized it at sf1000**: **34 (parquet) /
37 (csv) failed tasks and 4 stage retries**, where 200 had zero of each. So
2000 traded a small, noisy time change for a reliability regression at the top
scale. GC is also up 50–65% at sf400–sf700. **Prefer 200 for stability.**

#### query72 — light aggregate, but 2000 *introduces* spill

| sf10 | sf100 | sf400 | sf700 | sf1000 |
|------|-------|-------|-------|--------|
| 425 → 527 (+24%) | 470 → 563 (+20%) | 368 → 376 (+2%) | 445 → 464 (+4%) | 908 → 964 (+6%) |

Slower with 2000 across the board, and — notably — 2000 **creates spill that
does not exist at 200**: sf100 0 → 12.5 GB disk / 31 GB memory; sf1000 0 →
36.3 GB disk / 132 GB memory. Fragmenting the aggregation into 2000 partitions
forces spill where 200 fit in memory. **200 wins.**

### Per scale-factor trend

- **sf10 / sf100 (small):** 2000 is almost always slower — the data is tiny, so
  extra partitions are all overhead (near-empty tasks, scheduling, GC).
- **sf400 / sf700 (mid):** mixed on wall clock, but the **GC penalty** from 2000
  becomes pronounced (query24/query64 GC +50–107%).
- **sf1000 (large):** the decisive regime. 2000 makes the spill-bound queries
  (query14, query72) far worse and **destabilizes query64** (task failures +
  stage retries). Only query1/query64 occasionally edge faster, within noise.

### Format note (csv vs parquet)

CSV is consistently slower than parquet (larger scan + shuffle volume) and
spills slightly more — e.g. query14 sf1000 disk spill reaches 36.1 GB on csv vs
30.4 GB on parquet — so csv amplifies the same 2000-partition penalties.

### Recommendation

- **Keep `spark.sql.shuffle.partitions` at 200** for this workload. 2000 gives
  no consistent speedup and hurts the memory/spill-bound cases the most at
  sf1000 — exactly the regime that triggers the OOM / `No space left on device`
  / executor-failure-cascade bugs above.
- To tune a *specific* heavy query (query14), the lever that matters is
  **executor memory and executor count** (to cut per-executor spill under the
  ~50 GB cap), **not** raising the global shuffle-partition count.
