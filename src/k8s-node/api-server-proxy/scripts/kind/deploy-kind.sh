#!/bin/bash
set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

log_info() { echo -e "${GREEN}[INFO]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }

# Configuration
CLUSTER_NAME="${CLUSTER_NAME:-api-server-proxy-test}"
KIND_IMAGE="${KIND_IMAGE:-kindest/node:v1.33.0}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$(dirname "$SCRIPT_DIR")")"
WORKER_NODE_NAME="${CLUSTER_NAME}-worker"
SIGNING_TOOL="${SCRIPT_DIR}/../policy-signing-tool.sh"
GENERATED_DIR="$SCRIPT_DIR/generated"
SIGNING_KEY_DIR="${GENERATED_DIR}/policy-signing-keys"

# Proxy configuration
PROXY_LISTEN_ADDR="127.0.0.1:6444"

cleanup() {
    log_info "Cleaning up existing cluster if present..."
    kind delete cluster --name "$CLUSTER_NAME" 2>/dev/null || true
}

build_binary() {
    log_info "Building api-server-proxy binary..."
    local repo_root="$(dirname "$(dirname "$PROJECT_ROOT")")"
    
    # Build for Linux (kind nodes run Linux)
    # CGO_ENABLED=0 creates a statically linked binary that doesn't depend on glibc
    # Build from repo root where go.mod lives, using full module path
    # Note: -C must be the first flag on the command line.
    CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build \
        -C "$repo_root" \
        -v -ldflags "-s -w" \
        -o "$PROJECT_ROOT/bin/api-server-proxy-linux-amd64" \
        github.com/azure/azure-cleanroom/src/k8s-node/api-server-proxy/cmd/api-server-proxy
    
    log_info "Binary built: bin/api-server-proxy-linux-amd64 (static)"
}

generate_signing_keys() {
    log_info "Generating signing keys using policy-signing-tool.sh..."
    "$SIGNING_TOOL" --key-dir "$SIGNING_KEY_DIR" generate
}

create_cluster() {
    log_info "Creating kind cluster: $CLUSTER_NAME (1 control-plane + 1 worker)"
    
    # Create cluster with custom config - 2 nodes
    cat <<EOF | kind create cluster --name "$CLUSTER_NAME" --image "$KIND_IMAGE" --config=-
kind: Cluster
apiVersion: kind.x-k8s.io/v1alpha4
nodes:
- role: control-plane
- role: worker
  # Extra mounts for logs on worker node
  extraMounts:
  - hostPath: /tmp/api-server-proxy-logs
    containerPath: /var/log/api-server-proxy
EOF
    
    # Wait for cluster to be ready
    log_info "Waiting for cluster to be ready..."
    kubectl wait --for=condition=Ready nodes --all --timeout=120s
    
    log_info "Cluster nodes:"
    kubectl get nodes -o wide
}

label_and_taint_worker_node() {
    log_info "Adding label and taint to worker node for signed pods only..."
    
    # Add label to worker node
    kubectl label node "$WORKER_NODE_NAME" pod-policy=required --overwrite
    
    # Add taint to worker node - only pods with matching toleration can be scheduled
    kubectl taint node "$WORKER_NODE_NAME" pod-policy=required:NoSchedule --overwrite
    
    log_info "Worker node labeled and tainted:"
    kubectl get node "$WORKER_NODE_NAME" -o jsonpath='{.spec.taints}' | jq . 2>/dev/null || kubectl get node "$WORKER_NODE_NAME" -o jsonpath='{.spec.taints}'
    echo ""
}



deploy_to_node() {
    log_info "Deploying api-server-proxy to worker node using install.sh: $WORKER_NODE_NAME"
    
    local staging_dir="/opt/api-server-proxy-staging"
    
    # Create staging directory on node
    docker exec "$WORKER_NODE_NAME" mkdir -p "$staging_dir"
    
    # Get signing certificate path from policy-signing-tool
    local signing_cert_file
    signing_cert_file=$("$SIGNING_TOOL" --key-dir "$SIGNING_KEY_DIR" cert)
    log_info "Using signing certificate: $signing_cert_file"
    
    # Copy files to worker node
    log_info "Copying files to worker node..."
    
    # Copy local binary, install script, and signing cert
    docker cp "$PROJECT_ROOT/bin/api-server-proxy-linux-amd64" "$WORKER_NODE_NAME:$staging_dir/api-server-proxy"
    docker cp "$PROJECT_ROOT/scripts/install.sh" "$WORKER_NODE_NAME:$staging_dir/install.sh"
    docker cp "$signing_cert_file" "$WORKER_NODE_NAME:$staging_dir/signing-cert.pem"
    
    # Verify files were copied
    log_info "Verifying files on node..."
    docker exec "$WORKER_NODE_NAME" ls -la "$staging_dir/"
    
    # Run install.sh with local binary and signing cert file
    log_info "Running install.sh on worker node..."
    docker exec "$WORKER_NODE_NAME" bash "$staging_dir/install.sh" \
        --local-binary "$staging_dir/api-server-proxy" \
        --signing-cert-file "$staging_dir/signing-cert.pem" \
        --proxy-listen-addr "$PROXY_LISTEN_ADDR"
}

verify_deployment() {
    log_info "Verifying deployment on worker node: $WORKER_NODE_NAME"
    
    echo ""
    echo "=== API server proxy status on worker ==="
    docker exec "$WORKER_NODE_NAME" systemctl status api-server-proxy --no-pager || true
    
    echo ""
    echo "=== Recent api-server-proxy logs ==="
    docker exec "$WORKER_NODE_NAME" journalctl -u api-server-proxy --no-pager -n 20 || true
    
    echo ""
    echo "=== Cluster nodes ==="
    kubectl get nodes -o wide
    
    echo ""
    echo "=== All pods ==="
    kubectl get pods -A
}

print_usage() {
    echo ""
    log_info "Deployment complete! Here's how to test:"
    echo ""
    echo "NOTE: api-server-proxy is installed on the WORKER node only."
    echo "      The worker node has a taint 'pod-policy=required:NoSchedule'."
    echo "      Pods must have matching toleration AND node selector to be scheduled there."
    echo "      Pod policy verification is ENABLED - unsigned pods will be rejected."
    echo ""
    echo "1. Check api-server-proxy logs on worker:"
    echo "   docker exec $WORKER_NODE_NAME journalctl -u api-server-proxy -f"
    echo ""
    echo "2. Run pod policies verification tests:"
    echo "   make test-pod-policies"
    echo ""
    echo "3. Deploy an unsigned pod (will be rejected):"
    echo "   kubectl run test-unsigned --image=nginx --restart=Never"
    echo "   kubectl get pod test-unsigned  # Should show Failed status"
    echo ""
    echo "4. Signing keys:"
    echo "   ls $SIGNING_KEY_DIR/"
    echo ""
    echo "5. Clean up:"
    echo "   kind delete cluster --name $CLUSTER_NAME"
    echo ""
}

main() {
    log_info "Starting api-server-proxy deployment to kind cluster"
    
    # Check prerequisites
    command -v kind >/dev/null 2>&1 || { log_error "kind is required but not installed"; exit 1; }
    command -v docker >/dev/null 2>&1 || { log_error "docker is required but not installed"; exit 1; }
    command -v kubectl >/dev/null 2>&1 || { log_error "kubectl is required but not installed"; exit 1; }
    command -v openssl >/dev/null 2>&1 || { log_error "openssl is required but not installed"; exit 1; }
    
    # Create tmp directory
    mkdir -p "$PROJECT_ROOT/tmp"
    mkdir -p /tmp/api-server-proxy-logs
    
    # Run deployment steps
    cleanup
    build_binary
    generate_signing_keys
    create_cluster
    label_and_taint_worker_node
    deploy_to_node
    verify_deployment
    print_usage
    
    log_info "Done!"
}

# Run main
main "$@"
