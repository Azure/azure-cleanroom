#!/bin/bash
#
# Kubelet-Proxy Uninstall Script
#
# Stops the service, removes the binary, config directory, systemd unit,
# and the kubelet port drop-in.
#
# Usage:
#   sudo ./uninstall.sh
#

set -e

RED='\033[0;31m'
GREEN='\033[0;32m'
BLUE='\033[0;34m'
NC='\033[0m'

log_info() { echo -e "${GREEN}[INFO]${NC} $1"; }
log_step() { echo -e "${BLUE}[STEP]${NC} $1"; }

PROXY_BIN_PATH="/usr/local/bin/kubelet-proxy"
PROXY_CONFIG_DIR="/etc/kubelet-proxy"
SERVICE_NAME="kubelet-proxy"
KUBELET_DROPIN="/etc/systemd/system/kubelet.service.d/30-kubelet-proxy-node-config.conf"

main() {
    [[ $EUID -eq 0 ]] || { echo -e "${RED}[ERROR]${NC} Must be run as root"; exit 1; }

    log_step "Stopping and disabling service..."
    systemctl stop "$SERVICE_NAME" 2>/dev/null || true
    systemctl disable "$SERVICE_NAME" 2>/dev/null || true

    log_step "Removing systemd unit..."
    rm -f "/etc/systemd/system/${SERVICE_NAME}.service"

    log_step "Removing kubelet port drop-in..."
    rm -f "$KUBELET_DROPIN"

    systemctl daemon-reload

    log_step "Removing binary..."
    rm -f "$PROXY_BIN_PATH"

    log_step "Removing config directory..."
    rm -rf "$PROXY_CONFIG_DIR"

    log_info "kubelet-proxy uninstalled"
}

main "$@"
