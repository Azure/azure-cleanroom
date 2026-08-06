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
#   --max-pods-per-node N   Max pods per node for kubelet (default: 110)
#   --kubelet-mi-name <n>   Kubelet managed identity name (default: <vm-name>-kubelet-mi)
#
# Environment Variables:
#   LOCATION         Azure region (default: centralindia, overridden by --location)
#   VM_SIZE          VM size (default: Standard_DC2as_v5)
#   VM_IMAGE         VM image (default: Canonical:ubuntu-24_04-lts:cvm:24.04.202604160)
#
# Prerequisites:
#   - deploy-cluster.sh must have been run successfully
#   - Must be logged in to Azure (az login)
#

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Source shared helpers.
source "$SCRIPT_DIR/common.sh"

# Configuration
AKS_FLEX_NODE_VERSION="v0.0.19"
LOCATION="${LOCATION:-centralindia}"
VM_SIZE="${VM_SIZE:-Standard_DC2as_v5}"
VM_IMAGE="${VM_IMAGE:-Canonical:ubuntu-24_04-lts:cvm:24.04.202604160}"
MAX_PODS_PER_NODE="${MAX_PODS_PER_NODE:-110}"
GENERATED_DIR="$SHARED_AKS_GENERATED_DIR"

# get_unique_string generates a deterministic lowercase string from an input ID
# by taking a SHA-512 hash and mapping bytes to [a-z].
get_unique_string() {
    local id="$1"
    local length="${2:-13}"
    printf '%s' "$id" | sha512sum | cut -c1-$((length * 2)) | \
        sed 's/../0x& /g' | xargs -n1 printf '%d\n' | head -n "$length" | \
        awk '{printf "%c", ($1 % 26) + 97}'
}

# Set resource names based on username
set_resource_names() {
    if [[ "${GITHUB_ACTIONS:-}" == "true" ]]; then
        local unique
        unique=$(get_unique_string "flex-node-vm-${JOB_ID:-}-${RUN_ID:-}")
        VM_NAME="${VM_NAME:-flex-vm-${unique}}"
        KUBELET_MI_NAME="${KUBELET_MI_NAME:-flex-kubelet-mi-${unique}}"
    else
        VM_NAME="${VM_NAME:-${USERNAME}-flex-vm-2}"
        KUBELET_MI_NAME="${KUBELET_MI_NAME:-${USERNAME}-flex-kubelet-mi}"
    fi

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
    log_info "Downloading SSH keys from Key Vault 'azcleanroompublickv'..."
    az keyvault secret show \
        --vault-name "azcleanroompublickv" \
        --name "flex-node-ssh-private-key" \
        --query "value" -o tsv > "$SSH_PRIVATE_KEY_FILE"
    chmod 600 "$SSH_PRIVATE_KEY_FILE"

    az keyvault secret show \
        --vault-name "azcleanroompublickv" \
        --name "flex-node-ssh-public-key" \
        --query "value" -o tsv > "$ssh_public_key_file"
    log_info "SSH keys downloaded from Key Vault."

    if az vm show --resource-group "$RESOURCE_GROUP" --name "$VM_NAME" &>/dev/null; then
        log_warn "VM $VM_NAME already exists"
    else
        # Place the flex VM in the same VNet as the AKS cluster (in a dedicated
        # "flexnode" subnet) following the pattern in FlexNodeProvider.cs. This
        # gives the VM direct L3 connectivity to the AKS nodes and konnectivity
        # agents without requiring VNet peering.
        local nic_name="${VM_NAME}-nic"
        local nsg_name="${VM_NAME}-nsg"
        local public_ip_name="${VM_NAME}-ip"
        local flexnode_subnet_name="flexnode"
        local flexnode_subnet_prefix="10.10.0.0/16"

        # Get the AKS node resource group and VNet.
        local aks_node_rg
        aks_node_rg=$(az aks show --resource-group "$RESOURCE_GROUP" \
            --name "$AKS_CLUSTER_NAME" --query nodeResourceGroup -o tsv)
        local aks_vnet_name
        aks_vnet_name=$(az network vnet list --resource-group "$aks_node_rg" \
            --query "[0].name" -o tsv)
        log_info "AKS VNet: $aks_vnet_name (in $aks_node_rg)"

        # Create a "flexnode" subnet in the AKS VNet if it doesn't exist.
        # First ensure the VNet has the flexnode address space.
        if ! az network vnet subnet show --resource-group "$aks_node_rg" \
            --vnet-name "$aks_vnet_name" --name "$flexnode_subnet_name" &>/dev/null; then
            log_info "Adding address prefix $flexnode_subnet_prefix to AKS VNet..."
            local current_prefixes
            current_prefixes=$(az network vnet show --resource-group "$aks_node_rg" \
                --name "$aks_vnet_name" --query "addressSpace.addressPrefixes" -o tsv)
            if ! echo "$current_prefixes" | grep -q "$flexnode_subnet_prefix"; then
                az network vnet update \
                    --resource-group "$aks_node_rg" \
                    --name "$aks_vnet_name" \
                    --address-prefixes $current_prefixes "$flexnode_subnet_prefix" \
                    --output none
            fi

            log_info "Creating flexnode subnet ($flexnode_subnet_prefix) in AKS VNet..."
            az network vnet subnet create \
                --resource-group "$aks_node_rg" \
                --vnet-name "$aks_vnet_name" \
                --name "$flexnode_subnet_name" \
                --address-prefixes "$flexnode_subnet_prefix" \
                --output none
        else
            log_info "Flexnode subnet already exists in AKS VNet"
        fi

        local flexnode_subnet_id
        flexnode_subnet_id=$(az network vnet subnet show \
            --resource-group "$aks_node_rg" \
            --vnet-name "$aks_vnet_name" \
            --name "$flexnode_subnet_name" \
            --query id -o tsv)

        # Azure NRMS may auto-create a subnet-level NSG that blocks Internet
        # inbound. Add an SSH allow rule if a subnet NSG exists.
        local subnet_nsg_id
        subnet_nsg_id=$(az network vnet subnet show --resource-group "$aks_node_rg" \
            --vnet-name "$aks_vnet_name" --name "$flexnode_subnet_name" \
            --query "networkSecurityGroup.id" -o tsv 2>/dev/null || echo "")
        if [[ -n "$subnet_nsg_id" && "$subnet_nsg_id" != "None" ]]; then
            local subnet_nsg_name subnet_nsg_rg
            subnet_nsg_name=$(basename "$subnet_nsg_id")
            subnet_nsg_rg=$(echo "$subnet_nsg_id" | grep -oP '(?<=resourceGroups/)[^/]+')
            if ! az network nsg rule show --resource-group "$subnet_nsg_rg" \
                --nsg-name "$subnet_nsg_name" --name "AllowSSH" &>/dev/null; then
                log_info "Adding SSH rule to subnet NSG ($subnet_nsg_name)..."
                az network nsg rule create \
                    --resource-group "$subnet_nsg_rg" \
                    --nsg-name "$subnet_nsg_name" \
                    --name "AllowSSH" \
                    --priority 100 \
                    --protocol Tcp \
                    --destination-port-ranges 22 \
                    --source-address-prefixes "*" \
                    --output none
            fi
        fi

        log_info "Creating NSG, public IP and NIC..."
        az network nsg create \
            --resource-group "$RESOURCE_GROUP" \
            --name "$nsg_name" \
            --location "$LOCATION" \
            --output none

        az network nsg rule create \
            --resource-group "$RESOURCE_GROUP" \
            --nsg-name "$nsg_name" \
            --name "AllowSSH" \
            --priority 1000 \
            --protocol Tcp \
            --destination-port-ranges 22 \
            --output none

        az network public-ip create \
            --resource-group "$RESOURCE_GROUP" \
            --name "$public_ip_name" \
            --location "$LOCATION" \
            --sku Standard \
            --output none

        # Create NIC in the AKS VNet's flexnode subnet.
        az network nic create \
            --resource-group "$RESOURCE_GROUP" \
            --name "$nic_name" \
            --location "$LOCATION" \
            --subnet "$flexnode_subnet_id" \
            --public-ip-address "$public_ip_name" \
            --network-security-group "$nsg_name" \
            --output none

        log_info "Adding secondary IP config to NIC..."
        az network nic ip-config create \
            --resource-group "$RESOURCE_GROUP" \
            --nic-name "$nic_name" \
            --name "secondary-ipconfig" \
            --subnet "$flexnode_subnet_id" \
            --private-ip-address-version IPv4 \
            --output none

        PRIMARY_PRIVATE_IP=$(az network nic ip-config show \
            --resource-group "$RESOURCE_GROUP" \
            --nic-name "$nic_name" \
            --name "ipconfig1" \
            --query privateIPAddress -o tsv)
        log_info "Primary private IP: $PRIMARY_PRIVATE_IP"

        SECONDARY_PRIVATE_IP=$(az network nic ip-config show \
            --resource-group "$RESOURCE_GROUP" \
            --nic-name "$nic_name" \
            --name "secondary-ipconfig" \
            --query privateIPAddress -o tsv)
        log_info "Secondary private IP: $SECONDARY_PRIVATE_IP"

        # Generate cloud-init config that overrides the default netplan with a
        # static config listing both the primary and secondary IPs. Without this,
        # cloud-init's default netplan only configures the primary IP via DHCP
        # and the secondary IP is not visible to the OS.
        local cloud_init_file
        cloud_init_file=$(mktemp /tmp/cloud-init-XXXXXX.yaml)
        cat > "$cloud_init_file" <<CLOUDINITEOF
#cloud-config
write_files:
  - path: /etc/netplan/99-static-eth0.yaml
    content: |
      network:
        version: 2
        ethernets:
          eth0:
            addresses:
              - ${PRIMARY_PRIVATE_IP}/16
              - ${SECONDARY_PRIVATE_IP}/16
            dhcp4: true
            dhcp4-overrides:
              route-metric: 100
            dhcp6: false

runcmd:
  - chmod 600 /etc/netplan/99-static-eth0.yaml
  - mv /etc/netplan/50-cloud-init.yaml /etc/netplan/50-cloud-init.yaml.bak
  - netplan apply
  - apt-get remove -y unattended-upgrades
CLOUDINITEOF

        # Create Confidential VM with the pre-created NIC and cloud-init.
        log_info "Creating Confidential VM with SSH key and cloud-init netplan override..."
        az vm create \
            --resource-group "$RESOURCE_GROUP" \
            --name "$VM_NAME" \
            --location "$LOCATION" \
            --image "$VM_IMAGE" \
            --size "$VM_SIZE" \
            --admin-username azureuser \
            --ssh-key-values "$ssh_public_key_file" \
            --assign-identity "$KUBELET_MI_ID" \
            --nics "$nic_name" \
            --custom-data "$cloud_init_file" \
            --enable-vtpm true \
            --security-type ConfidentialVM \
            --os-disk-security-encryption-type VMGuestStateOnly \
            --enable-secure-boot true \
            --output none

        rm -f "$cloud_init_file"
        log_info "VM created in AKS VNet (flexnode subnet)"
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
  "node": {
    "kubelet": {
      "dnsServiceIp": "168.63.129.16"
    },
    "maxPods": $MAX_PODS_PER_NODE
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

# Cleanup previous aks-flex-node setup if any (but NOT the proxies —
# they were intentionally installed before the flex-node agent).
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

# Ensure api-server-proxy is still running after the flex-node install.
# The install may have done daemon-reload which can briefly disrupt services.
echo "Verifying api-server-proxy is running..."
if ! systemctl is-active --quiet api-server-proxy 2>/dev/null; then
    echo "  api-server-proxy not active, restarting..."
    systemctl restart api-server-proxy
    sleep 2
fi
systemctl is-active --quiet api-server-proxy && echo "  api-server-proxy: active" || echo "  WARNING: api-server-proxy not running"

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

# Restart kubelet-proxy after flex-node install completes.
echo "Restarting kubelet-proxy..."
systemctl restart kubelet-proxy
sleep 2
if systemctl is-active --quiet kubelet-proxy; then
    echo "  kubelet-proxy: active"
else
    echo "  WARNING: kubelet-proxy may have issues"
fi
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

    # Patch the node's kubelet endpoint port to 10250 (kubelet-proxy's port).
    # kubelet registers with port 10251 (its actual port), but external traffic
    # should go to the proxy on 10250.
    log_info "Patching node kubelet endpoint port to 10250 (kubelet-proxy)..."
    kubectl patch node "$VM_NAME" --subresource=status --type=merge \
        -p '{"status":{"daemonEndpoints":{"kubeletEndpoint":{"Port":10250}}}}' || {
        log_warn "Failed to patch node port (may need retrying)"
    }
    local patched_port
    patched_port=$(kubectl get node "$VM_NAME" -o jsonpath='{.status.daemonEndpoints.kubeletEndpoint.Port}')
    log_info "Node kubelet endpoint port: $patched_port"

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

# ============================================================================
# Pre-kubelet proxy setup (simulates cleanroom-boot stages)
# ============================================================================

# Uninstall any previously installed proxies on the VM.
# Must run BEFORE deploying new proxies for a clean state.
uninstall_existing_proxies() {
    log_info "Uninstalling existing proxies (if any)..."
    local ssh_opts="-i $SSH_PRIVATE_KEY_FILE -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null"

    ssh $ssh_opts azureuser@$VM_PUBLIC_IP 'sudo bash -s' <<'UNINSTALL_EOF'
# Stop and disable proxy services.
for svc in api-server-proxy kubelet-proxy; do
    if systemctl is-active --quiet "$svc" 2>/dev/null; then
        systemctl stop "$svc"
        echo "  Stopped $svc"
    fi
    systemctl disable "$svc" 2>/dev/null || true
    rm -f "/etc/systemd/system/${svc}.service"
done
# Remove proxy config dirs.
rm -rf /etc/api-server-proxy /etc/kubelet-proxy
# Remove kubelet drop-ins from previous kubelet-proxy installs.
rm -rf /etc/systemd/system/kubelet.service.d/30-kubelet-proxy-node-config.conf 2>/dev/null || true
rm -rf /etc/systemd/system/kubelet.service.d/10-kubelet-proxy-iptables.conf 2>/dev/null || true
# Remove binaries.
rm -f /usr/local/bin/api-server-proxy /usr/local/bin/kubelet-proxy
systemctl daemon-reload
echo "  Cleanup complete"
UNINSTALL_EOF
}

# Resolve API server URL and CA from the AKS cluster.
# Simulates cleanroom-boot's ResolveClusterConfig stage.
resolve_api_server_info() {
    log_info "Resolving API server info from AKS cluster..."

    API_SERVER_FQDN=$(az aks show --resource-group "$RESOURCE_GROUP" --name "$AKS_CLUSTER_NAME" \
        --query "fqdn" -o tsv 2>/dev/null)
    if [[ -z "$API_SERVER_FQDN" ]]; then
        log_error "Could not resolve API server FQDN"
        exit 1
    fi
    API_SERVER_URL="https://${API_SERVER_FQDN}:443"
    log_info "API Server: $API_SERVER_URL"

    # Get the cluster CA cert from admin credentials.
    local admin_kubeconfig
    admin_kubeconfig=$(az aks get-credentials --resource-group "$RESOURCE_GROUP" \
        --name "$AKS_CLUSTER_NAME" --admin --file - 2>/dev/null)
    API_SERVER_CA_B64=$(echo "$admin_kubeconfig" | grep "certificate-authority-data:" | awk '{print $2}' | head -1)

    if [[ -z "$API_SERVER_CA_B64" ]]; then
        log_error "Could not extract API server CA"
        exit 1
    fi

    # Save CA cert to file for the proxy.
    echo "$API_SERVER_CA_B64" | base64 -d > "$GENERATED_DIR/api-server-ca.pem"
    log_info "API server CA saved to $GENERATED_DIR/api-server-ca.pem"
}

# Build and deploy api-server-proxy to the VM.
# Simulates cleanroom-boot's StartApiServerProxy stage.
deploy_api_server_proxy_to_vm() {
    log_info "Building and deploying api-server-proxy..."

    local ssh_opts="-i $SSH_PRIVATE_KEY_FILE -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null"
    local proxy_dir="$K8S_NODE_DIR/api-server-proxy"
    local staging_dir="/opt/api-server-proxy-staging"

    # Build binary.
    local repo_root="$(dirname "$(dirname "$K8S_NODE_DIR")")"
    CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -C "$repo_root" \
        -ldflags "-s -w" \
        -o "$proxy_dir/bin/api-server-proxy-linux-amd64" \
        github.com/azure/azure-cleanroom/src/k8s-node/api-server-proxy/cmd/api-server-proxy
    log_info "Binary built"

    # Generate signing keys.
    local signing_tool="$proxy_dir/scripts/policy-signing-tool.sh"
    local signing_key_dir="$GENERATED_DIR/policy-signing-keys"
    "$signing_tool" --key-dir "$signing_key_dir" generate
    local signing_cert_file
    signing_cert_file=$("$signing_tool" --key-dir "$signing_key_dir" cert)

    # Copy files to VM. install.sh --env aks stages aks/configure.sh, so the
    # aks/ subdir must be present alongside install.sh.
    ssh $ssh_opts azureuser@$VM_PUBLIC_IP \
        "sudo mkdir -p $staging_dir/aks && sudo chown -R azureuser:azureuser $staging_dir"
    scp $ssh_opts "$proxy_dir/bin/api-server-proxy-linux-amd64" azureuser@$VM_PUBLIC_IP:$staging_dir/api-server-proxy
    scp $ssh_opts "$proxy_dir/scripts/install.sh" azureuser@$VM_PUBLIC_IP:$staging_dir/install.sh
    scp $ssh_opts "$proxy_dir/scripts/aks/configure.sh" azureuser@$VM_PUBLIC_IP:$staging_dir/aks/configure.sh
    scp $ssh_opts "$GENERATED_DIR/api-server-ca.pem" azureuser@$VM_PUBLIC_IP:$staging_dir/api-server-ca.pem
    scp $ssh_opts "$signing_cert_file" azureuser@$VM_PUBLIC_IP:$staging_dir/signing-cert.pem

    # Run install.sh — places binary + systemd unit + stages aks/configure.sh.
    log_info "Running api-server-proxy install.sh..."
    ssh $ssh_opts azureuser@$VM_PUBLIC_IP "sudo bash $staging_dir/install.sh \
        --env aks \
        --local-binary $staging_dir/api-server-proxy"

    # Run configure.sh — generates certs, writes config, starts service (boot phase).
    local configure_args="--upstream-api-server $API_SERVER_URL"
    configure_args="$configure_args --upstream-ca-file $staging_dir/api-server-ca.pem"
    configure_args="$configure_args --signing-cert-file $staging_dir/signing-cert.pem"
    configure_args="$configure_args --msi-client-id $KUBELET_MI_CLIENT_ID"

    local tenant_id
    tenant_id=$(az account show --query tenantId -o tsv)
    configure_args="$configure_args --msi-tenant-id $tenant_id"

    log_info "Running api-server-proxy configure.sh..."
    ssh $ssh_opts azureuser@$VM_PUBLIC_IP "sudo bash $staging_dir/configure.sh $configure_args"
    log_info "api-server-proxy is running on :6444"
}

# Build and deploy kubelet-proxy to the VM.
# Simulates cleanroom-boot's StartKubeletProxy stage.
deploy_kubelet_proxy_to_vm() {
    log_info "Building and deploying kubelet-proxy..."

    local ssh_opts="-i $SSH_PRIVATE_KEY_FILE -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null"
    local proxy_dir="$K8S_NODE_DIR/kubelet-proxy"
    local staging_dir="/opt/kubelet-proxy-staging"

    # Build binary.
    local repo_root="$(dirname "$(dirname "$K8S_NODE_DIR")")"
    CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -C "$repo_root" \
        -ldflags "-s -w" \
        -o "$proxy_dir/bin/kubelet-proxy-linux-amd64" \
        github.com/azure/azure-cleanroom/src/k8s-node/kubelet-proxy/cmd/kubelet-proxy
    log_info "Binary built"

    # Copy files to VM. install.sh --env aks stages aks/configure.sh, so the
    # aks/ subdir must be present alongside install.sh.
    local policy_file="$proxy_dir/scripts/api-policies/default-api-policy.json"
    ssh $ssh_opts azureuser@$VM_PUBLIC_IP \
        "sudo mkdir -p $staging_dir/aks && sudo chown -R azureuser:azureuser $staging_dir"
    scp $ssh_opts "$proxy_dir/bin/kubelet-proxy-linux-amd64" azureuser@$VM_PUBLIC_IP:$staging_dir/kubelet-proxy
    scp $ssh_opts "$proxy_dir/scripts/install.sh" azureuser@$VM_PUBLIC_IP:$staging_dir/install.sh
    scp $ssh_opts "$proxy_dir/scripts/aks/configure.sh" azureuser@$VM_PUBLIC_IP:$staging_dir/aks/configure.sh
    scp $ssh_opts "$policy_file" azureuser@$VM_PUBLIC_IP:$staging_dir/api-policy.json

    # Run install.sh — places binary + systemd unit + kubelet port drop-in +
    # stages aks/configure.sh (image-prep phase).
    log_info "Running kubelet-proxy install.sh..."
    ssh $ssh_opts azureuser@$VM_PUBLIC_IP "sudo bash $staging_dir/install.sh \
        --env aks \
        --local-binary $staging_dir/kubelet-proxy"

    # Run configure.sh — generates certs, installs policy, starts service (boot phase).
    log_info "Running kubelet-proxy configure.sh..."
    ssh $ssh_opts azureuser@$VM_PUBLIC_IP "sudo bash $staging_dir/configure.sh \
        --api-policy-file $staging_dir/api-policy.json \
        --ca-cert-file /etc/api-server-proxy/api-server-proxy.crt \
        --ca-key-file /etc/api-server-proxy/api-server-proxy.key"
    log_info "kubelet-proxy is running on :10250 (kubelet will be on :10251)"
}

# Inject proxy address into the flex-node config so the agent writes
# a kubeconfig pointing kubelet at the proxy from the start.
# Simulates cleanroom-boot's flex-node config injection.
inject_proxy_into_flex_config() {
    log_info "Injecting proxy address into flex-node config..."

    local ssh_opts="-i $SSH_PRIVATE_KEY_FILE -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null"
    local config_file="$GENERATED_DIR/aks-flex-node-config.json"

    # Get the proxy's self-signed cert (base64) from the VM.
    local proxy_ca_b64
    proxy_ca_b64=$(ssh $ssh_opts azureuser@$VM_PUBLIC_IP \
        "sudo base64 -w0 /etc/api-server-proxy/api-server-proxy.crt")

    # Inject serverURL and caCertData into the local config file
    # (before it's copied to the VM by install_aks_flex_node).
    local updated
    updated=$(jq --arg url "https://127.0.0.1:6444" \
                 --arg ca "$proxy_ca_b64" \
                 '.node.kubelet.serverURL = $url | .node.kubelet.caCertData = $ca' \
                 "$config_file")
    echo "$updated" > "$config_file"
    log_info "flex-node config updated: serverURL=https://127.0.0.1:6444"
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
    if [[ "${GITHUB_ACTIONS:-}" == "true" ]]; then
        log_info "Running in GitHub Actions - generating unique resource names"
    else
        get_current_user
    fi
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

    # --- Pre-kubelet proxy setup (simulates cleanroom-boot) ---
    uninstall_existing_proxies
    resolve_api_server_info
    deploy_api_server_proxy_to_vm
    deploy_kubelet_proxy_to_vm
    inject_proxy_into_flex_config
    # --- End proxy setup ---

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
        --max-pods-per-node)
            MAX_PODS_PER_NODE="$2"
            shift 2
            ;;
        *)
            log_error "Unknown option: $1"
            usage
            ;;
    esac
done

main
