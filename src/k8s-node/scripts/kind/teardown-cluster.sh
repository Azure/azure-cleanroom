#!/bin/bash
#
# Tears down the shared Kind cluster and cleans up temporary files.
#
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"

log_info "Deleting kind cluster: $CLUSTER_NAME"
kind delete cluster --name "$CLUSTER_NAME" 2>/dev/null || true

log_info "Cleaning up temporary files..."
rm -rf /tmp/api-server-proxy-logs
rm -rf /tmp/kubelet-proxy-logs

# Clean build artifacts in each project.
rm -rf "$K8S_NODE_DIR/api-server-proxy/tmp"
rm -rf "$K8S_NODE_DIR/api-server-proxy/bin"
rm -rf "$K8S_NODE_DIR/kubelet-proxy/bin"

log_info "Cleanup complete!"
