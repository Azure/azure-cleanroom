#!/bin/bash
#
# Configures the Kind cluster's worker node as a cleanroom flex node by
# installing and configuring both proxies. The worker node must already exist
# (created by deploy-cluster.sh). This is the Kind analogue of
# scripts/aks/deploy-flex-node-vm.sh and mirrors the product orchestration in
# VirtualClusterProvider.ConfigureFlexNodeWorkerAsync.
#
# Steps:
#   1. Build both proxy binaries (host-side).
#   2. Generate the pod-policy signing keypair.
#   3. Taint + label the worker node so only signed pods schedule there.
#   4. Extract the real apiserver-kubelet-client cert from the control-plane.
#   5. Per proxy: docker cp files, run install.sh --env kind, run configure.sh.
#   6. Verify both services and record the install mode for the tests.
#
# Usage:
#   ./deploy-kind-flex-node.sh [--insecure]
#
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"

GENERATED_DIR="$SCRIPT_DIR/generated"
SIGNING_KEY_DIR="$GENERATED_DIR/policy-signing-keys"

ASP_DIR="$K8S_NODE_DIR/api-server-proxy"
KP_DIR="$K8S_NODE_DIR/kubelet-proxy"
SIGNING_TOOL="$ASP_DIR/scripts/policy-signing-tool.sh"
PROXY_LISTEN_ADDR="127.0.0.1:6444"

# Parse flags.
INSECURE_MODE=false
for arg in "$@"; do
    [[ "$arg" == "--insecure" ]] && INSECURE_MODE=true
done

if [[ "$INSECURE_MODE" == "true" ]]; then
    INSTALL_MODE="insecure"
    KP_POLICY_FILE="$KP_DIR/scripts/api-policies/insecure-api-policy.json"
    log_info "Install mode: INSECURE (allows /containerLogs, /logs, /exec)"
else
    INSTALL_MODE="default"
    KP_POLICY_FILE="$KP_DIR/scripts/api-policies/default-api-policy.json"
    log_info "Install mode: DEFAULT"
fi

build_binaries() {
    log_info "Building proxy binaries..."
    local repo_root
    repo_root="$(cd "$K8S_NODE_DIR/../.." && pwd)"

    CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -C "$repo_root" \
        -ldflags "-s -w" \
        -o "$ASP_DIR/bin/api-server-proxy-linux-amd64" \
        github.com/azure/azure-cleanroom/src/k8s-node/api-server-proxy/cmd/api-server-proxy

    CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -C "$repo_root" \
        -ldflags "-s -w" \
        -o "$KP_DIR/bin/kubelet-proxy-linux-amd64" \
        github.com/azure/azure-cleanroom/src/k8s-node/kubelet-proxy/cmd/kubelet-proxy

    log_info "Binaries built."
}

generate_signing_keys() {
    log_info "Generating pod-policy signing keys..."
    "$SIGNING_TOOL" --key-dir "$SIGNING_KEY_DIR" generate
}

taint_and_label_worker() {
    log_info "Tainting and labeling worker node: $WORKER_NODE_NAME"
    kubectl label node "$WORKER_NODE_NAME" pod-policy=required --overwrite
    kubectl label node "$WORKER_NODE_NAME" cleanroom.azure.com/flexnode=true --overwrite
    kubectl taint node "$WORKER_NODE_NAME" pod-policy=required:NoSchedule --overwrite
}

# stage_proxy copies install.sh + kind/configure.sh + uninstall.sh + the binary
# into the node's staging dir, then removes any existing install so a fresh
# install.sh + configure.sh run cleanly.
stage_proxy() {
    local proxy_dir="$1"      # e.g., $ASP_DIR
    local proxy_name="$2"     # e.g., api-server-proxy
    local staging_dir="$3"
    local binary="$4"         # host path to linux binary

    docker exec "$WORKER_NODE_NAME" mkdir -p "$staging_dir/kind"
    docker cp "$binary" "$WORKER_NODE_NAME:$staging_dir/$proxy_name"
    docker cp "$proxy_dir/scripts/install.sh" "$WORKER_NODE_NAME:$staging_dir/install.sh"
    docker cp "$proxy_dir/scripts/kind/configure.sh" "$WORKER_NODE_NAME:$staging_dir/kind/configure.sh"
    docker cp "$proxy_dir/scripts/uninstall.sh" "$WORKER_NODE_NAME:$staging_dir/uninstall.sh"

    # Uninstall any existing install first (idempotent — tolerates absence).
    log_info "Uninstalling existing $proxy_name (if any)..."
    docker exec "$WORKER_NODE_NAME" bash "$staging_dir/uninstall.sh"
}

deploy_api_server_proxy() {
    log_info "Deploying api-server-proxy to $WORKER_NODE_NAME..."
    local staging_dir="/opt/api-server-proxy-staging"

    stage_proxy "$ASP_DIR" "api-server-proxy" "$staging_dir" \
        "$ASP_DIR/bin/api-server-proxy-linux-amd64"

    local signing_cert_file
    signing_cert_file=$("$SIGNING_TOOL" --key-dir "$SIGNING_KEY_DIR" cert)
    docker cp "$signing_cert_file" "$WORKER_NODE_NAME:$staging_dir/signing-cert.pem"

    log_info "Running install.sh (--env kind)..."
    docker exec "$WORKER_NODE_NAME" bash "$staging_dir/install.sh" \
        --env kind \
        --local-binary "$staging_dir/api-server-proxy" \
        --listen-addr "$PROXY_LISTEN_ADDR"

    log_info "Running configure.sh..."
    local insecure_arg=""
    [[ "$INSECURE_MODE" == "true" ]] && insecure_arg="--insecure"
    docker exec "$WORKER_NODE_NAME" bash "$staging_dir/configure.sh" \
        --signing-cert-file "$staging_dir/signing-cert.pem" \
        $insecure_arg
}

deploy_kubelet_proxy() {
    log_info "Deploying kubelet-proxy to $WORKER_NODE_NAME..."
    local staging_dir="/opt/kubelet-proxy-staging"

    stage_proxy "$KP_DIR" "kubelet-proxy" "$staging_dir" \
        "$KP_DIR/bin/kubelet-proxy-linux-amd64"

    # Extract the real apiserver-kubelet-client cert from the control-plane;
    # it is signed by the cluster CA so kubelet accepts the proxy's connection.
    log_info "Extracting apiserver-kubelet-client cert from control-plane..."
    docker cp "$CONTROL_PLANE_NODE_NAME:/etc/kubernetes/pki/apiserver-kubelet-client.crt" \
        "/tmp/apiserver-kubelet-client.crt"
    docker cp "$CONTROL_PLANE_NODE_NAME:/etc/kubernetes/pki/apiserver-kubelet-client.key" \
        "/tmp/apiserver-kubelet-client.key"
    docker cp "/tmp/apiserver-kubelet-client.crt" "$WORKER_NODE_NAME:$staging_dir/client.crt"
    docker cp "/tmp/apiserver-kubelet-client.key" "$WORKER_NODE_NAME:$staging_dir/client.key"
    rm -f /tmp/apiserver-kubelet-client.crt /tmp/apiserver-kubelet-client.key

    docker cp "$KP_POLICY_FILE" "$WORKER_NODE_NAME:$staging_dir/api-policy.json"

    log_info "Running install.sh (--env kind)..."
    docker exec "$WORKER_NODE_NAME" bash "$staging_dir/install.sh" \
        --env kind \
        --local-binary "$staging_dir/kubelet-proxy"

    log_info "Running configure.sh..."
    docker exec "$WORKER_NODE_NAME" bash "$staging_dir/configure.sh" \
        --client-cert "$staging_dir/client.crt" \
        --client-key "$staging_dir/client.key" \
        --api-policy-file "$staging_dir/api-policy.json"

    # Record the install mode so the integration tests know which policy is active.
    mkdir -p "$GENERATED_DIR"
    echo "{\"install_mode\": \"$INSTALL_MODE\"}" > "$GENERATED_DIR/install-config.json"
}

verify_deployment() {
    log_info "Verifying proxy services on $WORKER_NODE_NAME..."
    for svc in api-server-proxy kubelet-proxy; do
        if docker exec "$WORKER_NODE_NAME" systemctl is-active --quiet "$svc"; then
            log_info "  $svc: active"
        else
            log_error "  $svc is not active"
            docker exec "$WORKER_NODE_NAME" journalctl -u "$svc" --no-pager -n 20 || true
            exit 1
        fi
    done

    echo ""
    kubectl get nodes -o wide
}

main() {
    log_info "Configuring Kind worker '$WORKER_NODE_NAME' as a flex node"

    check_prerequisites

    mkdir -p "$ASP_DIR/bin" "$KP_DIR/bin" "$GENERATED_DIR"

    build_binaries
    generate_signing_keys
    taint_and_label_worker

    # api-server-proxy first so it can rewrite node status when kubelet
    # restarts during kubelet-proxy configuration.
    deploy_api_server_proxy
    deploy_kubelet_proxy

    verify_deployment

    log_info "Kind flex node configuration complete."
}

main "$@"
