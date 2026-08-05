#!/bin/bash
set -e

source /data/.venv/bin/activate
mkdir -p /data/publisher/parquet /data/consumer/parquet

PUB_PQ_COUNT=$(ls /data/publisher/parquet/*.parquet 2>/dev/null | wc -l || true)
CON_PQ_COUNT=$(ls /data/consumer/parquet/*.parquet 2>/dev/null | wc -l || true)
if [ "$PUB_PQ_COUNT" -ge 12 ] && [ "$CON_PQ_COUNT" -ge 12 ]; then
    echo "Parquet files already exist (pub=$PUB_PQ_COUNT, con=$CON_PQ_COUNT). Skipping."
    exit 0
fi

echo "=== Converting CSV to Parquet (sequential - safer for large files) ==="
python3 /tmp/convert_to_parquet.py \
    --publisher-csv-dir /data/publisher/csv \
    --consumer-csv-dir /data/consumer/csv \
    --publisher-parquet-dir /data/publisher/parquet \
    --consumer-parquet-dir /data/consumer/parquet \
    --schema-sql /data/DSGen-software-code-4.0.0/tools/tpcds.sql \
    --separator "|"

echo "Parquet conversion complete."
du -sh /data/publisher/parquet/ /data/consumer/parquet/
df -h /data
