#!/usr/bin/env python3
# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""SparkApplication cleanup helpers for the TPC-DS stress test runner.

Carved out of tpcds_helpers.py so the spark-resource lifecycle code stays
together and the helpers module doesn't double as a kitchen sink.
"""

from __future__ import annotations

import contextlib
import shutil
from datetime import datetime
from pathlib import Path

from tpcds_helpers import get_timestamp


def sweep_stale_spark_snapshots(
    spark_out: Path,
    *,
    archive_keep_last: int = 5,
) -> None:
    """Move any ``spark_metrics_cl-spark-*.json`` files in *spark_out* into
    ``spark_out / _archive / <YYYYMMDDTHHMMSS> /`` so a new run starts with a
    clean directory and prior-run per-job snapshots are preserved.

    After archiving, prune ``_archive/`` to keep only the
    ``archive_keep_last`` most recent generation directories (default 5).
    """
    if not spark_out.exists():
        return
    stale = list(spark_out.glob("spark_metrics_*.json"))
    if stale:
        archive = spark_out / "_archive" / datetime.now().strftime("%Y%m%dT%H%M%S")
        archive.mkdir(parents=True, exist_ok=True)
        for p in stale:
            with contextlib.suppress(OSError):
                shutil.move(str(p), str(archive / p.name))
        print(
            f"{get_timestamp()} [info] Archived {len(stale)} stale "
            f"spark snapshot(s) to {archive}."
        )
    _prune_archive_generations(spark_out / "_archive", archive_keep_last)


def _prune_archive_generations(archive_root: Path, keep_last: int) -> None:
    if not archive_root.exists() or keep_last <= 0:
        return
    generations = sorted(
        (d for d in archive_root.iterdir() if d.is_dir()),
        key=lambda d: d.name,
        reverse=True,
    )
    stale = generations[keep_last:]
    if not stale:
        return
    for gen in stale:
        with contextlib.suppress(OSError):
            shutil.rmtree(gen)
    print(
        f"{get_timestamp()} [warn] Pruned {len(stale)} archived generation(s) "
        f"(kept the {keep_last} most recent in {archive_root})."
    )
