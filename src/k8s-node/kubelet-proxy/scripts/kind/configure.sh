#!/bin/bash
#
# Kubelet-Proxy Configuration Script (Kind Environment)
#
# Configures and starts the kubelet-proxy on a Kind worker node. Unlike the AKS
# flow, kubelet is already running and uses /var/lib/kubelet/config.yaml for its
# port. This script:
#   1. Generates a self-signed serving cert for the proxy.
#   2. Installs the real apiserver-kubelet-client cert (passed in) so kubelet
#      accepts the proxy's connection (kubelet trusts the cluster CA).
#   3. Moves kubelet to port 10251 (via config.yaml) and restarts it, freeing
#      :10250 for the proxy.
#
# The binary and systemd unit must already be installed by install.sh.
#
# Usage:
#   sudo ./configure.sh --client-cert <crt> --client-key <key> \
#     [--api-policy-file <path>]
#
# Options:
#   --client-cert FILE          Client cert for connecting to kubelet (required)
#   --client-key FILE           Client key for connecting to kubelet (required)
#   --api-policy-file FILE       Path to API policy JSON file
#   --kubelet-port PORT          Port to move kubelet to (default: 10251)
#   --help                       Show this help message
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

API_POLICY_FILE=""
CLIENT_CERT_FILE=""
CLIENT_KEY_FILE=""
KUBELET_PORT=10251

PROXY_CONFIG_DIR="/etc/kubelet-proxy"
PROXY_BIN_PATH="/usr/local/bin/kubelet-proxy"
KUBELET_CONFIG="/var/lib/kubelet/config.yaml"
KUBELET_CONFIG_BACKUP="/var/lib/kubelet/config.yaml.kubelet-proxy-backup"

parse_args() {
    while [[ $# -gt 0 ]]; do
        case $1 in
            --client-cert) CLIENT_CERT_FILE="$2"; shift 2 ;;
            --client-key) CLIENT_KEY_FILE="$2"; shift 2 ;;
            --api-policy-file) API_POLICY_FILE="$2"; shift 2 ;;
            --kubelet-port) KUBELET_PORT="$2"; shift 2 ;;
            --help|-h) head -25 "$0" | grep -E "^#" | sed 's/^# \?//'; exit 0 ;;
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
    [[ -n "$CLIENT_CERT_FILE" && -f "$CLIENT_CERT_FILE" ]] || {
        log_error "--client-cert is required and must exist"; exit 1
    }
    [[ -n "$CLIENT_KEY_FILE" && -f "$CLIENT_KEY_FILE" ]] || {
        log_error "--client-key is required and must exist"; exit 1
    }
    [[ -f "$KUBELET_CONFIG" ]] || {
        log_error "kubelet config not found at $KUBELET_CONFIG"; exit 1
    }
    command -v openssl &>/dev/null || { log_error "openssl is required"; exit 1; }
    log_info "Prerequisites OK"
}

generate_tls_certs() {
    log_step "Generating server TLS certificate..."
    mkdir -p "$PROXY_CONFIG_DIR"
    chmod 700 "$PROXY_CONFIG_DIR"

    local hostname
    hostname=$(hostname)
    local node_ip
    node_ip=$(hostname -I | awk '{print $1}')

    local san="IP:127.0.0.1,DNS:localhost,DNS:$hostname"
    [[ -n "$node_ip" ]] && san="$san,IP:$node_ip"

    openssl req -x509 -nodes -days 365 -newkey rsa:2048 \
        -keyout "$PROXY_CONFIG_DIR/server.key" \
        -out "$PROXY_CONFIG_DIR/server.crt" \
        -subj "/CN=kubelet-proxy/O=cleanroom" \
        -addext "subjectAltName=$san" \
        2>/dev/null

    chmod 600 "$PROXY_CONFIG_DIR/server.key"
    chmod 644 "$PROXY_CONFIG_DIR/server.crt"
    log_info "Server cert: $PROXY_CONFIG_DIR/server.crt"
}

install_client_cert() {
    log_step "Installing kubelet client certificate..."
    # In Kind, kubelet's --client-ca-file is the cluster CA. The provided cert
    # (apiserver-kubelet-client) is signed by that CA, so kubelet accepts it.
    cp "$CLIENT_CERT_FILE" "$PROXY_CONFIG_DIR/client.crt"
    cp "$CLIENT_KEY_FILE" "$PROXY_CONFIG_DIR/client.key"
    chmod 644 "$PROXY_CONFIG_DIR/client.crt"
    chmod 600 "$PROXY_CONFIG_DIR/client.key"
    log_info "Client cert: $PROXY_CONFIG_DIR/client.crt"
}

install_api_policy() {
    if [[ -n "$API_POLICY_FILE" ]]; then
        log_step "Installing API policy..."
        [[ -f "$API_POLICY_FILE" ]] || {
            log_error "API policy file not found: $API_POLICY_FILE"; exit 1
        }
        cp "$API_POLICY_FILE" "$PROXY_CONFIG_DIR/api-policy.json"
        chmod 644 "$PROXY_CONFIG_DIR/api-policy.json"
        log_info "API policy: $PROXY_CONFIG_DIR/api-policy.json"
    else
        log_warn "No API policy file — proxy will deny all inbound requests"
    fi
}

configure_kubelet_port() {
    log_step "Moving kubelet to port $KUBELET_PORT..."

    if [[ ! -f "$KUBELET_CONFIG_BACKUP" ]]; then
        cp "$KUBELET_CONFIG" "$KUBELET_CONFIG_BACKUP"
        log_info "Backed up kubelet config to $KUBELET_CONFIG_BACKUP"
    fi

    if grep -q "^port:" "$KUBELET_CONFIG"; then
        sed -i "s/^port:.*/port: $KUBELET_PORT/" "$KUBELET_CONFIG"
    else
        echo "port: $KUBELET_PORT" >> "$KUBELET_CONFIG"
    fi
    log_info "kubelet config updated: port=$KUBELET_PORT"

    log_info "Restarting kubelet..."
    systemctl restart kubelet
}

start_service() {
    log_step "Starting kubelet-proxy..."
    systemctl daemon-reload
    systemctl enable kubelet-proxy
    systemctl start kubelet-proxy

    sleep 3
    if systemctl is-active --quiet kubelet-proxy; then
        log_info "kubelet-proxy is running"
    else
        log_error "kubelet-proxy failed to start"
        journalctl -u kubelet-proxy --no-pager -n 20
        exit 1
    fi
}

print_success() {
    echo ""
    log_info "=========================================="
    log_info "  kubelet-proxy configured (kind)"
    log_info "=========================================="
    echo ""
    echo "Proxy listen:   :10250"
    echo "Kubelet port:   :$KUBELET_PORT"
    echo "TLS cert:       $PROXY_CONFIG_DIR/server.crt"
    echo "API policy:     ${API_POLICY_FILE:-none (deny-all)}"
    echo ""
}

main() {
    parse_args "$@"

    echo ""
    log_info "=========================================="
    log_info "  kubelet-proxy Configure (Kind)"
    log_info "=========================================="
    echo ""

    check_prerequisites
    generate_tls_certs
    install_client_cert
    install_api_policy
    configure_kubelet_port
    start_service
    print_success
}

main "$@"
