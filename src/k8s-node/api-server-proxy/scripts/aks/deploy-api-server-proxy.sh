#!/bin/bash
#
# Deploy api-server-proxy to AKS Flex Node
#
# This script deploys api-server-proxy to an AKS Flex node that was previously
# set up using deploy-cluster.sh. It:
# 1. Generates signing keys using policy-signing-tool.sh
# 2. Builds the api-server-proxy binary
# 3. Copies the binary and signing certificate to the VM via SSH
# 4. Runs install.sh on the VM to install api-server-proxy
#
# Usage:
#   ./deploy-api-server-proxy.sh [options]
#
# Options:
#   --help, -h     Show this help message
#
# Prerequisites:
#   - deploy-cluster.sh must have been run successfully
#   - openssl must be installed
#   - Go must be installed for building the binary
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
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$(dirname "$SCRIPT_DIR")")"
GENERATED_DIR="$SCRIPT_DIR/generated"
SIGNING_TOOL="${SCRIPT_DIR}/../policy-signing-tool.sh"
SIGNING_KEY_DIR="${GENERATED_DIR}/policy-signing-keys"
PROXY_LISTEN_ADDR="127.0.0.1:6444"
VM_CONFIG_FILE="$GENERATED_DIR/flex-node-vm-config.json"

# Read VM config written by deploy-flex-node-vm.sh
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
    
    # Verify SSH key exists (downloaded from Key Vault by deploy-flex-node-vm.sh).
    if [[ ! -f "$SSH_PRIVATE_KEY_FILE" ]]; then
        log_error "SSH private key not found at: $SSH_PRIVATE_KEY_FILE"
        log_error "Make sure deploy-flex-node-vm.sh was run successfully"
        exit 1
    fi
    
    # Get VM public IP
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

# Build api-server-proxy binary
build_binary() {
    log_info "Building api-server-proxy binary..."
    local repo_root="$(dirname "$(dirname "$PROJECT_ROOT")")"
    
    # Build for Linux (Azure VMs run Linux)
    # Build from repo root where go.mod lives, using full module path
    CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -C "$repo_root" \
        -v -ldflags "-s -w" \
        -o "$PROJECT_ROOT/bin/api-server-proxy-linux-amd64" \
        github.com/azure/azure-cleanroom/src/k8s-node/api-server-proxy/cmd/api-server-proxy
    
    log_info "Binary built: $PROJECT_ROOT/bin/api-server-proxy-linux-amd64"
}

# Generate signing keys
generate_signing_keys() {
    log_info "Generating signing keys using policy-signing-tool.sh..."
    "$SIGNING_TOOL" --key-dir "$SIGNING_KEY_DIR" generate
    SIGNING_CERT_FILE=$("$SIGNING_TOOL" --key-dir "$SIGNING_KEY_DIR" cert)
    log_info "Signing certificate: $SIGNING_CERT_FILE"
}

# Deploy api-server-proxy to the Azure VM
deploy_to_vm() {
    log_info "Deploying api-server-proxy to Azure VM: $VM_NAME..."
    
    local ssh_opts="-i $SSH_PRIVATE_KEY_FILE -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null"
    local staging_dir="/opt/api-server-proxy-staging"
    
    # Create staging directory on VM
    log_info "Creating staging directory on VM..."
    ssh $ssh_opts azureuser@$VM_PUBLIC_IP "sudo mkdir -p $staging_dir && sudo chown azureuser:azureuser $staging_dir" || {
        log_error "Failed to create staging directory on VM"
        exit 1
    }
    
    # Copy files to VM
    log_info "Copying files to VM..."
    
    # Copy api-server-proxy binary
    scp $ssh_opts "$PROJECT_ROOT/bin/api-server-proxy-linux-amd64" azureuser@$VM_PUBLIC_IP:$staging_dir/api-server-proxy || {
        log_error "Failed to copy api-server-proxy binary to VM"
        exit 1
    }
    
    # Copy install.sh script
    scp $ssh_opts "$PROJECT_ROOT/scripts/install.sh" azureuser@$VM_PUBLIC_IP:$staging_dir/install.sh || {
        log_error "Failed to copy install.sh to VM"
        exit 1
    }
    
    # Copy uninstall.sh script
    scp $ssh_opts "$PROJECT_ROOT/scripts/uninstall.sh" azureuser@$VM_PUBLIC_IP:$staging_dir/uninstall.sh || {
        log_error "Failed to copy uninstall.sh to VM"
        exit 1
    }
    
    # Copy signing certificate
    scp $ssh_opts "$SIGNING_CERT_FILE" azureuser@$VM_PUBLIC_IP:$staging_dir/signing-cert.pem || {
        log_error "Failed to copy signing certificate to VM"
        exit 1
    }
    
    # Verify files were copied
    log_info "Verifying files on VM..."
    ssh $ssh_opts azureuser@$VM_PUBLIC_IP "ls -la $staging_dir/"
    
    # Run uninstall.sh to cleanup any previous install
    log_info "Running uninstall.sh on VM to cleanup any previous install..."
    ssh $ssh_opts azureuser@$VM_PUBLIC_IP "sudo bash $staging_dir/uninstall.sh" || {
        log_warn "uninstall.sh returned non-zero (may be first install)"
    }
    
    # Run install.sh on VM with --signing-cert-file and --local-binary options
    log_info "Running install.sh on VM..."
    ssh $ssh_opts azureuser@$VM_PUBLIC_IP "sudo bash $staging_dir/install.sh \
        --local-binary $staging_dir/api-server-proxy \
        --signing-cert-file $staging_dir/signing-cert.pem \
        --proxy-listen-addr $PROXY_LISTEN_ADDR" || {
        log_error "Failed to run install.sh on VM"
        exit 1
    }
    
    log_info "api-server-proxy installed successfully on VM"
}

# Verify api-server-proxy deployment
verify_deployment() {
    log_info "Verifying api-server-proxy deployment on VM..."
    
    local ssh_opts="-i $SSH_PRIVATE_KEY_FILE -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null"
    
    echo ""
    echo "=== api-server-proxy status ==="
    ssh $ssh_opts azureuser@$VM_PUBLIC_IP "sudo systemctl status api-server-proxy --no-pager" || true
    
    echo ""
    echo "=== Recent api-server-proxy logs ==="
    ssh $ssh_opts azureuser@$VM_PUBLIC_IP "sudo journalctl -u api-server-proxy --no-pager -n 20" || true
    
    echo ""
    echo "=== kubelet status ==="
    ssh $ssh_opts azureuser@$VM_PUBLIC_IP "sudo systemctl status kubelet --no-pager | head -15" || true
    
    echo ""
    echo "=== Cluster nodes ==="
    kubectl get nodes -o wide
}

# Print summary
print_summary() {
    echo ""
    log_info "=========================================="
    log_info "  api-server-proxy Deployment Summary"
    log_info "=========================================="
    echo ""
    echo "Signing Keys:"
    echo "  Key dir:       $SIGNING_KEY_DIR"
    echo ""
    echo "VM Deployment:"
    echo "  VM Name:       $VM_NAME"
    echo "  VM IP:         $VM_PUBLIC_IP"
    echo "  Proxy Address: $PROXY_LISTEN_ADDR"
    echo ""
    echo "Useful Commands:"
    echo "  View proxy logs:     ssh -i $SSH_PRIVATE_KEY_FILE azureuser@$VM_PUBLIC_IP 'sudo journalctl -u api-server-proxy -f'"
    echo "  View kubelet logs:   ssh -i $SSH_PRIVATE_KEY_FILE azureuser@$VM_PUBLIC_IP 'sudo journalctl -u kubelet -f'"
    echo "  Test pod policies:   ./test-pod-policies.sh"
    echo ""
    echo "Pod policy verification is now ENABLED."
    echo "Unsigned pods scheduled to this node will be rejected."
    echo ""
}

# Print usage
usage() {
    head -22 "$0" | grep -E "^#" | sed 's/^# \?//'
    exit 0
}

# Main function
main() {
    log_info "Starting api-server-proxy deployment to AKS Flex node"
    echo ""
    
    # Check prerequisites
    command -v az >/dev/null 2>&1 || { log_error "Azure CLI (az) is required but not installed"; exit 1; }
    command -v openssl >/dev/null 2>&1 || { log_error "openssl is required but not installed"; exit 1; }
    command -v go >/dev/null 2>&1 || { log_error "Go is required but not installed"; exit 1; }
    command -v kubectl >/dev/null 2>&1 || { log_error "kubectl is required but not installed"; exit 1; }
    
    # Check if logged in to Azure
    az account show &>/dev/null || { log_error "Not logged in to Azure. Run 'az login' first."; exit 1; }
    
    # Use kubeconfig from generated folder.
    export KUBECONFIG="$GENERATED_DIR/kubeconfig"
    
    # Get resource information
    read_vm_config
    
    # Deploy
    build_binary
    generate_signing_keys
    deploy_to_vm
    verify_deployment
    print_summary
    
    log_info "Deployment complete!"
}

# Parse arguments
case "${1:-}" in
    --help|-h)
        usage
        ;;
    *)
        main "$@"
        ;;
esac
