#!/bin/bash
# dsdgen emits pipe-delimited CSV; Spark CSV reader uses comma. Convert via
# Python's csv module so QUOTE_MINIMAL handles commas embedded in fields.
set -e

: "${PUBLISHER_TABLES:?PUBLISHER_TABLES is required}"
: "${CONSUMER_TABLES:?CONSUMER_TABLES is required}"

read -ra PUBLISHER_TABLES_ARR <<< "$PUBLISHER_TABLES"
read -ra CONSUMER_TABLES_ARR <<< "$CONSUMER_TABLES"

# Idempotent re-run: skip if files are already comma-delimited.
any_pipe_delimited() {
    local f first pipes commas
    for f in "$@"; do
        [ -f "$f" ] || continue
        first=$(head -c 4096 "$f" || true)
        pipes=$(echo "$first" | tr -cd '|' | wc -c)
        commas=$(echo "$first" | tr -cd ',' | wc -c)
        [ "$pipes" -gt "$commas" ] && return 0
    done
    return 1
}
PUB_FILES=("${PUBLISHER_TABLES_ARR[@]/#//data/publisher/csv/}")
CON_FILES=("${CONSUMER_TABLES_ARR[@]/#//data/consumer/csv/}")
if ! any_pipe_delimited "${PUB_FILES[@]/%/.csv}" "${CON_FILES[@]/%/.csv}"; then
    echo "CSV files are already comma-delimited. Skipping conversion."
    exit 0
fi

# Stage the conversion helper.
cat > /tmp/pipe_to_csv.py <<'PYEOF'
import csv
import sys

src = sys.argv[1]
dst = sys.argv[2]

# Stream line-by-line - files can be multi-GB at higher SF.
total = 0
with open(src, "r", encoding="utf-8", newline="") as fin, \
     open(dst, "w", encoding="utf-8", newline="") as fout:
    writer = csv.writer(
        fout,
        delimiter=",",
        quoting=csv.QUOTE_MINIMAL,
        lineterminator="\n",
    )
    for line in fin:
        line = line.rstrip("\n").rstrip("\r")
        if line.endswith("|"):
            line = line[:-1]
        # TPC-DS field values never contain pipes, so split is safe.
        writer.writerow(line.split("|"))
        total += 1

print(f"  rows={total}", flush=True)
PYEOF

convert_dir() {
    local dir=$1
    shift
    local tables=("$@")
    for t in "${tables[@]}"; do
        local src=${dir}/${t}.csv
        [ -f "$src" ] || { echo "  [skip] $src not found"; continue; }
        local tmp=${src}.commaconv.tmp
        echo "  Converting ${src}..."
        python3 /tmp/pipe_to_csv.py "$src" "$tmp"
        mv -f "$tmp" "$src"
    done
}

echo "=== Converting publisher CSVs ==="
convert_dir /data/publisher/csv "${PUBLISHER_TABLES_ARR[@]}"
echo "=== Converting consumer CSVs ==="
convert_dir /data/consumer/csv "${CONSUMER_TABLES_ARR[@]}"

rm -f /tmp/pipe_to_csv.py
echo "CSV conversion complete."
du -sh /data/publisher/csv/ /data/consumer/csv/
