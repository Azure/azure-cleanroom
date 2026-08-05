#!/bin/bash
#
# Api-Server-Proxy Configuration Script (Boot Phase)
#
# Generates TLS certs, writes upstream kubeconfig, installs the signing cert,
# and starts the service. The binary and systemd unit must already be installed
# by install.sh (image-prep phase).
#
# Usage:
#   sudo ./configure.sh [OPTIONS]
#
# Required:
#   --upstream-api-server URL     Real API server URL (e.g., https://host:443)
#   --upstream-ca-file FILE       Path to the real API server CA cert (PEM)
#   --signing-cert-file FILE      Path to pod policy signing certificate
#
# Optional:
#   --insecure                    Bypass pod policy verification
#   --msi-client-id ID            MSI client ID for proxy-initiated requests
#   --msi-tenant-id ID            MSI tenant ID
#   --help                        Show this help message
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
UPSTREAM_API_SERVER=""
UPSTREAM_CA_FILE=""
SIGNING_CERT_FILE=""
INSECURE=false
MSI_CLIENT_ID=""
MSI_TENANT_ID=""

PROXY_CONFIG_DIR="/etc/api-server-proxy"
PROXY_BIN_PATH="/usr/local/bin/api-server-proxy"

parse_args() {
    while [[ $# -gt 0 ]]; do
        case $1 in
            --upstream-api-server) UPSTREAM_API_SERVER="$2"; shift 2 ;;
            --upstream-ca-file) UPSTREAM_CA_FILE="$2"; shift 2 ;;
            --signing-cert-file) SIGNING_CERT_FILE="$2"; shift 2 ;;
            --insecure) INSECURE=true; shift ;;
            --msi-client-id) MSI_CLIENT_ID="$2"; shift 2 ;;
            --msi-tenant-id) MSI_TENANT_ID="$2"; shift 2 ;;
            --help|-h) head -24 "$0" | grep -E "^#" | sed 's/^# \?//'; exit 0 ;;
            *) log_error "Unknown option: $1"; exit 1 ;;
        esac
    done
}

check_prerequisites() {
    log_step "Checking prerequisites..."
    [[ $EUID -eq 0 ]] || { log_error "Must be run as root"; exit 1; }
    [[ -x "$PROXY_BIN_PATH" ]] || { log_error "Binary not found at $PROXY_BIN_PATH — run install.sh first"; exit 1; }
    [[ -n "$UPSTREAM_API_SERVER" ]] || { log_error "--upstream-api-server is required"; exit 1; }
    [[ -n "$UPSTREAM_CA_FILE" && -f "$UPSTREAM_CA_FILE" ]] || { log_error "--upstream-ca-file is required and must exist"; exit 1; }
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
    log_step "Writing upstream kubeconfig..."

    # Copy the API server CA.
    cp "$UPSTREAM_CA_FILE" "$PROXY_CONFIG_DIR/upstream-ca.pem"

    # Create a kubeconfig pointing to the real API server.
    # If MSI credentials are provided, include the exec credential provider
    # so the proxy can authenticate its own requests (e.g., pod status PATCH).
    if [[ -n "$MSI_CLIENT_ID" && -n "$MSI_TENANT_ID" ]]; then
        cat > "$PROXY_CONFIG_DIR/upstream-kubeconfig" <<EOF
apiVersion: v1
kind: Config
clusters:
- cluster:
    server: $UPSTREAM_API_SERVER
    certificate-authority: $PROXY_CONFIG_DIR/upstream-ca.pem
  name: upstream
contexts:
- context:
    cluster: upstream
    user: proxy
  name: upstream
current-context: upstream
users:
- name: proxy
  user:
    exec:
      apiVersion: client.authentication.k8s.io/v1
      command: /usr/local/bin/aks-flex-node
      args:
      - token
      - kubelogin
      env:
      - name: AAD_LOGIN_METHOD
        value: msi
      - name: AZURE_CLIENT_ID
        value: $MSI_CLIENT_ID
      - name: AZURE_TENANT_ID
        value: $MSI_TENANT_ID
      interactiveMode: Never
      provideClusterInfo: false
EOF
    else
        # No MSI credentials — proxy can only forward kubelet's tokens.
        # Pod status PATCH (initiated by proxy) will fail with 401.
        log_warn "No MSI credentials provided — proxy-initiated requests won't authenticate"
        cat > "$PROXY_CONFIG_DIR/upstream-kubeconfig" <<EOF
apiVersion: v1
kind: Config
clusters:
- cluster:
    server: $UPSTREAM_API_SERVER
    certificate-authority: $PROXY_CONFIG_DIR/upstream-ca.pem
  name: upstream
contexts:
- context:
    cluster: upstream
    user: passthrough
  name: upstream
current-context: upstream
users:
- name: passthrough
  user: {}
EOF
    fi

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

print_success() {
    echo ""
    log_info "=========================================="
    log_info "  api-server-proxy configured"
    log_info "=========================================="
    echo ""
    echo "Upstream:        $UPSTREAM_API_SERVER"
    echo "TLS cert:        $PROXY_CONFIG_DIR/api-server-proxy.crt"
    echo "Insecure:        $INSECURE"
    echo ""
    echo "For flex-node config injection, use:"
    echo "  serverURL:  https://127.0.0.1:6444"
    echo "  caCertData: $(base64 -w0 $PROXY_CONFIG_DIR/api-server-proxy.crt)"
    echo ""
}

main() {
    parse_args "$@"

    echo ""
    log_info "=========================================="
    log_info "  api-server-proxy Configure (Boot)"
    log_info "=========================================="
    echo ""

    check_prerequisites
    generate_tls_certs
    write_upstream_kubeconfig
    install_signing_cert
    write_service_env
    start_service
    print_success
}

main "$@"
