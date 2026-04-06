#!/bin/bash
#
# Api-Server-Proxy Installation Script
#
# This script installs api-server-proxy on a Kubernetes worker node VM.
# It downloads the binary from GitHub releases, generates TLS certificates,
# and configures the kubelet to route requests through the proxy.
#
# Usage:
#   sudo ./install.sh --signing-cert-url <URL> [OPTIONS]
#
# Required:
#   --signing-cert-url URL    URL to download the signing certificate from
#                             (e.g., https://example.com/signing-cert.pem)
#
# Optional:
#   --config FILE             JSON configuration file with all options
#   --local-binary FILE       Use local binary instead of downloading from GitHub
#   --version VERSION         API server proxy version to install (default: latest)
#   --signing-cert-file FILE  Path to local signing certificate file (instead of URL)
#   --signing-cert-url-ca-cert FILE  CA certificate for verifying signing cert URL (for curl --cacert)
#   --github-repo REPO        GitHub repository (default: azure/azure-cleanroom)
#   --proxy-listen-addr ADDR  Proxy listen address (default: 127.0.0.1:6444)
#   --skip-kubelet-restart    Don't restart kubelet after installation
#   --help                    Show this help message
#
# JSON Config Example:
#   {
#     "signingCertUrl": "https://example.com/signing-cert.pem",
#     "version": "v1.0.0",
#     "githubRepo": "azure/azure-cleanroom",
#     "proxyListenAddr": "127.0.0.1:6444",
#     "skipKubeletRestart": false
#   }
#
# Requirements:
#   - Must be run as root (sudo)
#   - curl, openssl must be installed
#   - systemd-based system
#   - Kubernetes node with kubelet already configured
#

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

log_info() { echo -e "${GREEN}[INFO]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }
log_step() { echo -e "${BLUE}[STEP]${NC} $1"; }

# Default configuration
GITHUB_REPO="azure/azure-cleanroom"
VERSION=""  # Will be determined from GitHub releases if not specified
PROXY_LISTEN_ADDR="127.0.0.1:6444"
PROXY_CERT_DIR="/etc/api-server-proxy"
PROXY_BIN_PATH="/usr/local/bin/api-server-proxy"
SIGNING_CERT_URL=""
SIGNING_CERT_FILE=""
SIGNING_CERT_URL_CA_CERT=""
LOCAL_BINARY=""
SKIP_KUBELET_RESTART=false
CONFIG_FILE=""

# Environment detection
DETECTED_ENV=""  # Will be set to "kind" or "aks-flex"

# Environment-specific paths (will be set based on detected environment)
KUBELET_KUBECONFIG=""
KUBELET_VIA_PROXY_KUBECONFIG=""
API_SERVER_PROXY_KUBECONFIG=""
KUBELET_CONFIG_BACKUP=""
KUBELET_DROPIN_DIR=""
KUBELET_DROPIN_FILE=""

# ============================================================================
# Environment Detection
# ============================================================================

# Detect if running on AKS Flex node
is_aks_flex_node() {
    [[ -f "/usr/local/bin/aks-flex-node" ]]
}

# Detect if running on Kind worker node
is_kind_node() {
    # Kind nodes have specific characteristics:
    # 1. Running inside a Docker container
    # 2. Have /.dockerenv file
    # 3. Have kind-specific paths
    if [[ -f "/.dockerenv" ]] && grep -q "kind" /etc/hostname 2>/dev/null; then
        return 0
    fi
    # Alternative: check for kind cluster label in kubelet args
    if systemctl cat kubelet 2>/dev/null | grep -q "kind"; then
        return 0
    fi
    # Check if kubelet kubeconfig exists at kind's default location
    if [[ -f "/etc/kubernetes/kubelet.conf" ]] && [[ -f "/.dockerenv" ]]; then
        return 0
    fi
    return 1
}

# Detect the environment and set environment-specific variables
detect_environment() {
    log_step "Detecting environment..."

    if is_aks_flex_node; then
        DETECTED_ENV="aks-flex"
        log_info "Detected environment: AKS Flex node"
        
        # AKS Flex specific paths
        KUBELET_KUBECONFIG="/var/lib/kubelet/kubelet/kubeconfig"
        KUBELET_VIA_PROXY_KUBECONFIG="/var/lib/kubelet/kubelet/kubelet-via-proxy.conf"
        API_SERVER_PROXY_KUBECONFIG="${KUBELET_KUBECONFIG}.backup"
        KUBELET_CONFIG_BACKUP="/var/lib/kubelet/kubelet/config.yaml.backup"
        KUBELET_DROPIN_DIR="/etc/systemd/system/kubelet.service.d"
        KUBELET_DROPIN_FILE="$KUBELET_DROPIN_DIR/20-api-server-proxy.conf"
        
    elif is_kind_node; then
        DETECTED_ENV="kind"
        log_info "Detected environment: Kind worker node"
        
        # Kind specific paths
        KUBELET_KUBECONFIG="/etc/kubernetes/kubelet.conf"
        KUBELET_VIA_PROXY_KUBECONFIG="/etc/kubernetes/kubelet-via-proxy.conf"
        API_SERVER_PROXY_KUBECONFIG="$KUBELET_KUBECONFIG"
        KUBELET_CONFIG_BACKUP="/var/lib/kubelet/config.yaml.backup"
        KUBELET_DROPIN_DIR="/etc/systemd/system/kubelet.service.d"
        KUBELET_DROPIN_FILE="$KUBELET_DROPIN_DIR/20-api-server-proxy.conf"
        
    else
        log_error "Unsupported environment"
        log_error "This script supports only:"
        log_error "  - Kind worker nodes"
        log_error "  - AKS Flex nodes"
        exit 1
    fi
}


usage() {
    head -40 "$0" | grep -E "^#" | sed 's/^# \?//'
    exit 0
}

parse_args() {
    while [[ $# -gt 0 ]]; do
        case $1 in
            --config)
                CONFIG_FILE="$2"
                shift 2
                ;;
            --signing-cert-url)
                SIGNING_CERT_URL="$2"
                shift 2
                ;;
            --signing-cert-file)
                SIGNING_CERT_FILE="$2"
                shift 2
                ;;
            --signing-cert-url-ca-cert)
                SIGNING_CERT_URL_CA_CERT="$2"
                shift 2
                ;;
            --local-binary)
                LOCAL_BINARY="$2"
                shift 2
                ;;
            --version)
                VERSION="$2"
                shift 2
                ;;
            --github-repo)
                GITHUB_REPO="$2"
                shift 2
                ;;
            --proxy-listen-addr)
                PROXY_LISTEN_ADDR="$2"
                shift 2
                ;;
            --skip-kubelet-restart)
                SKIP_KUBELET_RESTART=true
                shift
                ;;
            --help|-h)
                usage
                ;;
            *)
                log_error "Unknown option: $1"
                usage
                ;;
        esac
    done
}

load_config() {
    if [[ -z "$CONFIG_FILE" ]]; then
        return
    fi

    log_step "Loading configuration from $CONFIG_FILE..."

    if [[ ! -f "$CONFIG_FILE" ]]; then
        log_error "Configuration file not found: $CONFIG_FILE"
        exit 1
    fi

    # Validate JSON syntax
    if ! jq empty "$CONFIG_FILE" 2>/dev/null; then
        log_error "Invalid JSON in configuration file: $CONFIG_FILE"
        exit 1
    fi

    # Load values from JSON (only if not already set via command line)
    local val

    if [[ -z "$SIGNING_CERT_URL" ]]; then
        val=$(jq -r '.signingCertUrl // empty' "$CONFIG_FILE")
        [[ -n "$val" ]] && SIGNING_CERT_URL="$val"
    fi

    if [[ -z "$SIGNING_CERT_FILE" ]]; then
        val=$(jq -r '.signingCertFile // empty' "$CONFIG_FILE")
        [[ -n "$val" ]] && SIGNING_CERT_FILE="$val"
    fi

    if [[ -z "$SIGNING_CERT_URL_CA_CERT" ]]; then
        val=$(jq -r '.signingCertUrlCaCert // empty' "$CONFIG_FILE")
        [[ -n "$val" ]] && SIGNING_CERT_URL_CA_CERT="$val"
    fi

    if [[ -z "$VERSION" ]]; then
        val=$(jq -r '.version // empty' "$CONFIG_FILE")
        [[ -n "$val" ]] && VERSION="$val"
    fi

    if [[ "$GITHUB_REPO" == "azure/azure-cleanroom" ]]; then
        val=$(jq -r '.githubRepo // empty' "$CONFIG_FILE")
        [[ -n "$val" ]] && GITHUB_REPO="$val"
    fi

    if [[ "$PROXY_LISTEN_ADDR" == "127.0.0.1:6444" ]]; then
        val=$(jq -r '.proxyListenAddr // empty' "$CONFIG_FILE")
        [[ -n "$val" ]] && PROXY_LISTEN_ADDR="$val"
    fi

    if [[ "$SKIP_KUBELET_RESTART" == "false" ]]; then
        val=$(jq -r '.skipKubeletRestart // empty' "$CONFIG_FILE")
        [[ "$val" == "true" ]] && SKIP_KUBELET_RESTART=true
    fi

    if [[ -z "$LOCAL_BINARY" ]]; then
        val=$(jq -r '.localBinary // empty' "$CONFIG_FILE")
        [[ -n "$val" ]] && LOCAL_BINARY="$val"
    fi

    log_info "Configuration loaded from $CONFIG_FILE"
}

check_prerequisites() {
    log_step "Checking prerequisites..."

    # Check if running as root
    if [[ $EUID -ne 0 ]]; then
        log_error "This script must be run as root (use sudo)"
        exit 1
    fi

    # Check required commands
    local required_cmds="curl openssl systemctl"
    if [[ -n "$CONFIG_FILE" ]]; then
        required_cmds="$required_cmds jq"
    fi
    for cmd in $required_cmds; do
        if ! command -v "$cmd" &>/dev/null; then
            log_error "Required command not found: $cmd"
            exit 1
        fi
    done

    # Wait for kubelet kubeconfig to appear (may still be bootstrapping).
    local retries=12
    while [[ ! -f "$KUBELET_KUBECONFIG" ]] && (( retries > 0 )); do
        log_info "Waiting for $KUBELET_KUBECONFIG to appear (${retries} retries left)..."
        sleep 5
        (( retries-- ))
    done

    if [[ ! -f "$KUBELET_KUBECONFIG" ]]; then
        log_error "Kubelet kubeconfig not found at $KUBELET_KUBECONFIG"
        log_error "Is this a Kubernetes node with kubelet configured?"
        exit 1
    fi

    # Check signing cert source
    if [[ -z "$SIGNING_CERT_URL" && -z "$SIGNING_CERT_FILE" ]]; then
        log_error "Either --signing-cert-url or --signing-cert-file is required"
        exit 1
    fi

    if [[ -n "$SIGNING_CERT_FILE" && ! -f "$SIGNING_CERT_FILE" ]]; then
        log_error "Signing certificate file not found: $SIGNING_CERT_FILE"
        exit 1
    fi

    if [[ -n "$LOCAL_BINARY" && ! -f "$LOCAL_BINARY" ]]; then
        log_error "Local binary not found: $LOCAL_BINARY"
        exit 1
    fi

    if [[ -n "$SIGNING_CERT_URL_CA_CERT" && ! -f "$SIGNING_CERT_URL_CA_CERT" ]]; then
        log_error "CA certificate file not found: $SIGNING_CERT_URL_CA_CERT"
        exit 1
    fi

    log_info "Prerequisites check passed"
}

detect_architecture() {
    local arch
    arch=$(uname -m)
    case $arch in
        x86_64)
            echo "amd64"
            ;;
        aarch64|arm64)
            echo "arm64"
            ;;
        *)
            log_error "Unsupported architecture: $arch"
            exit 1
            ;;
    esac
}

get_latest_version() {
    local latest
    latest=$(curl -sf "https://api.github.com/repos/$GITHUB_REPO/releases/latest" | grep '"tag_name"' | sed -E 's/.*"([^"]+)".*/\1/')
    if [[ -z "$latest" ]]; then
        log_warn "Could not fetch latest version from GitHub releases"
        echo ""
    else
        echo "$latest"
    fi
}

resolve_version() {
    # Skip version resolution if using local binary
    if [[ -n "$LOCAL_BINARY" ]]; then
        VERSION="local"
        return
    fi

    if [[ -n "$VERSION" ]]; then
        log_info "Using specified version: $VERSION"
        return
    fi

    log_step "Determining latest version from GitHub releases..."
    VERSION=$(get_latest_version)

    if [[ -z "$VERSION" ]]; then
        log_error "Could not determine version. Please specify with --version"
        exit 1
    fi

    log_info "Latest release version: $VERSION"
}

download_binary() {
    log_step "Installing api-server-proxy binary..."

    # Use local binary if specified
    if [[ -n "$LOCAL_BINARY" ]]; then
        log_info "Using local binary: $LOCAL_BINARY"
        cp "$LOCAL_BINARY" "$PROXY_BIN_PATH"
        chmod +x "$PROXY_BIN_PATH"
        log_info "Binary installed to $PROXY_BIN_PATH"
        log_info "Version: $($PROXY_BIN_PATH --version 2>/dev/null || echo 'unknown')"
        return
    fi

    local arch
    arch=$(detect_architecture)

    local tarball_name="api-server-proxy-linux-${arch}.tar.gz"
    local download_url="https://github.com/$GITHUB_REPO/releases/download/$VERSION/$tarball_name"

    log_info "Downloading from: $download_url"

    local tmp_dir="/tmp/api-server-proxy-download-$$"
    mkdir -p "$tmp_dir"

    local tmp_tarball="$tmp_dir/$tarball_name"
    if ! curl -sfL "$download_url" -o "$tmp_tarball"; then
        rm -rf "$tmp_dir"
        log_error "Failed to download release from $download_url"
        exit 1
    fi

    # Extract the tarball
    log_info "Extracting archive..."
    if ! tar -xzf "$tmp_tarball" -C "$tmp_dir"; then
        rm -rf "$tmp_dir"
        log_error "Failed to extract tarball"
        exit 1
    fi

    # Find and install the binary
    local binary_path="$tmp_dir/api-server-proxy"
    if [[ ! -f "$binary_path" ]]; then
        # Try looking in a subdirectory
        binary_path=$(find "$tmp_dir" -name "api-server-proxy" -type f | head -1)
    fi

    if [[ -z "$binary_path" || ! -f "$binary_path" ]]; then
        rm -rf "$tmp_dir"
        log_error "Could not find api-server-proxy binary in archive"
        exit 1
    fi

    chmod +x "$binary_path"
    mv "$binary_path" "$PROXY_BIN_PATH"

    # Cleanup
    rm -rf "$tmp_dir"

    log_info "Binary installed to $PROXY_BIN_PATH"
    log_info "Version: $($PROXY_BIN_PATH --version 2>/dev/null || echo 'unknown')"
}

generate_tls_certs() {
    log_step "Generating TLS certificates for api-server-proxy..."

    mkdir -p "$PROXY_CERT_DIR"
    chmod 700 "$PROXY_CERT_DIR"

    local hostname
    hostname=$(hostname)

    # Generate self-signed certificate for api-server-proxy server
    openssl req -x509 -nodes -days 365 -newkey rsa:2048 \
        -keyout "$PROXY_CERT_DIR/api-server-proxy.key" \
        -out "$PROXY_CERT_DIR/api-server-proxy.crt" \
        -subj "/CN=api-server-proxy/O=api-server-proxy" \
        -addext "subjectAltName=IP:127.0.0.1,DNS:localhost,DNS:api-server-proxy,DNS:$hostname" \
        2>/dev/null

    chmod 600 "$PROXY_CERT_DIR/api-server-proxy.key"
    chmod 644 "$PROXY_CERT_DIR/api-server-proxy.crt"

    log_info "TLS certificates generated in $PROXY_CERT_DIR"
}

fetch_signing_cert() {
    log_step "Fetching signing certificate..."

    if [[ -n "$SIGNING_CERT_FILE" ]]; then
        log_info "Copying signing certificate from $SIGNING_CERT_FILE"
        cp "$SIGNING_CERT_FILE" "$PROXY_CERT_DIR/signing-cert.pem"
    else
        log_info "Downloading signing certificate from $SIGNING_CERT_URL"
        local curl_opts="-sf"
        if [[ -n "$SIGNING_CERT_URL_CA_CERT" ]]; then
            log_info "Using CA certificate: $SIGNING_CERT_URL_CA_CERT"
            curl_opts="$curl_opts --cacert $SIGNING_CERT_URL_CA_CERT"
        fi
        if ! curl $curl_opts "$SIGNING_CERT_URL" -o "$PROXY_CERT_DIR/signing-cert.pem"; then
            log_error "Failed to download signing certificate"
            exit 1
        fi
    fi

    if [[ ! -s "$PROXY_CERT_DIR/signing-cert.pem" ]]; then
        log_error "Signing certificate is empty"
        exit 1
    fi

    chmod 644 "$PROXY_CERT_DIR/signing-cert.pem"
    log_info "Signing certificate saved to $PROXY_CERT_DIR/signing-cert.pem"
}

create_proxy_kubeconfig() {
    log_step "Creating proxy kubeconfig for kubelet..."

    case "$DETECTED_ENV" in
        kind)
            create_proxy_kubeconfig_kind
            ;;
        aks-flex)
            create_proxy_kubeconfig_aks_flex
            ;;
        *)
            log_error "Unknown environment: $DETECTED_ENV"
            exit 1
            ;;
    esac

    chmod 600 "$KUBELET_VIA_PROXY_KUBECONFIG"
    log_info "Proxy kubeconfig created at $KUBELET_VIA_PROXY_KUBECONFIG"
}

# Create proxy kubeconfig for Kind environment (uses client certificates)
create_proxy_kubeconfig_kind() {
    # Extract client certificate and key paths from original kubeconfig
    local client_cert
    local client_key

    # Try to get the paths from kubeconfig
    if command -v kubectl &>/dev/null; then
        client_cert=$(kubectl config view --kubeconfig="$KUBELET_KUBECONFIG" -o jsonpath='{.users[0].user.client-certificate}' --raw 2>/dev/null || true)
        client_key=$(kubectl config view --kubeconfig="$KUBELET_KUBECONFIG" -o jsonpath='{.users[0].user.client-key}' --raw 2>/dev/null || true)
    fi

    # Fallback: parse kubeconfig directly
    if [[ -z "$client_cert" ]]; then
        client_cert=$(grep 'client-certificate:' "$KUBELET_KUBECONFIG" | head -1 | awk '{print $2}')
    fi
    if [[ -z "$client_key" ]]; then
        client_key=$(grep 'client-key:' "$KUBELET_KUBECONFIG" | head -1 | awk '{print $2}')
    fi

    if [[ -z "$client_cert" || -z "$client_key" ]]; then
        log_error "Could not extract client certificate/key from kubelet kubeconfig"
        log_error "Please check $KUBELET_KUBECONFIG"
        exit 1
    fi

    # Create new kubeconfig pointing to proxy with client certificate auth
    cat > "$KUBELET_VIA_PROXY_KUBECONFIG" <<EOF
apiVersion: v1
kind: Config
clusters:
- cluster:
    certificate-authority: $PROXY_CERT_DIR/api-server-proxy.crt
    server: https://$PROXY_LISTEN_ADDR
  name: proxy
contexts:
- context:
    cluster: proxy
    user: kubelet
  name: proxy
current-context: proxy
users:
- name: kubelet
  user:
    client-certificate: ${client_cert}
    client-key: ${client_key}
EOF
}

# Create proxy kubeconfig for AKS Flex environment (uses exec credential provider)
create_proxy_kubeconfig_aks_flex() {
    # Copy the original kubeconfig and modify only the cluster server and CA.
    # This preserves the full exec credential configuration (command, args, env, etc.)
    # which is required for the kubelet to authenticate with the API server.
    cp "$KUBELET_KUBECONFIG" "$KUBELET_VIA_PROXY_KUBECONFIG"
    sed -i "s|server: .*|server: https://$PROXY_LISTEN_ADDR|" "$KUBELET_VIA_PROXY_KUBECONFIG"
    sed -i "s|certificate-authority-data: .*|certificate-authority: $PROXY_CERT_DIR/api-server-proxy.crt|" "$KUBELET_VIA_PROXY_KUBECONFIG"
}

create_systemd_service() {
    log_step "Creating systemd service for api-server-proxy..."

    cat > /etc/systemd/system/api-server-proxy.service <<EOF
[Unit]
Description=Kubelet Proxy - Pod Admission Control
Documentation=https://github.com/$GITHUB_REPO
Before=kubelet.service
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=$PROXY_BIN_PATH \\
    --kubeconfig $API_SERVER_PROXY_KUBECONFIG \\
    --listen-addr $PROXY_LISTEN_ADDR \\
    --tls-cert $PROXY_CERT_DIR/api-server-proxy.crt \\
    --tls-key $PROXY_CERT_DIR/api-server-proxy.key \\
    --policy-verification-cert $PROXY_CERT_DIR/signing-cert.pem \\
    --log-requests=true \\
    --log-pod-payloads=false

Restart=always
RestartSec=5
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF

    log_info "Systemd service file created"
}

configure_kubelet() {
    log_step "Configuring kubelet to use proxy..."

    case "$DETECTED_ENV" in
        kind)
            configure_kubelet_kind
            ;;
        aks-flex)
            configure_kubelet_aks_flex
            ;;
        *)
            log_error "Unknown environment: $DETECTED_ENV"
            exit 1
            ;;
    esac
}

# Configure kubelet for Kind environment
configure_kubelet_kind() {
    # Backup original kubelet config if not already backed up
    if [[ -f /var/lib/kubelet/config.yaml && ! -f "$KUBELET_CONFIG_BACKUP" ]]; then
        cp /var/lib/kubelet/config.yaml "$KUBELET_CONFIG_BACKUP"
        log_info "Original kubelet config backed up to $KUBELET_CONFIG_BACKUP"
    fi

    # Create kubelet drop-in directory
    mkdir -p "$KUBELET_DROPIN_DIR"

    # Create drop-in to override kubeconfig
    cat > "$KUBELET_DROPIN_FILE" <<EOF
[Service]
Environment="KUBELET_KUBECONFIG_ARGS=--kubeconfig=$KUBELET_VIA_PROXY_KUBECONFIG --bootstrap-kubeconfig="
EOF

    log_info "Kubelet drop-in created at $KUBELET_DROPIN_FILE"
}

# Configure kubelet for AKS Flex environment
configure_kubelet_aks_flex() {
    # Stop and disable aks-flex-node-agent to prevent it from overwriting the
    # kubeconfig file that we are about to replace with the proxy kubeconfig.
    if systemctl is-active --quiet aks-flex-node-agent 2>/dev/null; then
        log_info "Stopping aks-flex-node-agent service..."
        systemctl stop aks-flex-node-agent
    fi
    if systemctl is-enabled --quiet aks-flex-node-agent 2>/dev/null; then
        log_info "Disabling aks-flex-node-agent service..."
        systemctl disable aks-flex-node-agent
    fi

    # Backup original kubelet config if not already backed up
    if [[ -f /var/lib/kubelet/config.yaml && ! -f "$KUBELET_CONFIG_BACKUP" ]]; then
        cp /var/lib/kubelet/config.yaml "$KUBELET_CONFIG_BACKUP"
        log_info "Original kubelet config backed up to $KUBELET_CONFIG_BACKUP"
    fi

    # Backup original kubelet kubeconfig
    local kubeconfig_backup="${KUBELET_KUBECONFIG}.backup"
    if [[ ! -f "$kubeconfig_backup" ]]; then
        cp "$KUBELET_KUBECONFIG" "$kubeconfig_backup"
        log_info "Original kubelet kubeconfig backed up to $kubeconfig_backup"
    fi

    # Replace the kubelet kubeconfig with the proxy kubeconfig.
    # The AKS Flex kubelet unit file hardcodes --kubeconfig so drop-in environment
    # variables are not picked up. Overwriting the file directly is the only way.
    log_info "Replacing $KUBELET_KUBECONFIG with proxy kubeconfig..."
    cp "$KUBELET_VIA_PROXY_KUBECONFIG" "$KUBELET_KUBECONFIG"
    chmod 600 "$KUBELET_KUBECONFIG"
    log_info "Kubelet kubeconfig replaced with proxy kubeconfig"
}

start_services() {
    log_step "Starting api-server-proxy and restarting kubelet..."

    # Reload systemd
    systemctl daemon-reload

    # Enable and start api-server-proxy
    systemctl enable api-server-proxy
    systemctl start api-server-proxy

    # Wait for proxy to be ready
    log_info "Waiting for api-server-proxy to start..."
    sleep 3

    if systemctl is-active --quiet api-server-proxy; then
        log_info "api-server-proxy is running"
    else
        log_error "api-server-proxy failed to start"
        journalctl -u api-server-proxy --no-pager -n 20
        exit 1
    fi

    # Restart kubelet if not skipped
    if [[ "$SKIP_KUBELET_RESTART" == "false" ]]; then
        log_info "Restarting kubelet..."
        systemctl restart kubelet

        sleep 5

        if systemctl is-active --quiet kubelet; then
            log_info "kubelet is running with proxy"
        else
            log_warn "kubelet may have issues after restart"
            systemctl status kubelet --no-pager || true
        fi
    else
        log_warn "Skipping kubelet restart (--skip-kubelet-restart specified)"
        log_warn "You must manually restart kubelet for changes to take effect:"
        log_warn "  sudo systemctl daemon-reload && sudo systemctl restart kubelet"
    fi
}

verify_installation() {
    log_step "Verifying installation..."

    echo ""
    echo "=== api-server-proxy status ==="
    systemctl status api-server-proxy --no-pager || true

    echo ""
    echo "=== Recent api-server-proxy logs ==="
    journalctl -u api-server-proxy --no-pager -n 10 || true

    echo ""
    echo "=== kubelet status ==="
    systemctl status kubelet --no-pager | head -15 || true
}

print_success() {
    echo ""
    log_info "=========================================="
    log_info "  api-server-proxy installed successfully!"
    log_info "=========================================="
    echo ""
    echo "Configuration:"
    echo "  Environment:      $DETECTED_ENV"
    echo "  Version:          $VERSION"
    echo "  Binary:           $PROXY_BIN_PATH"
    echo "  Config directory: $PROXY_CERT_DIR"
    echo "  Listen address:   $PROXY_LISTEN_ADDR"
    echo "  Proxy kubeconfig: $KUBELET_VIA_PROXY_KUBECONFIG"
    echo ""
    echo "Useful commands:"
    echo "  View proxy logs:    journalctl -u api-server-proxy -f"
    echo "  View kubelet logs:  journalctl -u kubelet -f"
    echo "  Restart proxy:      sudo systemctl restart api-server-proxy"
    echo "  Uninstall:          sudo ./uninstall.sh"
    echo ""
    echo "Pod policy verification is now ENABLED."
    echo "Unsigned pods scheduled to this node will be rejected."
    echo ""
}

main() {
    parse_args "$@"
    load_config

    echo ""
    log_info "=========================================="
    log_info "  api-server-proxy Installation Script"
    log_info "=========================================="
    echo ""

    detect_environment
    check_prerequisites
    resolve_version
    download_binary
    generate_tls_certs
    fetch_signing_cert
    create_proxy_kubeconfig
    create_systemd_service
    configure_kubelet
    start_services
    verify_installation
    print_success
}

main "$@"
