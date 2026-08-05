#!/bin/bash
#
# Creates a shared Kind cluster for k8s-node components.
# Idempotent: reuses an existing cluster if one is already running.
# Pass --force to delete and recreate the cluster.
#
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"

FORCE=false
if [[ "${1:-}" == "--force" ]]; then
    FORCE=true
fi

if [[ "$FORCE" == "true" ]]; then
    log_info "Cleaning up existing cluster (--force)..."
    kind delete cluster --name "$CLUSTER_NAME" 2>/dev/null || true
fi

if kind get clusters 2>/dev/null | grep -q "^${CLUSTER_NAME}$"; then
    log_info "Kind cluster '$CLUSTER_NAME' already exists, reusing it"
    kubectl config use-context "kind-${CLUSTER_NAME}" >/dev/null 2>&1
    exit 0
fi

check_prerequisites

log_info "Creating kind cluster: $CLUSTER_NAME (1 control-plane + 1 worker)"

cat <<EOF | kind create cluster --name "$CLUSTER_NAME" --image "$KIND_IMAGE" --config=-
kind: Cluster
apiVersion: kind.x-k8s.io/v1alpha4
nodes:
- role: control-plane
- role: worker
EOF

log_info "Waiting for cluster to be ready..."
kubectl wait --for=condition=Ready nodes --all --timeout=120s

log_info "Cluster nodes:"
kubectl get nodes -o wide
