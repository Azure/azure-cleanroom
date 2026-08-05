#!/bin/bash
#
# Shared helpers and configuration for Kind cluster scripts.
# Source this file from other scripts:
#   source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
#

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

log_info() { echo -e "${GREEN}[INFO]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }
log_test() { echo -e "${BLUE}[TEST]${NC} $1"; }

# Shared cluster configuration
CLUSTER_NAME="${CLUSTER_NAME:-api-server-proxy-test}"
KIND_IMAGE="${KIND_IMAGE:-kindest/node:v1.33.0}"
WORKER_NODE_NAME="${CLUSTER_NAME}-worker"
CONTROL_PLANE_NODE_NAME="${CLUSTER_NAME}-control-plane"

# Resolve the k8s-node root directory relative to this script.
K8S_NODE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

# Check that required CLI tools are installed.
check_prerequisites() {
    local tools="kind docker kubectl openssl"
    for tool in $tools; do
        command -v "$tool" >/dev/null 2>&1 || {
            log_error "$tool is required but not installed"
            exit 1
        }
    done
}
