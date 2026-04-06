#!/bin/bash
#
# Deploy AKS Cluster for api-server-proxy testing
#
# This script creates an Azure resource group and AKS cluster for testing
# api-server-proxy with signed pod policies.
#
# Usage:
#   ./deploy-cluster.sh [options]
#
# Options:
#   --help, -h                   Show this help message
#   --resource-group <name>      Resource group name (default: <username>-flex-test-rg)
#   --cluster-name <name>        AKS cluster name (default: <username>-flex-aks)
#   --location <region>          Azure region (default: centralindia)
#
# Environment Variables:
#   KUBERNETES_VERSION    AKS Kubernetes version (default: AKS default)
#   AKS_NODE_COUNT        AKS node count (default: AKS default)
#   AKS_NODE_VM_SIZE      AKS node VM size (default: Standard_D4ds_v5)
#
# Prerequisites:
#   - Must be logged in to Azure (az login)
#

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

log_info() { echo -e "${GREEN}[INFO]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }

# Configuration
LOCATION="${LOCATION:-centralindia}"
KUBERNETES_VERSION="${KUBERNETES_VERSION:-}"
AKS_NODE_COUNT="${AKS_NODE_COUNT:-}"
AKS_NODE_VM_SIZE="${AKS_NODE_VM_SIZE:-Standard_D4ds_v5}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GENERATED_DIR="$SCRIPT_DIR/generated"
CLUSTER_CONFIG_FILE="$GENERATED_DIR/cluster-config.json"

# Get currently logged in user info
get_current_user() {
    log_info "Getting current user information..."
    
    # Get the currently logged in user's UPN or email
    CURRENT_USER_ID=$(az ad signed-in-user show --query id -o tsv 2>/dev/null) || {
        log_error "Failed to get current user. Make sure you are logged in with 'az login'"
        exit 1
    }
    
    CURRENT_USER_UPN=$(az ad signed-in-user show --query userPrincipalName -o tsv 2>/dev/null) || {
        log_error "Failed to get current user UPN"
        exit 1
    }
    
    # Extract username from UPN (before the @)
    USERNAME=$(echo "$CURRENT_USER_UPN" | cut -d'@' -f1 | tr '.' '-' | tr '[:upper:]' '[:lower:]')
    
    log_info "Current user: $CURRENT_USER_UPN"
    log_info "Username for resources: $USERNAME"
}

# Set resource names based on username (can be overridden by CLI options)
set_resource_names() {
    RESOURCE_GROUP="${RESOURCE_GROUP_OVERRIDE:-${USERNAME}-flex-test-rg}"
    AKS_CLUSTER_NAME="${CLUSTER_NAME_OVERRIDE:-${USERNAME}-flex-aks}"
    
    log_info "Resource group: $RESOURCE_GROUP"
    log_info "AKS cluster: $AKS_CLUSTER_NAME"
}

# Create resource group
create_resource_group() {
    log_info "Creating resource group: $RESOURCE_GROUP in $LOCATION..."
    
    if az group show --name "$RESOURCE_GROUP" &>/dev/null; then
        log_warn "Resource group $RESOURCE_GROUP already exists"
    else
        az group create \
            --name "$RESOURCE_GROUP" \
            --location "$LOCATION" \
            --tags SkipCleanup=true \
            --output none
        
        log_info "Resource group created"
    fi
}

# Create AAD enabled AKS cluster (no Azure RBAC)
create_aks_cluster() {
    log_info "Creating AKS cluster: $AKS_CLUSTER_NAME..."
    
    if az aks show --resource-group "$RESOURCE_GROUP" --name "$AKS_CLUSTER_NAME" &>/dev/null; then
        log_warn "AKS cluster $AKS_CLUSTER_NAME already exists"
    else
        # Build AKS create command with AAD enabled (no Azure RBAC) and dev/test configuration
        local aks_create_cmd="az aks create \
            --resource-group $RESOURCE_GROUP \
            --name $AKS_CLUSTER_NAME \
            --location $LOCATION \
            --node-vm-size $AKS_NODE_VM_SIZE \
            --enable-aad \
            --output none"
        
        # Add node count only if specified
        if [[ -n "$AKS_NODE_COUNT" ]]; then
            aks_create_cmd="$aks_create_cmd --node-count $AKS_NODE_COUNT"
        fi
        
        # Add kubernetes version only if specified
        if [[ -n "$KUBERNETES_VERSION" ]]; then
            aks_create_cmd="$aks_create_cmd --kubernetes-version $KUBERNETES_VERSION"
        fi
        
        eval $aks_create_cmd
        
        log_info "AKS cluster created"
    fi
}

# Write cluster config for downstream scripts
write_cluster_config() {
    log_info "Writing cluster config to $CLUSTER_CONFIG_FILE..."
    cat > "$CLUSTER_CONFIG_FILE" <<EOF
{
  "resourceGroup": "$RESOURCE_GROUP",
  "clusterName": "$AKS_CLUSTER_NAME",
  "location": "$LOCATION"
}
EOF
    log_info "Cluster config written"
}

# Get AKS credentials
get_aks_credentials() {
    log_info "Getting AKS credentials..."
    
    KUBECONFIG_FILE="$GENERATED_DIR/kubeconfig"
    az aks get-credentials \
        --resource-group "$RESOURCE_GROUP" \
        --name "$AKS_CLUSTER_NAME" \
        --admin \
        --overwrite-existing \
        --file "$KUBECONFIG_FILE"
    
    export KUBECONFIG="$KUBECONFIG_FILE"
    log_info "AKS credentials saved to: $KUBECONFIG_FILE"
}

# Print summary
print_summary() {
    echo ""
    log_info "=========================================="
    log_info "  AKS Cluster Deployment Summary"
    log_info "=========================================="
    echo ""
    echo "Resource Group:     $RESOURCE_GROUP"
    echo "Location:           $LOCATION"
    echo ""
    echo "AKS Cluster:        $AKS_CLUSTER_NAME"
    echo "Kubernetes Version: ${KUBERNETES_VERSION:-<default>}"
    echo "Node Count:         ${AKS_NODE_COUNT:-<default>}"
    echo "Node VM Size:       $AKS_NODE_VM_SIZE"
    echo ""
    echo "=========================================="
    echo ""
    echo "To use kubectl with the AKS cluster:"
    echo "  export KUBECONFIG=$KUBECONFIG_FILE"
    echo "  kubectl get nodes"
    echo ""
    echo "Next steps:"
    echo "  1. Deploy a Flex Node VM: ./deploy-flex-node-vm.sh"
    echo "  2. Deploy api-server-proxy:  ./deploy-api-server-proxy.sh"
    echo "  3. Test pod policies:     ./test-pod-policies.sh"
    echo ""
    echo "To delete all resources:"
    echo "  az group delete --name $RESOURCE_GROUP --yes --no-wait"
    echo ""
}

# Print usage
usage() {
    head -22 "$0" | grep -E "^#" | sed 's/^# \?//'
    exit 0
}

# Main function
main() {
    log_info "Starting AKS cluster deployment for api-server-proxy testing"
    echo ""
    
    # Check prerequisites
    command -v az >/dev/null 2>&1 || { log_error "Azure CLI (az) is required but not installed"; exit 1; }
    
    # Check if logged in
    az account show &>/dev/null || { log_error "Not logged in to Azure. Run 'az login' first."; exit 1; }
    
    # Ensure generated directory exists
    mkdir -p "$GENERATED_DIR"
    
    # Get current user and set resource names
    get_current_user
    set_resource_names
    
    echo ""
    
    # Create resources
    create_resource_group
    create_aks_cluster
    write_cluster_config
    get_aks_credentials
    
    # Print summary
    print_summary
    
    log_info "AKS cluster deployment complete!"
}

# Parse arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        --help|-h)
            usage
            ;;
        --resource-group)
            RESOURCE_GROUP_OVERRIDE="$2"
            shift 2
            ;;
        --cluster-name)
            CLUSTER_NAME_OVERRIDE="$2"
            shift 2
            ;;
        --location)
            LOCATION="$2"
            shift 2
            ;;
        *)
            log_error "Unknown option: $1"
            usage
            ;;
    esac
done

main
