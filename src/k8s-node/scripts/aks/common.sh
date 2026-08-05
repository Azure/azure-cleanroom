#!/bin/bash
#
# Shared helpers and configuration for AKS deployment scripts.
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

# Resolve the k8s-node root directory relative to this script.
K8S_NODE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

# Shared generated files directory (cluster config, VM config, kubeconfig, SSH keys).
SHARED_AKS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SHARED_AKS_GENERATED_DIR="$SHARED_AKS_DIR/generated"
CLUSTER_CONFIG_FILE="$SHARED_AKS_GENERATED_DIR/cluster-config.json"
VM_CONFIG_FILE="$SHARED_AKS_GENERATED_DIR/flex-node-vm-config.json"
AKS_KUBECONFIG_FILE="$SHARED_AKS_GENERATED_DIR/kubeconfig"

# Get currently logged in Azure user info.
# Sets: CURRENT_USER_ID, CURRENT_USER_UPN, USERNAME
get_current_user() {
    log_info "Getting current user information..."

    CURRENT_USER_ID=$(az ad signed-in-user show --query id -o tsv 2>/dev/null) || {
        log_error "Failed to get current user. Make sure you are logged in with 'az login'"
        exit 1
    }

    CURRENT_USER_UPN=$(az ad signed-in-user show --query userPrincipalName -o tsv 2>/dev/null) || {
        log_error "Failed to get current user UPN"
        exit 1
    }

    # Extract username from UPN (before the @).
    USERNAME=$(echo "$CURRENT_USER_UPN" | cut -d'@' -f1 | tr '.' '-' | tr '[:upper:]' '[:lower:]')

    log_info "Current user: $CURRENT_USER_UPN"
    log_info "Username for resources: $USERNAME"
}

# Read cluster config written by deploy-cluster.sh.
# Sets: RESOURCE_GROUP, AKS_CLUSTER_NAME
read_cluster_config() {
    log_info "Reading cluster config from $CLUSTER_CONFIG_FILE..."

    if [[ ! -f "$CLUSTER_CONFIG_FILE" ]]; then
        log_error "Cluster config not found at: $CLUSTER_CONFIG_FILE"
        log_error "Make sure deploy-cluster.sh was run successfully"
        exit 1
    fi

    RESOURCE_GROUP=$(jq -r '.resourceGroup' "$CLUSTER_CONFIG_FILE")
    AKS_CLUSTER_NAME=$(jq -r '.clusterName' "$CLUSTER_CONFIG_FILE")

    if [[ -z "$RESOURCE_GROUP" || "$RESOURCE_GROUP" == "null" ]]; then
        log_error "resourceGroup not found in $CLUSTER_CONFIG_FILE"
        exit 1
    fi
    if [[ -z "$AKS_CLUSTER_NAME" || "$AKS_CLUSTER_NAME" == "null" ]]; then
        log_error "clusterName not found in $CLUSTER_CONFIG_FILE"
        exit 1
    fi

    log_info "Resource group: $RESOURCE_GROUP"
    log_info "AKS cluster: $AKS_CLUSTER_NAME"
}

# Read VM config written by deploy-flex-node-vm.sh.
# Sets: RESOURCE_GROUP, VM_NAME, SSH_PRIVATE_KEY_FILE, VM_PUBLIC_IP
read_vm_config() {
    log_info "Reading VM config from $VM_CONFIG_FILE..."

    if [[ ! -f "$VM_CONFIG_FILE" ]]; then
        log_error "VM config not found at: $VM_CONFIG_FILE"
        log_error "Make sure deploy-flex-node-vm.sh was run successfully"
        exit 1
    fi

    RESOURCE_GROUP=$(jq -r '.resourceGroup' "$VM_CONFIG_FILE")
    VM_NAME=$(jq -r '.vmName' "$VM_CONFIG_FILE")
    SSH_PRIVATE_KEY_FILE=$(jq -r '.sshPrivateKeyFile' "$VM_CONFIG_FILE")

    if [[ -z "$RESOURCE_GROUP" || "$RESOURCE_GROUP" == "null" ]]; then
        log_error "resourceGroup not found in $VM_CONFIG_FILE"
        exit 1
    fi
    if [[ -z "$VM_NAME" || "$VM_NAME" == "null" ]]; then
        log_error "vmName not found in $VM_CONFIG_FILE"
        exit 1
    fi

    log_info "Resource group: $RESOURCE_GROUP"
    log_info "VM name: $VM_NAME"

    # Verify SSH key exists.
    if [[ ! -f "$SSH_PRIVATE_KEY_FILE" ]]; then
        log_error "SSH private key not found at: $SSH_PRIVATE_KEY_FILE"
        log_error "Make sure deploy-flex-node-vm.sh was run successfully"
        exit 1
    fi

    # Get VM public IP.
    VM_PUBLIC_IP=$(az vm show --resource-group "$RESOURCE_GROUP" --name "$VM_NAME" --show-details --query publicIps -o tsv 2>/dev/null) || {
        log_error "Failed to get VM public IP. Make sure the VM exists."
        exit 1
    }

    if [[ -z "$VM_PUBLIC_IP" ]]; then
        log_error "VM public IP is empty"
        exit 1
    fi

    log_info "VM public IP: $VM_PUBLIC_IP"
}
