#!/bin/bash
#
# Api-Server-Proxy Configuration Script (Kind Environment)
#
# Configures and starts the api-server-proxy on a Kind worker node. Unlike the
# AKS flow, kubelet is already running when this runs, so we:
#   1. Reuse kubelet's existing kubeconfig (client-cert auth) as the proxy's
#      upstream credentials.
#   2. Rewrite kubelet's kubeconfig to point at the proxy (127.0.0.1:6444) and
#      restart kubelet so its traffic flows through the proxy.
#
# The binary and systemd unit must already be installed by install.sh.
#
# Usage:
#   sudo ./configure.sh --signing-cert-file <path> [--insecure]
#
# Options:
#   --signing-cert-file FILE      Pod policy signing certificate (required
#                                 unless --insecure)
#   --insecure                    Bypass pod policy verification
#   --help                        Show this help message
#

set -e

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

log_info() { echo -e "${GREEN}[INFO]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }
log_step() { echo -e "${BLUE}[STEP]${NC} $1"; }

SIGNING_CERT_FILE=""
INSECURE=false

PROXY_CONFIG_DIR="/etc/api-server-proxy"
PROXY_BIN_PATH="/usr/local/bin/api-server-proxy"
PROXY_LISTEN_ADDR="127.0.0.1:6444"
KUBELET_KUBECONFIG="/etc/kubernetes/kubelet.conf"
KUBELET_KUBECONFIG_BACKUP="/etc/kubernetes/kubelet.conf.api-server-proxy-backup"

parse_args() {
    while [[ $# -gt 0 ]]; do
        case $1 in
            --signing-cert-file) SIGNING_CERT_FILE="$2"; shift 2 ;;
            --insecure) INSECURE=true; shift ;;
            --help|-h) head -22 "$0" | grep -E "^#" | sed 's/^# \?//'; exit 0 ;;
            *) log_error "Unknown option: $1"; exit 1 ;;
        esac
    done
}

check_prerequisites() {
    log_step "Checking prerequisites..."
    [[ $EUID -eq 0 ]] || { log_error "Must be run as root"; exit 1; }
    [[ -x "$PROXY_BIN_PATH" ]] || {
        log_error "Binary not found at $PROXY_BIN_PATH — run install.sh first"; exit 1
    }
    [[ -f "$KUBELET_KUBECONFIG" ]] || {
        log_error "kubelet kubeconfig not found at $KUBELET_KUBECONFIG"; exit 1
    }
    if [[ "$INSECURE" == "false" && -z "$SIGNING_CERT_FILE" ]]; then
        log_error "--signing-cert-file is required (or use --insecure)"
        exit 1
    fi
    command -v openssl &>/dev/null || { log_error "openssl is required"; exit 1; }
    log_info "Prerequisites OK"
}

generate_tls_certs() {
    log_step "Generating TLS certificates..."
    mkdir -p "$PROXY_CONFIG_DIR"
    chmod 700 "$PROXY_CONFIG_DIR"

    local hostname
    hostname=$(hostname)

    openssl req -x509 -nodes -days 365 -newkey rsa:2048 \
        -keyout "$PROXY_CONFIG_DIR/api-server-proxy.key" \
        -out "$PROXY_CONFIG_DIR/api-server-proxy.crt" \
        -subj "/CN=api-server-proxy/O=cleanroom" \
        -addext "subjectAltName=IP:127.0.0.1,DNS:localhost,DNS:$hostname" \
        2>/dev/null

    chmod 600 "$PROXY_CONFIG_DIR/api-server-proxy.key"
    chmod 644 "$PROXY_CONFIG_DIR/api-server-proxy.crt"
    log_info "TLS cert: $PROXY_CONFIG_DIR/api-server-proxy.crt"
}

write_upstream_kubeconfig() {
    log_step "Writing upstream kubeconfig from kubelet's credentials..."

    # Back up the original kubelet kubeconfig (points at the real API server
    # with client-cert auth). The proxy uses this to talk upstream, preserving
    # kubelet's identity (system:node:<name>).
    if [[ ! -f "$KUBELET_KUBECONFIG_BACKUP" ]]; then
        cp "$KUBELET_KUBECONFIG" "$KUBELET_KUBECONFIG_BACKUP"
        log_info "Backed up kubelet kubeconfig to $KUBELET_KUBECONFIG_BACKUP"
    fi

    cp "$KUBELET_KUBECONFIG_BACKUP" "$PROXY_CONFIG_DIR/upstream-kubeconfig"
    chmod 600 "$PROXY_CONFIG_DIR/upstream-kubeconfig"
    log_info "Upstream kubeconfig: $PROXY_CONFIG_DIR/upstream-kubeconfig"
}

install_signing_cert() {
    if [[ "$INSECURE" == "true" ]]; then
        log_info "Insecure mode — skipping signing cert"
        return
    fi
    log_step "Installing signing certificate..."
    cp "$SIGNING_CERT_FILE" "$PROXY_CONFIG_DIR/signing-cert.pem"
    chmod 644 "$PROXY_CONFIG_DIR/signing-cert.pem"
    log_info "Signing cert: $PROXY_CONFIG_DIR/signing-cert.pem"
}

write_service_env() {
    log_step "Writing service environment..."

    local env_file="$PROXY_CONFIG_DIR/service-env"
    if [[ "$INSECURE" == "true" ]]; then
        echo "EXTRA_ARGS=--insecure" > "$env_file"
    else
        echo "EXTRA_ARGS=" > "$env_file"
    fi
    chmod 644 "$env_file"
    log_info "Service env: $env_file"
}

start_service() {
    log_step "Starting api-server-proxy..."
    systemctl daemon-reload
    systemctl enable api-server-proxy
    systemctl start api-server-proxy

    sleep 3
    if systemctl is-active --quiet api-server-proxy; then
        log_info "api-server-proxy is running"
    else
        log_error "api-server-proxy failed to start"
        journalctl -u api-server-proxy --no-pager -n 20
        exit 1
    fi
}

reconfigure_kubelet() {
    log_step "Reconfiguring kubelet to route through the proxy..."

    # Point kubelet at the proxy (https://127.0.0.1:6444) and trust the proxy's
    # self-signed serving cert. kubelet keeps its client cert for auth (which
    # the proxy ignores at TLS and instead uses the backed-up kubeconfig
    # upstream). Rewrite server + CA in the live kubelet kubeconfig.
    sed -i "s|server: .*|server: https://$PROXY_LISTEN_ADDR|" "$KUBELET_KUBECONFIG"
    if grep -q "certificate-authority-data:" "$KUBELET_KUBECONFIG"; then
        sed -i "s|certificate-authority-data: .*|certificate-authority: $PROXY_CONFIG_DIR/api-server-proxy.crt|" \
            "$KUBELET_KUBECONFIG"
    elif grep -q "certificate-authority:" "$KUBELET_KUBECONFIG"; then
        sed -i "s|certificate-authority: .*|certificate-authority: $PROXY_CONFIG_DIR/api-server-proxy.crt|" \
            "$KUBELET_KUBECONFIG"
    else
        log_error "No certificate-authority field found in $KUBELET_KUBECONFIG"
        exit 1
    fi

    log_info "Restarting kubelet..."
    systemctl restart kubelet
    log_info "kubelet reconfigured to use proxy at https://$PROXY_LISTEN_ADDR"
}

print_success() {
    echo ""
    log_info "=========================================="
    log_info "  api-server-proxy configured (kind)"
    log_info "=========================================="
    echo ""
    echo "Proxy listen:   https://$PROXY_LISTEN_ADDR"
    echo "Upstream:       kubelet's kubeconfig ($KUBELET_KUBECONFIG_BACKUP)"
    echo "Insecure:       $INSECURE"
    echo ""
}

main() {
    parse_args "$@"

    echo ""
    log_info "=========================================="
    log_info "  api-server-proxy Configure (Kind)"
    log_info "=========================================="
    echo ""

    check_prerequisites
    generate_tls_certs
    write_upstream_kubeconfig
    install_signing_cert
    write_service_env
    start_service
    reconfigure_kubelet
    print_success
}

main "$@"
