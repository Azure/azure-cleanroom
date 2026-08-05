#!/bin/bash
set -e

: "${SCALE_FACTOR:?SCALE_FACTOR is required}"
: "${PARALLEL_WORKERS:?PARALLEL_WORKERS is required}"
: "${PUBLISHER_TABLES:?PUBLISHER_TABLES is required}"
: "${CONSUMER_TABLES:?CONSUMER_TABLES is required}"

read -ra PUBLISHER_TABLES_ARR <<< "$PUBLISHER_TABLES"
read -ra CONSUMER_TABLES_ARR <<< "$CONSUMER_TABLES"
ALL_TABLES=("${PUBLISHER_TABLES_ARR[@]}" "${CONSUMER_TABLES_ARR[@]}")

PUB_COUNT=$(ls /data/publisher/csv/*.csv 2>/dev/null | wc -l || true)
CON_COUNT=$(ls /data/consumer/csv/*.csv 2>/dev/null | wc -l || true)
if [ "$PUB_COUNT" -ge 12 ] && [ "$CON_COUNT" -ge 12 ]; then
    echo "Partitioned CSV already exists (pub=$PUB_COUNT, con=$CON_COUNT). Skipping dsdgen."
    exit 0
fi

RAW_CSV_COUNT=$(ls /data/raw/*.csv 2>/dev/null | wc -l || true)
if [ "$RAW_CSV_COUNT" -ge "${#ALL_TABLES[@]}" ]; then
    echo "Raw CSV already exists ($RAW_CSV_COUNT files). Skipping dsdgen."
    du -sh /data/raw/
    exit 0
fi

if [ -d /data/raw ]; then
    DAT_COUNT=$(find /data/raw -name '*.dat' 2>/dev/null | wc -l || true)
    if [ "$DAT_COUNT" -gt 0 ]; then
        echo "=== Cleaning up $DAT_COUNT stale .dat files ==="
        rm -f /data/raw/*.dat
    fi
fi

echo "=== Running dsdgen with SCALE=$SCALE_FACTOR ($PARALLEL_WORKERS workers) ==="
mkdir -p /data/raw
cd /data/DSGen-software-code-4.0.0/tools

echo "Disk space before dsdgen:"
df -h /data
pids=()
for child in $(seq 1 "$PARALLEL_WORKERS"); do
    ./dsdgen -DIR /data/raw -SCALE "$SCALE_FACTOR" \
        -PARALLEL "$PARALLEL_WORKERS" -CHILD "$child" -FORCE Y -TERMINATE N &
    pids+=($!)
    echo "  Started dsdgen child $child (PID ${pids[-1]})"
done

echo "  Waiting for $PARALLEL_WORKERS dsdgen workers..."
dsdgen_failed=0
for pid in "${pids[@]}"; do
    if ! wait "$pid"; then
        echo "ERROR: dsdgen child PID $pid failed."
        dsdgen_failed=1
    fi
done
[ $dsdgen_failed -eq 1 ] && exit 1

echo "dsdgen complete."
ls /data/raw/ | head -30
df -h /data
