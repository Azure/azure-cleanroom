#!/bin/bash
#
# Kubelet-Proxy Install Script (Image-Prep Phase)
#
# Installs the binary, creates config directories, installs the systemd unit
# file, stages the environment-specific configure.sh, and (for aks) creates
# the kubelet port drop-in. Does NOT start the service or write TLS
# certs/policy — that is handled by configure.sh later.
#
# Usage:
#   sudo ./install.sh --local-binary <path> [--env aks|kind]
#
# Options:
#   --local-binary FILE         Path to the kubelet-proxy binary (required)
#   --env ENV                   Target environment: aks (default) or kind.
#                               Selects which configure.sh is staged and
#                               whether the AKS kubelet port drop-in is created
#                               (kind edits /var/lib/kubelet/config.yaml instead).
#   --proxy-listen-port PORT    Port for kubelet-proxy (default: 10250)
#   --kubelet-port PORT         Port kubelet will use (default: 10251)
#   --help                      Show this help message
#

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

RED='\033[0;31m'
GREEN='\033[0;32m'
BLUE='\033[0;34m'
NC='\033[0m'

log_info() { echo -e "${GREEN}[INFO]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }
log_step() { echo -e "${BLUE}[STEP]${NC} $1"; }

LOCAL_BINARY=""
ENV="aks"
PROXY_LISTEN_PORT=10250
KUBELET_PORT=10251
PROXY_BIN_PATH="/usr/local/bin/kubelet-proxy"
PROXY_CONFIG_DIR="/etc/kubelet-proxy"

parse_args() {
    while [[ $# -gt 0 ]]; do
        case $1 in
            --local-binary) LOCAL_BINARY="$2"; shift 2 ;;
            --env) ENV="$2"; shift 2 ;;
            --proxy-listen-port) PROXY_LISTEN_PORT="$2"; shift 2 ;;
            --kubelet-port) KUBELET_PORT="$2"; shift 2 ;;
            --help|-h) head -22 "$0" | grep -E "^#" | sed 's/^# \?//'; exit 0 ;;
            *) log_error "Unknown option: $1"; exit 1 ;;
        esac
    done

    case "$ENV" in
        aks|kind) ;;
        *) log_error "Invalid --env '$ENV' (must be aks or kind)"; exit 1 ;;
    esac
}

stage_configure_script() {
    log_step "Staging configure.sh for env '$ENV'..."
    local src="$SCRIPT_DIR/$ENV/configure.sh"
    [[ -f "$src" ]] || {
        log_error "configure.sh for env '$ENV' not found at $src"; exit 1
    }
    cp "$src" "$SCRIPT_DIR/configure.sh"
    chmod +x "$SCRIPT_DIR/configure.sh"
    log_info "Staged $ENV configure.sh to $SCRIPT_DIR/configure.sh"
}

install_binary() {
    log_step "Installing binary..."
    [[ -n "$LOCAL_BINARY" && -f "$LOCAL_BINARY" ]] || {
        log_error "--local-binary is required and must exist"; exit 1
    }
    cp "$LOCAL_BINARY" "$PROXY_BIN_PATH"
    chmod +x "$PROXY_BIN_PATH"
    log_info "Binary installed to $PROXY_BIN_PATH"
}

create_directories() {
    log_step "Creating config directory..."
    mkdir -p "$PROXY_CONFIG_DIR"
    chmod 700 "$PROXY_CONFIG_DIR"
    log_info "Config dir: $PROXY_CONFIG_DIR"
}

install_kubelet_port_dropin() {
    log_step "Installing kubelet port drop-in..."

    local dropin_dir="/etc/systemd/system/kubelet.service.d"
    mkdir -p "$dropin_dir"

    cat > "$dropin_dir/30-kubelet-proxy-node-config.conf" <<EOF
[Service]
Environment="KUBELET_EXTRA_ARGS=--port=$KUBELET_PORT"
EOF

    log_info "Kubelet will listen on :$KUBELET_PORT (proxy on :$PROXY_LISTEN_PORT)"
}
install_systemd_unit() {
    log_step "Installing systemd unit file..."

    # The unit references config files that configure.sh will create at boot.
    cat > /etc/systemd/system/kubelet-proxy.service <<EOF
[Unit]
Description=Kubelet Proxy - Kubelet API Access Control
Before=kubelet.service
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=$PROXY_BIN_PATH \\
    --kubelet-url https://127.0.0.1:$KUBELET_PORT \\
    --listen-addr :$PROXY_LISTEN_PORT \\
    --server-cert $PROXY_CONFIG_DIR/server.crt \\
    --server-key $PROXY_CONFIG_DIR/server.key \\
    --client-cert $PROXY_CONFIG_DIR/client.crt \\
    --client-key $PROXY_CONFIG_DIR/client.key \\
    --api-policy $PROXY_CONFIG_DIR/api-policy.json \\
    --log-requests=true

Restart=always
RestartSec=5
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF

    systemctl daemon-reload
    log_info "Systemd unit installed (not started)"
}

main() {
    parse_args "$@"
    [[ $EUID -eq 0 ]] || { log_error "Must be run as root"; exit 1; }

    echo ""
    log_info "=========================================="
    log_info "  kubelet-proxy Install (Image-Prep)"
    log_info "=========================================="
    echo ""

    install_binary
    create_directories
    # The AKS kubelet port drop-in relies on $KUBELET_EXTRA_ARGS in the flex
    # node's kubelet.service template. Kind's kubelet does not use that
    # variable, so its configure.sh edits /var/lib/kubelet/config.yaml instead.
    if [[ "$ENV" == "aks" ]]; then
        install_kubelet_port_dropin
    fi
    install_systemd_unit
    stage_configure_script

    echo ""
    log_info "Installation complete. Run configure.sh to start the service."
}

main "$@"
