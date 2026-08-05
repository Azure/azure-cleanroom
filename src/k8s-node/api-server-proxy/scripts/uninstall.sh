#!/bin/bash
#
# Api-Server-Proxy Uninstall Script
#
# Stops the service, removes the binary, config directory, and systemd unit.
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

PROXY_BIN_PATH="/usr/local/bin/api-server-proxy"
PROXY_CONFIG_DIR="/etc/api-server-proxy"
SERVICE_NAME="api-server-proxy"

main() {
    [[ $EUID -eq 0 ]] || { echo -e "${RED}[ERROR]${NC} Must be run as root"; exit 1; }

    log_step "Stopping and disabling service..."
    systemctl stop "$SERVICE_NAME" 2>/dev/null || true
    systemctl disable "$SERVICE_NAME" 2>/dev/null || true

    log_step "Removing systemd unit..."
    rm -f "/etc/systemd/system/${SERVICE_NAME}.service"
    systemctl daemon-reload

    log_step "Removing binary..."
    rm -f "$PROXY_BIN_PATH"

    log_step "Removing config directory..."
    rm -rf "$PROXY_CONFIG_DIR"

    log_info "api-server-proxy uninstalled"
}

main "$@"
