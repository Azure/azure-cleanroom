#!/bin/bash
set -e

# Teardown script for api-server-proxy kind cluster
CLUSTER_NAME="${CLUSTER_NAME:-api-server-proxy-test}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$(dirname "$SCRIPT_DIR")")"

echo "Deleting kind cluster: $CLUSTER_NAME"
kind delete cluster --name "$CLUSTER_NAME" 2>/dev/null || true

echo "Cleaning up temporary files..."
rm -rf "$PROJECT_ROOT/tmp"
rm -rf /tmp/api-server-proxy-logs

echo "Cleanup complete!"
