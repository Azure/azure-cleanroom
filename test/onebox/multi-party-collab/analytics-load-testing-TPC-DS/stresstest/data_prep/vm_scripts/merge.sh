#!/bin/bash
set -e

: "${SCALE_FACTOR:?SCALE_FACTOR is required}"
: "${PUBLISHER_TABLES:?PUBLISHER_TABLES is required}"
: "${CONSUMER_TABLES:?CONSUMER_TABLES is required}"

read -ra PUBLISHER_TABLES_ARR <<< "$PUBLISHER_TABLES"
read -ra CONSUMER_TABLES_ARR <<< "$CONSUMER_TABLES"
ALL_TABLES=("${PUBLISHER_TABLES_ARR[@]}" "${CONSUMER_TABLES_ARR[@]}")

# Skip if partitioned data is already present from a previous run.
PUB_COUNT=$(ls /data/publisher/csv/*.csv 2>/dev/null | wc -l || true)
CON_COUNT=$(ls /data/consumer/csv/*.csv 2>/dev/null | wc -l || true)
if [ "$PUB_COUNT" -ge 12 ] && [ "$CON_COUNT" -ge 12 ] && [ ! -d /data/raw ]; then
    echo "Data already partitioned (pub=$PUB_COUNT, con=$CON_COUNT). Skipping to verification."
else

if [ ! -d /data/raw ]; then
    echo "ERROR: /data/raw does not exist and partitioned data is incomplete."
    echo "  Publisher CSV: $PUB_COUNT/12, Consumer CSV: $CON_COUNT/12"
    echo "  Re-run dsdgen step first."
    exit 1
fi

cd /data/raw

# Process longer table names first so shorter names don't glob-match parts
# of longer ones (e.g. 'store_sales' before 'store').
echo "=== Merging parallel output files ==="
IFS=$'\n' SORTED_TABLES=($(printf '%s\n' "${ALL_TABLES[@]}" | awk '{print length, $0}' | sort -rn | cut -d' ' -f2-))
unset IFS

for table in "${SORTED_TABLES[@]}"; do
    if [ -f "${table}.csv" ]; then
        echo "  ${table}.csv already exists, skipping merge."
        continue
    fi
    # dsdgen -PARALLEL N writes {table}_{child}_{total}.dat; non-parallel writes {table}_{N}.dat.
    parts=($(find . -maxdepth 1 \( -regex "\./${table}_[0-9]+_[0-9]+\.dat" -o -regex "\./${table}_[0-9]+\.dat" \) -printf '%f\n' | sort -t_ -k2 -n))
    if [ ${#parts[@]} -gt 0 ]; then
        echo "  Merging ${table} (${#parts[@]} parts)..."
        > "${table}.csv"
        for part in "${parts[@]}"; do
            cat "$part" >> "${table}.csv"
            rm -f "$part"
        done
        echo "    -> $(du -h "${table}.csv" | cut -f1)"
    elif [ -f "${table}.dat" ]; then
        mv "${table}.dat" "${table}.csv"
        echo "  ${table}.dat -> ${table}.csv (single file)"
    else
        echo "  [warn] No .dat files found for ${table}"
    fi
done
rm -f ./*.dat 2>/dev/null || true

echo "Merge complete. CSV file count: $(ls ./*.csv 2>/dev/null | wc -l)"
du -sh /data/raw/

echo "=== Partitioning into publisher/consumer ==="
mkdir -p /data/publisher/csv /data/consumer/csv
for t in "${PUBLISHER_TABLES_ARR[@]}"; do
    if [ -f "/data/raw/${t}.csv" ] && [ ! -f "/data/publisher/csv/${t}.csv" ]; then
        mv "/data/raw/${t}.csv" "/data/publisher/csv/"
    fi
done
for t in "${CONSUMER_TABLES_ARR[@]}"; do
    if [ -f "/data/raw/${t}.csv" ] && [ ! -f "/data/consumer/csv/${t}.csv" ]; then
        mv "/data/raw/${t}.csv" "/data/consumer/csv/"
    fi
done
rm -rf /data/raw

fi  # end of merge/partition block

# Verify all tables are present; regenerate any missing with single-worker dsdgen.
echo "=== Verifying all 24 tables ==="
MISSING_PUB=()
for t in "${PUBLISHER_TABLES_ARR[@]}"; do
    [ -f "/data/publisher/csv/${t}.csv" ] || MISSING_PUB+=("$t")
done
MISSING_CON=()
for t in "${CONSUMER_TABLES_ARR[@]}"; do
    [ -f "/data/consumer/csv/${t}.csv" ] || MISSING_CON+=("$t")
done

if [ ${#MISSING_PUB[@]} -gt 0 ] || [ ${#MISSING_CON[@]} -gt 0 ]; then
    echo "  Missing publisher: ${MISSING_PUB[*]:-none}"
    echo "  Missing consumer:  ${MISSING_CON[*]:-none}"
    echo "  Regenerating missing tables with single-worker dsdgen..."

    # dsdgen child tables cannot be generated alone; map child -> parent.
    declare -A CHILD_TO_PARENT=(
        ["store_returns"]="store_sales"
        ["catalog_returns"]="catalog_sales"
        ["web_returns"]="web_sales"
    )

    declare -A TABLES_TO_GEN
    ALL_MISSING=("${MISSING_PUB[@]}" "${MISSING_CON[@]}")
    for t in "${ALL_MISSING[@]}"; do
        gen_table="${CHILD_TO_PARENT[$t]:-$t}"
        TABLES_TO_GEN["$gen_table"]=1
    done

    cd /data/DSGen-software-code-4.0.0/tools
    GEN_DIR=/data/regen_tmp
    mkdir -p "$GEN_DIR"
    for gen_table in "${!TABLES_TO_GEN[@]}"; do
        echo "    Generating $gen_table (+ any child tables)..."
        ./dsdgen -DIR "$GEN_DIR" -SCALE "$SCALE_FACTOR" -TABLE "$gen_table" -FORCE Y -TERMINATE N
    done

    for t in "${MISSING_PUB[@]}"; do
        for ext in dat csv; do
            if [ -f "$GEN_DIR/${t}.${ext}" ]; then
                mv "$GEN_DIR/${t}.${ext}" "/data/publisher/csv/${t}.csv"
                echo "    -> publisher/${t}.csv"
                break
            fi
        done
    done
    for t in "${MISSING_CON[@]}"; do
        for ext in dat csv; do
            if [ -f "$GEN_DIR/${t}.${ext}" ]; then
                mv "$GEN_DIR/${t}.${ext}" "/data/consumer/csv/${t}.csv"
                echo "    -> consumer/${t}.csv"
                break
            fi
        done
    done
    rm -rf "$GEN_DIR"
    echo "  Regeneration complete."
fi

echo "Publisher CSV tables: $(ls /data/publisher/csv/ | wc -l)"
echo "Consumer CSV tables: $(ls /data/consumer/csv/ | wc -l)"
du -sh /data/publisher/csv/ /data/consumer/csv/
df -h /data

STILL_MISSING=()
for t in "${ALL_TABLES[@]}"; do
    [ -f "/data/publisher/csv/${t}.csv" ] || [ -f "/data/consumer/csv/${t}.csv" ] || STILL_MISSING+=("$t")
done
if [ ${#STILL_MISSING[@]} -gt 0 ]; then
    echo "ERROR: Still missing: ${STILL_MISSING[*]}"
    exit 1
fi
echo "All ${#ALL_TABLES[@]} tables verified."

# wc -l is very slow on multi-GB files; use byte-size floor instead.
echo "=== File size sanity check ==="
VALIDATION_FAILED=0
check_min_size() {
    local file="$1" table="$2" min_bytes="$3"
    if [ ! -f "$file" ]; then
        echo "  ERROR: $table file missing: $file"
        VALIDATION_FAILED=1
        return
    fi
    actual=$(stat -c%s "$file")
    if [ "$actual" -lt "$min_bytes" ]; then
        echo "  ERROR: $table is ${actual} bytes, expected >= ${min_bytes} (possible merge corruption)"
        VALIDATION_FAILED=1
    else
        echo "  $table: $(du -h "$file" | cut -f1) (ok)"
    fi
}
# Floor at ~10% of expected SF=10 size, scaled linearly with SF.
SF_RATIO=$(( SCALE_FACTOR < 10 ? 1 : SCALE_FACTOR / 10 ))
check_min_size /data/publisher/csv/store_sales.csv store_sales $((SF_RATIO * 40 * 1024 * 1024))
check_min_size /data/publisher/csv/catalog_sales.csv catalog_sales $((SF_RATIO * 30 * 1024 * 1024))
check_min_size /data/publisher/csv/web_sales.csv web_sales $((SF_RATIO * 15 * 1024 * 1024))
check_min_size /data/consumer/csv/customer.csv customer $((SF_RATIO * 2 * 1024 * 1024))
check_min_size /data/consumer/csv/customer_address.csv customer_address $((SF_RATIO * 1 * 1024 * 1024))
# customer_demographics is a FIXED-cardinality dimension in TPC-DS (1,920,800 rows,
# ~75 MB) - it does NOT scale with the scale factor, so use a fixed floor rather than
# an SF_RATIO-scaled one (the scaled floor false-fails at large SF, e.g. sf2000 where
# SF_RATIO*500KB = 100 MB > the real ~75 MB file).
check_min_size /data/consumer/csv/customer_demographics.csv customer_demographics $((50 * 1024 * 1024))

if [ "$VALIDATION_FAILED" -eq 1 ]; then
    echo "ERROR: File size validation failed. Data may be corrupted."
    exit 1
fi
echo "File size validation passed."
