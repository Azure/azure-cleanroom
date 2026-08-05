#!/bin/bash
#
# Api-Server-Proxy Install Script (Image-Prep Phase)
#
# Installs the binary, creates config directories, installs the systemd unit
# file, and stages the environment-specific configure.sh. Does NOT start the
# service or write configuration — that is handled by configure.sh later.
#
# Usage:
#   sudo ./install.sh --local-binary <path> [--env aks|kind]
#
# Options:
#   --local-binary FILE   Path to the api-server-proxy binary (required)
#   --env ENV             Target environment: aks (default) or kind. Selects
#                         which configure.sh (aks/ or kind/) is staged.
#   --listen-addr ADDR    Proxy listen address (default: 127.0.0.1:6444)
#   --help                Show this help message
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
PROXY_LISTEN_ADDR="127.0.0.1:6444"
PROXY_BIN_PATH="/usr/local/bin/api-server-proxy"
PROXY_CONFIG_DIR="/etc/api-server-proxy"

parse_args() {
    while [[ $# -gt 0 ]]; do
        case $1 in
            --local-binary) LOCAL_BINARY="$2"; shift 2 ;;
            --env) ENV="$2"; shift 2 ;;
            --listen-addr) PROXY_LISTEN_ADDR="$2"; shift 2 ;;
            --help|-h) head -20 "$0" | grep -E "^#" | sed 's/^# \?//'; exit 0 ;;
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

install_systemd_unit() {
    log_step "Installing systemd unit file..."

    # The unit references config files that configure.sh will create at boot.
    # EnvironmentFile provides runtime overrides (e.g., --insecure toggle).
    cat > /etc/systemd/system/api-server-proxy.service <<EOF
[Unit]
Description=API Server Proxy - Pod Admission Control
Before=kubelet.service
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
EnvironmentFile=-$PROXY_CONFIG_DIR/service-env
ExecStart=$PROXY_BIN_PATH \\
    --kubeconfig $PROXY_CONFIG_DIR/upstream-kubeconfig \\
    --listen-addr $PROXY_LISTEN_ADDR \\
    --tls-cert $PROXY_CONFIG_DIR/api-server-proxy.crt \\
    --tls-key $PROXY_CONFIG_DIR/api-server-proxy.key \\
    --policy-verification-cert $PROXY_CONFIG_DIR/signing-cert.pem \\
    --rewrite-kubelet-port 10250 \\
    --log-requests=true \\
    --log-pod-payloads=false \\
    \${EXTRA_ARGS}

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
    log_info "  api-server-proxy Install (Image-Prep)"
    log_info "=========================================="
    echo ""

    install_binary
    create_directories
    install_systemd_unit
    stage_configure_script

    echo ""
    log_info "Installation complete. Run configure.sh to start the service."
}

main "$@"
