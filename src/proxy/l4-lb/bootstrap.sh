#! /bin/bash

set -e

if [ -z "$CONFIG_DATA_TGZ" ]; then
    echo "Error: CONFIG_DATA_TGZ environment variable must be set"
    exit 1
fi

CONFIG_EXTRACT_DIR=${CONFIG_EXTRACT_DIR:-"/tmp"}

echo "Expanding config payload into $CONFIG_EXTRACT_DIR"
echo "$CONFIG_DATA_TGZ" | base64 -d | tar xz -C $CONFIG_EXTRACT_DIR

echo "Launching envoy with configuration:"
cat "$CONFIG_EXTRACT_DIR/l4-proxy-config.yaml"

# Use exec so that SIGTERM is propagated to the child process and the process can be gracefully stopped.
exec envoy -c $CONFIG_EXTRACT_DIR/l4-proxy-config.yaml