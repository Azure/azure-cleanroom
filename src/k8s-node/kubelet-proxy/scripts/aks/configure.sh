#!/bin/bash
#
# Kubelet-Proxy Configuration Script (Boot Phase)
#
# Generates TLS certs, installs the API policy, and starts the service.
# The binary, systemd unit, and kubelet port drop-in must already be installed
# by install.sh (image-prep phase).
#
# Usage:
#   sudo ./configure.sh [OPTIONS]
#
# Optional:
#   --api-policy-file FILE      Path to API policy JSON file
#   --ca-cert-file FILE         CA cert used to sign the kubelet client cert
#                               (must match kubelet's --client-ca-file)
#   --ca-key-file FILE          CA private key matching --ca-cert-file
#   --help                      Show this help message
#

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

log_info() { echo -e "${GREEN}[INFO]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }
log_step() { echo -e "${BLUE}[STEP]${NC} $1"; }

# Defaults
API_POLICY_FILE=""
CA_CERT_FILE=""
CA_KEY_FILE=""

PROXY_CONFIG_DIR="/etc/kubelet-proxy"
PROXY_BIN_PATH="/usr/local/bin/kubelet-proxy"

parse_args() {
    while [[ $# -gt 0 ]]; do
        case $1 in
            --api-policy-file) API_POLICY_FILE="$2"; shift 2 ;;
            --ca-cert-file) CA_CERT_FILE="$2"; shift 2 ;;
            --ca-key-file) CA_KEY_FILE="$2"; shift 2 ;;
            --help|-h) head -19 "$0" | grep -E "^#" | sed 's/^# \?//'; exit 0 ;;
            *) log_error "Unknown option: $1"; exit 1 ;;
        esac
    done
}

check_prerequisites() {
    log_step "Checking prerequisites..."
    [[ $EUID -eq 0 ]] || { log_error "Must be run as root"; exit 1; }
    [[ -x "$PROXY_BIN_PATH" ]] || { log_error "Binary not found at $PROXY_BIN_PATH — run install.sh first"; exit 1; }
    command -v openssl &>/dev/null || { log_error "openssl is required"; exit 1; }
    log_info "Prerequisites OK"
}

generate_tls_certs() {
    log_step "Generating TLS certificates..."
    mkdir -p "$PROXY_CONFIG_DIR"
    chmod 700 "$PROXY_CONFIG_DIR"

    local hostname
    hostname=$(hostname)
    local node_ip
    node_ip=$(hostname -I | awk '{print $1}')

    # Server cert: presented to incoming TLS connections (from API server/konnectivity).
    # Include the node IP in SANs so konnectivity agents that verify the cert
    # (connecting to node-IP:10250) accept it.
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

    # Client cert: used by the proxy to connect to kubelet.
    # Kubelet verifies client certs against its --client-ca-file which is set
    # to the api-server-proxy's CA cert. So we sign the client cert with that CA,
    # supplied via --ca-cert-file / --ca-key-file.
    if [[ -n "$CA_CERT_FILE" && -n "$CA_KEY_FILE" \
          && -f "$CA_CERT_FILE" && -f "$CA_KEY_FILE" ]]; then
        # Generate CSR and sign with the supplied CA.
        openssl req -nodes -newkey rsa:2048 \
            -keyout "$PROXY_CONFIG_DIR/client.key" \
            -out "$PROXY_CONFIG_DIR/client.csr" \
            -subj "/CN=kubelet-proxy-client/O=system:masters" \
            2>/dev/null

        openssl x509 -req -days 365 \
            -in "$PROXY_CONFIG_DIR/client.csr" \
            -CA "$CA_CERT_FILE" \
            -CAkey "$CA_KEY_FILE" \
            -CAcreateserial \
            -out "$PROXY_CONFIG_DIR/client.crt" \
            2>/dev/null

        rm -f "$PROXY_CONFIG_DIR/client.csr"
        log_info "Client cert signed by supplied CA ($CA_CERT_FILE)"
    else
        # Fallback: self-signed (won't pass kubelet client auth).
        log_warn "CA cert/key not supplied, using self-signed client cert"
        openssl req -x509 -nodes -days 365 -newkey rsa:2048 \
            -keyout "$PROXY_CONFIG_DIR/client.key" \
            -out "$PROXY_CONFIG_DIR/client.crt" \
            -subj "/CN=kubelet-proxy-client/O=system:masters" \
            2>/dev/null
    fi

    chmod 600 "$PROXY_CONFIG_DIR/client.key"
    chmod 644 "$PROXY_CONFIG_DIR/client.crt"
    log_info "Client cert: $PROXY_CONFIG_DIR/client.crt"
}

install_api_policy() {
    if [[ -n "$API_POLICY_FILE" ]]; then
        log_step "Installing API policy..."
        if [[ ! -f "$API_POLICY_FILE" ]]; then
            log_error "API policy file not found: $API_POLICY_FILE"
            exit 1
        fi
        cp "$API_POLICY_FILE" "$PROXY_CONFIG_DIR/api-policy.json"
        chmod 644 "$PROXY_CONFIG_DIR/api-policy.json"
        log_info "API policy: $PROXY_CONFIG_DIR/api-policy.json"
    else
        log_warn "No API policy file — proxy will deny all inbound requests"
    fi
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
    log_info "  kubelet-proxy configured"
    log_info "=========================================="
    echo ""
    echo "TLS cert:       $PROXY_CONFIG_DIR/server.crt"
    echo "API policy:     ${API_POLICY_FILE:-none (deny-all)}"
    echo ""
}

main() {
    parse_args "$@"

    echo ""
    log_info "=========================================="
    log_info "  kubelet-proxy Configure (Boot)"
    log_info "=========================================="
    echo ""

    check_prerequisites
    generate_tls_certs
    install_api_policy
    start_service
    print_success
}

main "$@"
