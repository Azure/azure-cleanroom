#!/bin/bash
set -e

: "${SCALE_FACTOR:?SCALE_FACTOR is required}"
: "${FORMATS:?FORMATS is required}"
: "${PUBLISHER_STORAGE_ACCOUNT:?PUBLISHER_STORAGE_ACCOUNT is required}"
: "${CONSUMER_STORAGE_ACCOUNT:?CONSUMER_STORAGE_ACCOUNT is required}"
: "${PUBLISHER_TABLES:?PUBLISHER_TABLES is required}"
: "${CONSUMER_TABLES:?CONSUMER_TABLES is required}"

read -ra FORMATS_ARR <<< "$FORMATS"
read -ra PUBLISHER_TABLES_ARR <<< "$PUBLISHER_TABLES"
read -ra CONSUMER_TABLES_ARR <<< "$CONSUMER_TABLES"

echo "=== Starting uploads ==="

upload_count=0

# `azcopy make` avoids a 404 on the first upload to a fresh account;
# `|| true` swallows the 409 "already exists" on re-runs.
upload_table() {
    local role=$1 role_long=$2 table=$3 format=$4 account=$5
    local ext=$format
    [ "$format" = "parquet" ] || ext=csv
    local table_safe=${table//_/-}
    local container="tpcds-${role}-${table_safe}-sf${SCALE_FACTOR}-${format}"
    local src="/data/${role_long}/${format}/${table}.${ext}"
    local dst="https://${account}.blob.core.windows.net/${container}/"
    local mk="https://${account}.blob.core.windows.net/${container}"
    echo "Uploading ${role_long} ${table} (${format})..."
    azcopy make "$mk" 2>/dev/null || true
    azcopy copy "$src" "$dst" --put-md5
    upload_count=$((upload_count + 1))
}

for format in "${FORMATS_ARR[@]}"; do
    for table in "${PUBLISHER_TABLES_ARR[@]}"; do
        upload_table pub publisher "$table" "$format" "$PUBLISHER_STORAGE_ACCOUNT"
    done
    for table in "${CONSUMER_TABLES_ARR[@]}"; do
        upload_table con consumer "$table" "$format" "$CONSUMER_STORAGE_ACCOUNT"
    done
done

echo "Upload complete. ${upload_count} files uploaded."
