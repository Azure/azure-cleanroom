#!/bin/bash
set -e

AKS_FLEX_NODE_VERSION="v0.0.17"

# Cleanup previous aks-flex-node setup if any
echo "Running aks-flex-node uninstall script to cleanup previous setup..."
curl -fsSL https://raw.githubusercontent.com/Azure/AKSFlexNode/refs/tags/${AKS_FLEX_NODE_VERSION}/scripts/uninstall.sh -o /tmp/aks-flex-node-uninstall.sh
sed -i 's/^\([[:space:]]*\)remove_azure_cli$/\1#remove_azure_cli/' /tmp/aks-flex-node-uninstall.sh
sudo bash /tmp/aks-flex-node-uninstall.sh --force || true

# Create config directory and write config file
echo "Creating config directory..."
sudo mkdir -p /etc/aks-flex-node

echo "Writing config file..."
cat << 'CONFIGEOF' | sudo tee /etc/aks-flex-node/config.json > /dev/null
{{CONFIG_JSON}}
CONFIGEOF

# Run the AKS Flex Node install script
echo "Running AKS Flex Node install script..."
curl -fsSL https://raw.githubusercontent.com/Azure/AKSFlexNode/refs/tags/${AKS_FLEX_NODE_VERSION}/scripts/install.sh -o /tmp/aks-flex-node-install.sh

# Remove az cli related steps as its not needed for MI based setup.
sed -i "s/version=\$(get_latest_release)/version=\"${AKS_FLEX_NODE_VERSION}\"/" /tmp/aks-flex-node-install.sh
sed -i 's/^\([[:space:]]*\)install_azure_cli$/\1#install_azure_cli/' /tmp/aks-flex-node-install.sh
sed -i 's/^\([[:space:]]*\)check_azure_cli_auth$/\1#check_azure_cli_auth/' /tmp/aks-flex-node-install.sh
sed -i 's/^\([[:space:]]*\)setup_permissions$/\1#setup_permissions/' /tmp/aks-flex-node-install.sh
sudo bash /tmp/aks-flex-node-install.sh

# Enable and start the aks-flex-node-agent service
echo "Enabling and starting aks-flex-node-agent service..."
sudo systemctl enable --now aks-flex-node-agent

# Wait for status.json to appear and kubelet to be ready
echo "Waiting for aks-flex-node to become ready..."
status_file="/run/aks-flex-node/status.json"
max_wait=300
wait_interval=10
elapsed=0

while [[ $elapsed -lt $max_wait ]]; do
    if ! systemctl is-active --quiet aks-flex-node-agent; then
        service_status=$(systemctl is-active aks-flex-node-agent 2>/dev/null || echo "unknown")
        if [[ "$service_status" == "failed" || "$service_status" == "inactive" ]]; then
            echo "ERROR: aks-flex-node-agent service has stopped (status: $service_status)"
            journalctl -u aks-flex-node-agent --since "5 minutes ago" --no-pager
            exit 1
        fi
    else
        # Service is running, show last 3 lines of logs.
        echo "Last 3 lines of aks-flex-node-agent logs:"
        journalctl -u aks-flex-node-agent --no-pager -n 3

        # Show unique error lines (deduplicated by msg= value).
        log_file="/var/log/aks-flex-node/aks-flex-node.log"
        if [[ -f "$log_file" ]]; then
            errors=$(grep 'level=error' "$log_file" \
                | sed -n 's/.*\(msg="[^"]*"\).*/\1/p' \
                | sort -u \
                | while read -r msg_key; do
                    grep -F "$msg_key" "$log_file" | head -1
                done)
            if [[ -n "$errors" ]]; then
                echo "Unique error lines from $log_file:"
                echo "$errors"
            fi
        fi
    fi

    if sudo test -f "$status_file"; then
        kubelet_running=$(sudo jq -r '.kubeletRunning' "$status_file" 2>/dev/null || echo "false")
        kubelet_ready=$(sudo jq -r '.kubeletReady' "$status_file" 2>/dev/null || echo "")
        echo "Status: kubeletRunning=$kubelet_running, kubeletReady=$kubelet_ready"
        if [[ "$kubelet_running" == "true" && "$kubelet_ready" == "Ready" ]]; then
            echo "AKS Flex Node is ready!"
            exit 0
        fi
    else
        echo "Waiting for $status_file to appear..."
    fi

    sleep $wait_interval
    elapsed=$((elapsed + wait_interval))
done

echo "ERROR: AKS Flex Node did not become ready within ${max_wait} seconds"
journalctl -u aks-flex-node-agent --since "5 minutes ago" --no-pager
exit 1
