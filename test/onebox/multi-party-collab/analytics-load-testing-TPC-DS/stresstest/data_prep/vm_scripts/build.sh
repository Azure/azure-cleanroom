#!/bin/bash
set -e

: "${STORAGE_ACCOUNT:?STORAGE_ACCOUNT is required}"
: "${TOOLKIT_CONTAINER:?TOOLKIT_CONTAINER is required}"

# This runs early under cloud-init, shortly after the VM's managed identity is
# granted Storage Blob Data Contributor. Retry azcopy to absorb the brief RBAC
# propagation window before the role takes effect.
azcopy_retry() {
    local i
    for i in 1 2 3 4 5 6; do
        if azcopy "$@"; then
            return 0
        fi
        if [ "$i" -lt 6 ]; then
            echo "azcopy failed (attempt $i/6); retrying in 30s (RBAC may still be propagating)..."
            sleep 30
        fi
    done
    printf 'ERROR: azcopy failed after retries:'
    printf ' %q' "$@"
    printf '\n'
    return 1
}

# Sentinel = a real toolkit file, not the dir (a previous failed tar can leave
# an empty/partial DSGen-software-code-4.0.0/).
TOOLKIT_SENTINEL=/data/DSGen-software-code-4.0.0/tools/Makefile.suite
if [ ! -f "$TOOLKIT_SENTINEL" ]; then
    echo "=== Downloading TPC-DS toolkit from storage ==="
    rm -rf /data/DSGen-software-code-4.0.0
    azcopy_retry copy "https://${STORAGE_ACCOUNT}.blob.core.windows.net/${TOOLKIT_CONTAINER}/tpcds-toolkit.tar.gz" /data/tpcds-toolkit.tar.gz
    cd /data && tar xzf tpcds-toolkit.tar.gz && rm -f tpcds-toolkit.tar.gz
    if [ ! -f "$TOOLKIT_SENTINEL" ]; then
        echo "ERROR: toolkit extraction did not produce $TOOLKIT_SENTINEL"
        exit 1
    fi
fi

azcopy_retry copy "https://${STORAGE_ACCOUNT}.blob.core.windows.net/${TOOLKIT_CONTAINER}/convert_to_parquet.py" /tmp/convert_to_parquet.py
azcopy_retry copy "https://${STORAGE_ACCOUNT}.blob.core.windows.net/${TOOLKIT_CONTAINER}/tpcds_helpers.py" /tmp/tpcds_helpers.py
azcopy_retry copy "https://${STORAGE_ACCOUNT}.blob.core.windows.net/${TOOLKIT_CONTAINER}/format_utils.py" /tmp/format_utils.py

cd /data/DSGen-software-code-4.0.0/tools
if [ ! -f dsdgen ]; then
    echo "=== Building dsdgen ==="
    make -f Makefile.suite OS=LINUX CFLAGS='-D_FILE_OFFSET_BITS=64 -D_LARGEFILE_SOURCE -DYYDEBUG -DLINUX -g -Wall -fcommon'
fi

echo "dsdgen build complete."
ls -la /data/DSGen-software-code-4.0.0/tools/dsdgen
