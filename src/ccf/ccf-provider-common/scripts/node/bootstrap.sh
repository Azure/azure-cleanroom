#! /bin/bash

set -e

if [ -z "$CONFIG_DATA_TGZ" ]; then
    echo "Error: CONFIG_DATA_TGZ environment variable must be set"
    exit 1
fi

CONFIG_EXTRACT_DIR=${CONFIG_EXTRACT_DIR:-"/app"}
LOGS_DIR=${LOGS_DIR:-"/app/logs"}
CCF_LOG_LEVEL=${CCF_LOG_LEVEL:-"info"}

echo "Expanding config payload into $CONFIG_EXTRACT_DIR"
echo "$CONFIG_DATA_TGZ" | base64 -d | tar xz -C $CONFIG_EXTRACT_DIR

if [ -n "$TAIL_DEV_NULL" ]; then
    echo "Executing tail -f /dev/null."
    tail -f /dev/null
fi

echo "Running as: $(id)"

# Adding pipe to tee to capture the process output to a log file for when we need to debug.
mkdir -p $LOGS_DIR
echo "Launching cchost"
cchostLog="$LOGS_DIR/cchost_$(date +"%Y_%m_%d_%I_%M_%p").log"
ln -f -s $cchostLog "/app/cchost.log"
# Use exec so that SIGTERM is propagated to the child process and the process can be gracefully stopped.
exec /usr/bin/cchost --config $CONFIG_EXTRACT_DIR/cchost_config.json --enclave-log-level $CCF_LOG_LEVEL 2>&1 | tee $cchostLog
