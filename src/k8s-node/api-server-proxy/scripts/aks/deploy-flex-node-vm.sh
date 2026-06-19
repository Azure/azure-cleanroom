#!/bin/bash
#
# Deploy AKS Flex Node VM
#
# This script creates an Ubuntu 22.04 Confidential Azure VM and joins it as
# a flex node to an existing AKS cluster created by deploy-cluster.sh.
#
# Usage:
#   ./deploy-flex-node-vm.sh [options]
#
# Options:
#   --help, -h              Show this help message
#   --location <region>     Azure region (default: centralindia)
#   --vm-name <name>        VM name (default: <username>-flex-vm)
#   --kubelet-mi-name <n>   Kubelet managed identity name (default: <vm-name>-kubelet-mi)
#
# Environment Variables:
#   LOCATION         Azure region (default: centralindia, overridden by --location)
#   VM_SIZE          VM size (default: Standard_DC4as_v5)
#   VM_IMAGE         VM image (default: Canonical:0001-com-ubuntu-confidential-vm-jammy:22_04-lts-cvm:latest)
#
# Prerequisites:
#   - deploy-cluster.sh must have been run successfully
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
AKS_FLEX_NODE_VERSION="v0.0.17"
LOCATION="${LOCATION:-centralindia}"
VM_SIZE="${VM_SIZE:-Standard_DC4as_v5}"
VM_IMAGE="${VM_IMAGE:-Canonical:0001-com-ubuntu-confidential-vm-jammy:22_04-lts-cvm:latest}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GENERATED_DIR="$SCRIPT_DIR/generated"
PROJECT_ROOT="$(dirname "$(dirname "$SCRIPT_DIR")")"
CLUSTER_CONFIG_FILE="$GENERATED_DIR/cluster-config.json"
VM_CONFIG_FILE="$GENERATED_DIR/flex-node-vm-config.json"

# Get currently logged in user info
get_current_user() {
    log_info "Getting current user information..."

    CURRENT_USER_UPN=$(az ad signed-in-user show --query userPrincipalName -o tsv 2>/dev/null) || {
        log_error "Failed to get current user. Make sure you are logged in with 'az login'"
        exit 1
    }

    # Extract username from UPN (before the @)
    USERNAME=$(echo "$CURRENT_USER_UPN" | cut -d'@' -f1 | tr '.' '-' | tr '[:upper:]' '[:lower:]')

    log_info "Current user: $CURRENT_USER_UPN"
    log_info "Username for resources: $USERNAME"
}

# Read cluster config written by deploy-cluster.sh
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
    
    log_info "Resource group (from cluster config): $RESOURCE_GROUP"
    log_info "AKS cluster (from cluster config): $AKS_CLUSTER_NAME"
}

# Set resource names based on username
set_resource_names() {
    VM_NAME="${VM_NAME:-${USERNAME}-flex-vm}"
    KUBELET_MI_NAME="${KUBELET_MI_NAME:-${USERNAME}-flex-kubelet-mi}"

    log_info "VM name: $VM_NAME"
    log_info "Kubelet managed identity: $KUBELET_MI_NAME"
}

# Verify AKS cluster exists
verify_aks_cluster() {
    log_info "Verifying AKS cluster exists..."

    if ! az aks show --resource-group "$RESOURCE_GROUP" --name "$AKS_CLUSTER_NAME" &>/dev/null; then
        log_error "AKS cluster '$AKS_CLUSTER_NAME' not found in resource group '$RESOURCE_GROUP'"
        log_error "Please run deploy-cluster.sh first to create the AKS cluster"
        exit 1
    fi

    log_info "AKS cluster '$AKS_CLUSTER_NAME' found"
}

# Create kubelet user assigned managed identity
create_managed_identity() {
    log_info "Creating kubelet managed identity: $KUBELET_MI_NAME..."

    if az identity show --resource-group "$RESOURCE_GROUP" --name "$KUBELET_MI_NAME" &>/dev/null; then
        log_warn "Managed identity $KUBELET_MI_NAME already exists"
    else
        az identity create \
            --resource-group "$RESOURCE_GROUP" \
            --name "$KUBELET_MI_NAME" \
            --location "$LOCATION" \
            --output none

        log_info "Kubelet managed identity created"
    fi

    # Get the kubelet managed identity IDs
    KUBELET_MI_ID=$(az identity show --resource-group "$RESOURCE_GROUP" --name "$KUBELET_MI_NAME" --query id -o tsv)
    KUBELET_MI_CLIENT_ID=$(az identity show --resource-group "$RESOURCE_GROUP" --name "$KUBELET_MI_NAME" --query clientId -o tsv)
    KUBELET_MI_PRINCIPAL_ID=$(az identity show --resource-group "$RESOURCE_GROUP" --name "$KUBELET_MI_NAME" --query principalId -o tsv)
    log_info "Kubelet MI ID: $KUBELET_MI_ID"
    log_info "Kubelet MI Client ID: $KUBELET_MI_CLIENT_ID"
    log_info "Kubelet MI Principal ID (Object ID): $KUBELET_MI_PRINCIPAL_ID"

    # Give Owner role on the AKS cluster to the kubelet identity
    log_info "Assigning Owner role on AKS cluster to kubelet identity..."
    local aks_id
    aks_id=$(az aks show --resource-group "$RESOURCE_GROUP" --name "$AKS_CLUSTER_NAME" --query id -o tsv)
    az role assignment create \
        --assignee-object-id "$KUBELET_MI_PRINCIPAL_ID" \
        --assignee-principal-type ServicePrincipal \
        --role "Owner" \
        --scope "$aks_id" \
        --output none 2>/dev/null || log_warn "Owner role assignment may already exist"
    log_info "Owner role assigned to kubelet identity on AKS cluster"
}

# Setup Kubernetes RBAC roles for the kubelet identity
setup_kubernetes_rbac() {
    log_info "Setting up Kubernetes RBAC roles for kubelet identity..."

    local SP_OBJECT_ID="$KUBELET_MI_PRINCIPAL_ID"

    # Create node bootstrapper role binding
    log_info "Creating node bootstrapper ClusterRoleBinding..."
    kubectl apply -f - <<EOF
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRoleBinding
metadata:
  name: aks-flex-node-bootstrapper
roleRef:
  apiGroup: rbac.authorization.k8s.io
  kind: ClusterRole
  name: system:node-bootstrapper
subjects:
- apiGroup: rbac.authorization.k8s.io
  kind: User
  name: $SP_OBJECT_ID
EOF

    # Create node role binding
    log_info "Creating node ClusterRoleBinding..."
    kubectl apply -f - <<EOF
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRoleBinding
metadata:
  name: aks-flex-node-role
roleRef:
  apiGroup: rbac.authorization.k8s.io
  kind: ClusterRole
  name: system:node
subjects:
- apiGroup: rbac.authorization.k8s.io
  kind: User
  name: $SP_OBJECT_ID
EOF

    log_info "Kubernetes RBAC roles configured for kubelet identity"
}

# Create Ubuntu 22.04 Confidential VM with SSH enabled and managed identity
create_vm() {
    log_info "Creating Ubuntu 22.04 Confidential VM: $VM_NAME..."

    # Ensure generated directory exists
    mkdir -p "$GENERATED_DIR"

    # SSH key file paths
    SSH_PRIVATE_KEY_FILE="$GENERATED_DIR/${VM_NAME}-ssh.pem"
    local ssh_public_key_file="$GENERATED_DIR/${VM_NAME}-ssh.pub"

    # Always download SSH keys from Azure Key Vault.
    log_info "Downloading SSH keys from Key Vault 'azcleanroomemukv'..."
    az keyvault secret show \
        --vault-name "azcleanroomemukv" \
        --name "flex-node-ssh-private-key" \
        --query "value" -o tsv > "$SSH_PRIVATE_KEY_FILE"
    chmod 600 "$SSH_PRIVATE_KEY_FILE"

    az keyvault secret show \
        --vault-name "azcleanroomemukv" \
        --name "flex-node-ssh-public-key" \
        --query "value" -o tsv > "$ssh_public_key_file"
    log_info "SSH keys downloaded from Key Vault."

    if az vm show --resource-group "$RESOURCE_GROUP" --name "$VM_NAME" &>/dev/null; then
        log_warn "VM $VM_NAME already exists"
    else
        # Create Confidential VM with SSH key from Key Vault and kubelet managed identity
        log_info "Creating Confidential VM with SSH key..."
        az vm create \
            --resource-group "$RESOURCE_GROUP" \
            --name "$VM_NAME" \
            --location "$LOCATION" \
            --image "$VM_IMAGE" \
            --size "$VM_SIZE" \
            --admin-username azureuser \
            --ssh-key-values "$ssh_public_key_file" \
            --assign-identity "$KUBELET_MI_ID" \
            --public-ip-sku Standard \
            --enable-vtpm true \
            --security-type ConfidentialVM \
            --os-disk-security-encryption-type VMGuestStateOnly \
            --enable-secure-boot true \
            --output none

        log_info "VM created"
    fi

    # Wait for VM to get a public IP
    log_info "Waiting for VM to get a public IP..."
    for i in {1..30}; do
        VM_PUBLIC_IP=$(az vm show --resource-group "$RESOURCE_GROUP" --name "$VM_NAME" --show-details --query publicIps -o tsv 2>/dev/null || echo "")
        if [[ -n "$VM_PUBLIC_IP" ]]; then
            break
        fi
        sleep 2
    done

    if [[ -z "$VM_PUBLIC_IP" ]]; then
        log_error "Failed to get VM public IP after waiting"
        exit 1
    fi

    log_info "VM public IP: $VM_PUBLIC_IP"
    log_info "SSH private key: $SSH_PRIVATE_KEY_FILE"
    echo ""
    log_info "To SSH into the VM, run:"
    echo "  ssh -i $SSH_PRIVATE_KEY_FILE azureuser@$VM_PUBLIC_IP"
    echo ""
}

# Generate config file for aks-flex-node
generate_config_file() {
    log_info "Generating aks-flex-node-config.json..."

    # Get subscription ID
    local subscription_id
    subscription_id=$(az account show --query id -o tsv)

    # Get tenant ID
    local tenant_id
    tenant_id=$(az account show --query tenantId -o tsv)

    # Get kubelet managed identity client ID
    local mi_client_id
    mi_client_id="$KUBELET_MI_CLIENT_ID"

    # Get AKS cluster resource ID and location
    local aks_resource_id
    aks_resource_id=$(az aks show --resource-group "$RESOURCE_GROUP" --name "$AKS_CLUSTER_NAME" --query id -o tsv)
    local aks_location
    aks_location=$(az aks show --resource-group "$RESOURCE_GROUP" --name "$AKS_CLUSTER_NAME" --query location -o tsv)
    
    # Get Kubernetes version from the cluster
    local k8s_version
    k8s_version=$(az aks show --resource-group "$RESOURCE_GROUP" --name "$AKS_CLUSTER_NAME" --query currentKubernetesVersion -o tsv)

    # Ensure generated directory exists
    mkdir -p "$GENERATED_DIR"

    # Generate the config file
    local config_file="$GENERATED_DIR/aks-flex-node-config.json"
    cat > "$config_file" <<EOF
{
  "azure": {
    "subscriptionId": "$subscription_id",
    "tenantId": "$tenant_id",
    "cloud": "AzurePublicCloud",
    "managedIdentity": {
      "clientId": "$mi_client_id"
    },
    "targetCluster": {
      "resourceId": "$aks_resource_id",
      "location": "$aks_location"
    }
  },
  "kubernetes": {
    "version": "$k8s_version"
  },
  "agent": {
    "logLevel": "debug",
    "logDir": "/var/log/aks-flex-node"
  }
}
EOF

    log_info "Config file generated: $config_file"
}

# Run AKS Flex Node install script on VM
install_aks_flex_node() {
    log_info "Running AKS Flex Node install script on VM..."

    local ssh_opts="-i $SSH_PRIVATE_KEY_FILE -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null"

    # Copy api-server-proxy uninstall.sh to VM first
    log_info "Copying api-server-proxy uninstall.sh to VM..."
    local staging_dir="/tmp/api-server-proxy-staging"
    ssh $ssh_opts azureuser@$VM_PUBLIC_IP "mkdir -p $staging_dir" || {
        log_error "Failed to create staging directory on VM"
        exit 1
    }

    scp $ssh_opts "$PROJECT_ROOT/scripts/uninstall.sh" azureuser@$VM_PUBLIC_IP:$staging_dir/uninstall.sh || {
        log_error "Failed to copy uninstall.sh to VM"
        exit 1
    }
    
    # Download aks-flex-node uninstall script, patch out remove_azure_cli, and copy to VM
    log_info "Downloading and patching aks-flex-node uninstall script..."
    local flex_uninstall_file="$GENERATED_DIR/aks-flex-node-uninstall.sh"
    curl -fsSL "https://raw.githubusercontent.com/Azure/AKSFlexNode/refs/tags/$AKS_FLEX_NODE_VERSION/scripts/uninstall.sh" -o "$flex_uninstall_file"
    sed -i 's/^\([[:space:]]*\)remove_azure_cli$/\1#remove_azure_cli/' "$flex_uninstall_file"
    
    scp $ssh_opts "$flex_uninstall_file" azureuser@$VM_PUBLIC_IP:/tmp/aks-flex-node-uninstall.sh || {
        log_error "Failed to copy aks-flex-node uninstall script to VM"
        exit 1
    }

    # Create a temporary setup script
    local setup_script_file
    setup_script_file=$(mktemp /tmp/flex-node-setup-XXXXXX.sh)
    cat > "$setup_script_file" <<'SCRIPT_EOF'
#!/bin/bash
set -e

# Uninstall api-server-proxy if present (do this first)
echo "Uninstalling api-server-proxy if present..."
if [[ -f /tmp/api-server-proxy-staging/uninstall.sh ]]; then
    sudo bash /tmp/api-server-proxy-staging/uninstall.sh || echo "api-server-proxy uninstall returned non-zero (may not be installed)"
fi

# Cleanup previous aks-flex-node setup if any
echo "Running aks-flex-node uninstall script to cleanup previous setup..."
sudo bash /tmp/aks-flex-node-uninstall.sh --force

echo "Setup completed successfully"
SCRIPT_EOF

    # Replace the placeholder with actual client ID
    sed -i "s/\$KUBELET_MI_CLIENT_ID/$KUBELET_MI_CLIENT_ID/g" "$setup_script_file"
    sed -i "s/\$AKS_FLEX_NODE_VERSION/$AKS_FLEX_NODE_VERSION/g" "$setup_script_file"

    scp $ssh_opts "$setup_script_file" azureuser@$VM_PUBLIC_IP:/tmp/flex-node-setup.sh || {
        rm -f "$setup_script_file"
        log_error "Failed to copy setup script to VM"
        exit 1
    }
    rm -f "$setup_script_file"

    ssh $ssh_opts azureuser@$VM_PUBLIC_IP "sudo bash /tmp/flex-node-setup.sh" || {
        log_error "Failed to run setup script on VM"
        exit 1
    }

    # Copy config file to VM after uninstall but before install
    log_info "Copying config file to Azure VM..."
    local config_file="$GENERATED_DIR/aks-flex-node-config.json"

    # Create the target directory on the VM
    log_info "Creating /etc/aks-flex-node directory on VM..."
    ssh $ssh_opts azureuser@$VM_PUBLIC_IP "sudo mkdir -p /etc/aks-flex-node" || {
        log_error "Failed to create directory on VM"
        exit 1
    }

    # Copy the config file to a temp location first, then move with sudo
    log_info "Copying config file to VM..."
    scp $ssh_opts "$config_file" azureuser@$VM_PUBLIC_IP:/tmp/config.json || {
        log_error "Failed to copy config file to VM"
        exit 1
    }

    ssh $ssh_opts azureuser@$VM_PUBLIC_IP "sudo mv /tmp/config.json /etc/aks-flex-node/config.json" || {
        log_error "Failed to move config file to /etc/aks-flex-node"
        exit 1
    }

    log_info "Config file copied to /etc/aks-flex-node/config.json on VM"
    
    # Download aks-flex-node install script, patch it, and copy to VM
    log_info "Downloading and patching aks-flex-node install script..."
    local flex_install_file="$GENERATED_DIR/aks-flex-node-install.sh"
    curl -fsSL "https://raw.githubusercontent.com/Azure/AKSFlexNode/refs/tags/$AKS_FLEX_NODE_VERSION/scripts/install.sh" -o "$flex_install_file"
    sed -i "s/version=\$(get_latest_release)/version=\"$AKS_FLEX_NODE_VERSION\"/" "$flex_install_file"

    # Remove az cli related steps as its not needed for MI based setup.
    sed -i 's/^\([[:space:]]*\)install_azure_cli$/\1#install_azure_cli/' "$flex_install_file"
    sed -i 's/^\([[:space:]]*\)check_azure_cli_auth$/\1#check_azure_cli_auth/' "$flex_install_file"
    sed -i 's/^\([[:space:]]*\)setup_permissions$/\1#setup_permissions/' "$flex_install_file"
    
    scp $ssh_opts "$flex_install_file" azureuser@$VM_PUBLIC_IP:/tmp/aks-flex-node-install.sh || {
        log_error "Failed to copy aks-flex-node install script to VM"
        exit 1
    }

    # Now run the install and enable script
    log_info "Running install and enable script on VM..."
    local install_script_file
    install_script_file=$(mktemp /tmp/flex-node-install-XXXXXX.sh)
    cat > "$install_script_file" <<'INSTALL_SCRIPT_EOF'
#!/bin/bash
set -e

# Run the AKS Flex Node install script
echo "Running AKS Flex Node install script..."
sudo bash /tmp/aks-flex-node-install.sh

# Enable and start the aks-flex-node-agent service
echo "Enabling and starting aks-flex-node-agent service..."
sudo systemctl enable --now aks-flex-node-agent

# Wait for status.json to appear and kubelet to be ready
echo "Waiting for aks-flex-node to become ready... (use journalctl -u aks-flex-node-agent -f to view logs)"
status_file="/run/aks-flex-node/status.json"
max_wait=300  # 5 minutes
wait_interval=10
elapsed=0

while [[ $elapsed -lt $max_wait ]]; do
    # Check if aks-flex-node-agent service has failed
    if ! systemctl is-active --quiet aks-flex-node-agent; then
        service_status=$(systemctl is-active aks-flex-node-agent 2>/dev/null || echo "unknown")
        if [[ "$service_status" == "failed" || "$service_status" == "inactive" ]]; then
            echo "ERROR: aks-flex-node-agent service has stopped (status: $service_status)"
            echo "Dumping aks-flex-node-agent logs:"
            journalctl -u aks-flex-node-agent --since "5 minutes ago" --no-pager
            exit 1
        fi
    else
        # Service is running, show last 3 lines of logs
        echo "Last 3 lines of aks-flex-node-agent logs:"
        journalctl -u aks-flex-node-agent --no-pager -n 3
    fi

    if [[ -f "$status_file" ]]; then
        kubelet_running=$(sudo jq -r '.kubeletRunning' "$status_file" 2>/dev/null || echo "false")
        kubelet_ready=$(sudo jq -r '.kubeletReady' "$status_file" 2>/dev/null || echo "")

        echo "Status: kubeletRunning=$kubelet_running, kubeletReady=$kubelet_ready"

        if [[ "$kubelet_running" == "true" && "$kubelet_ready" == "Ready" ]]; then
            echo "AKS Flex Node is ready!"
            break
        fi
    else
        echo "Waiting for $status_file to appear..."
    fi

    sleep $wait_interval
    elapsed=$((elapsed + wait_interval))
done

if [[ $elapsed -ge $max_wait ]]; then
    echo "ERROR: AKS Flex Node did not become ready within ${max_wait} seconds"
    echo "Dumping aks-flex-node-agent logs:"
    journalctl -u aks-flex-node-agent --since "5 minutes ago" --no-pager
    exit 1
fi

echo "Install and setup completed successfully"
INSTALL_SCRIPT_EOF

    scp $ssh_opts "$install_script_file" azureuser@$VM_PUBLIC_IP:/tmp/flex-node-install.sh || {
        rm -f "$install_script_file"
        log_error "Failed to copy install script to VM"
        exit 1
    }
    rm -f "$install_script_file"

    ssh $ssh_opts azureuser@$VM_PUBLIC_IP "sudo bash /tmp/flex-node-install.sh" || {
        log_error "Failed to run install script on VM"
        exit 1
    }

    log_info "AKS Flex Node installation completed on VM"
}

# Verify the VM node joined the AKS cluster
verify_node_joined() {
    log_info "Verifying Azure VM is showing up as a node on the AKS cluster..."

    echo ""
    kubectl get nodes
    echo ""

    # Check if the node with the VM name exists in the cluster
    if kubectl get node "$VM_NAME" &>/dev/null; then
        log_info "Node verification successful - VM '$VM_NAME' is showing up as a node in the cluster"
    else
        log_error "VM '$VM_NAME' is not showing up as a node in the AKS cluster"
        exit 1
    fi

    # Add taint to indicate only pods with pod policy can be scheduled on this node
    log_info "Adding taint to node '$VM_NAME' to require pod policy..."
    kubectl taint nodes "$VM_NAME" pod-policy=required:NoSchedule --overwrite
    log_info "Taint added: pod-policy=required:NoSchedule"

    # Add node selector label to help pods pick nodes that require pod policy
    log_info "Adding node selector label to node '$VM_NAME'..."
    kubectl label nodes "$VM_NAME" pod-policy=required --overwrite
    log_info "Label added: pod-policy=required"

    log_info "Node '$VM_NAME' successfully joined the cluster!"
}

# Write VM config for downstream scripts
write_vm_config() {
    log_info "Writing VM config to $VM_CONFIG_FILE..."
    cat > "$VM_CONFIG_FILE" <<EOF
{
  "resourceGroup": "$RESOURCE_GROUP",
  "clusterName": "$AKS_CLUSTER_NAME",
  "vmName": "$VM_NAME",
  "sshPrivateKeyFile": "$SSH_PRIVATE_KEY_FILE",
  "kubeletMiName": "$KUBELET_MI_NAME"
}
EOF
    log_info "VM config written"
}

# Print summary
print_summary() {
    echo ""
    log_info "=========================================="
    log_info "  Flex Node VM Deployment Summary"
    log_info "=========================================="
    echo ""
    echo "Resource Group:     $RESOURCE_GROUP"
    echo "Location:           $LOCATION"
    echo "AKS Cluster:        $AKS_CLUSTER_NAME"
    echo ""
    echo "VM Name:            $VM_NAME"
    echo "VM Public IP:       $VM_PUBLIC_IP"
    echo "VM Admin User:      azureuser"
    echo "VM Image:           $VM_IMAGE"
    echo "VM Size:            $VM_SIZE"
    echo ""
    echo "Kubelet MI:         $KUBELET_MI_NAME"
    echo ""
    echo "Generated Files:"
    echo "  SSH Private Key:  $SSH_PRIVATE_KEY_FILE"
    echo "  Config File:      $GENERATED_DIR/aks-flex-node-config.json"
    echo ""
    echo "=========================================="
    echo ""
    echo "To SSH into the VM:"
    echo "  ssh -i $SSH_PRIVATE_KEY_FILE azureuser@$VM_PUBLIC_IP"
    echo ""
    echo "Node taint and label applied:"
    echo "  Taint: pod-policy=required:NoSchedule"
    echo "  Label: pod-policy=required"
    echo ""
}

# Print usage
usage() {
    head -22 "$0" | grep -E "^#" | sed 's/^# \?//'
    exit 0
}

# Main function
main() {
    log_info "Starting Azure Flex Node VM deployment"
    echo ""

    # Check prerequisites
    command -v az >/dev/null 2>&1 || { log_error "Azure CLI (az) is required but not installed"; exit 1; }
    command -v kubectl >/dev/null 2>&1 || { log_error "kubectl is required but not installed"; exit 1; }

    # Check if logged in
    az account show &>/dev/null || { log_error "Not logged in to Azure. Run 'az login' first."; exit 1; }

    # Use kubeconfig from generated folder (created by deploy-cluster.sh).
    export KUBECONFIG="$GENERATED_DIR/kubeconfig"
    if [[ ! -f "$KUBECONFIG" ]]; then
        log_error "Kubeconfig not found at: $KUBECONFIG"
        log_error "Make sure deploy-cluster.sh was run successfully"
        exit 1
    fi

    # Get current user and set resource names
    get_current_user
    read_cluster_config
    set_resource_names

    echo ""

    # Verify AKS cluster exists
    verify_aks_cluster

    # Create kubelet managed identity and setup k8s RBAC
    create_managed_identity
    setup_kubernetes_rbac

    # Create VM and configure
    create_vm
    generate_config_file
    install_aks_flex_node
    verify_node_joined
    write_vm_config

    # Print summary
    print_summary

    log_info "Flex Node VM deployment complete!"
}

# Parse arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        --help|-h)
            usage
            ;;
        --location)
            LOCATION="$2"
            shift 2
            ;;
        --vm-name)
            VM_NAME="$2"
            shift 2
            ;;
        --kubelet-mi-name)
            KUBELET_MI_NAME="$2"
            shift 2
            ;;
        *)
            log_error "Unknown option: $1"
            usage
            ;;
    esac
done

main
