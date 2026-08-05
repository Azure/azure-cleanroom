#!/bin/bash
# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

# Adds a worker node to an existing Kind cluster. Inspired by
# https://github.com/lobuhi/kindscaler/blob/main/kindscaler.sh but scoped to
# worker-only and adapted for cleanroom flex node requirements.

set -euo pipefail

usage() {
    echo "Usage: $0 --cluster-name <name> --node-name <name> [--add-taint <key=value:effect>] [--provider-id <id>]"
    exit 1
}

CLUSTER_NAME=""
NODE_NAME=""
ADD_TAINT=""
PROVIDER_ID=""

while [[ "$#" -gt 0 ]]; do
    case $1 in
        --cluster-name) CLUSTER_NAME="$2"; shift ;;
        --node-name) NODE_NAME="$2"; shift ;;
        --add-taint) ADD_TAINT="$2"; shift ;;
        --provider-id) PROVIDER_ID="$2"; shift ;;
        *) echo "Unknown parameter: $1"; usage ;;
    esac
    shift
done

if [ -z "$CLUSTER_NAME" ] || [ -z "$NODE_NAME" ]; then
    echo "Error: --cluster-name and --node-name are required."
    usage
fi

# Find an existing worker node to copy kubeadm.conf from.
EXISTING_WORKER=""
for node in $(kind get nodes --name "$CLUSTER_NAME"); do
    if [[ $node == "$CLUSTER_NAME-worker"* ]]; then
        EXISTING_WORKER=$node
        break
    fi
done

if [ -z "$EXISTING_WORKER" ]; then
    echo "Error: No existing worker node found in cluster '$CLUSTER_NAME'."
    exit 1
fi

echo "Using existing worker '$EXISTING_WORKER' as template."

# Get the container image used by existing Kind nodes.
IMAGE=$(docker inspect "$EXISTING_WORKER" --format '{{.Config.Image}}')
echo "Node image: $IMAGE"

# Copy kubeadm.conf from existing worker.
WORK_DIR=$(mktemp -d)
trap 'rm -rf "$WORK_DIR"' EXIT

docker cp "$EXISTING_WORKER:/kind/kubeadm.conf" "$WORK_DIR/kubeadm.conf" 2>/dev/null

# Rewrite the node name in kubeadm.conf.
sed -i "s/${EXISTING_WORKER}/${NODE_NAME}/g" "$WORK_DIR/kubeadm.conf"

# Extract the original IP address from the kubeadm config.
ORIGINAL_IP=$(grep -oP '(advertiseAddress|node-ip):\s*\K([0-9]{1,3}(\.[0-9]{1,3}){3})' \
    "$WORK_DIR/kubeadm.conf" | head -1)
echo "Original template IP: $ORIGINAL_IP"

# Clear any taints inherited from the template worker's kubeadm.conf so that
# only explicitly requested taints are applied to the new node.
yq -i '(select(.kind == "JoinConfiguration") | .nodeRegistration.taints) = []' "$WORK_DIR/kubeadm.conf"

# Inject flex-node taint into kubeadm.conf if requested.
if [ -n "$ADD_TAINT" ]; then
    # Parse taint: key=value:effect
    TAINT_KEY=$(echo "$ADD_TAINT" | sed 's/=.*//')
    TAINT_VALUE=$(echo "$ADD_TAINT" | sed 's/[^=]*=//;s/:.*//')
    TAINT_EFFECT=$(echo "$ADD_TAINT" | sed 's/.*://')

    # Insert taints into the JoinConfiguration document's
    # nodeRegistration section using yq.
    yq -i '(select(.kind == "JoinConfiguration") | .nodeRegistration.taints) += [{"key": "'"$TAINT_KEY"'", "value": "'"$TAINT_VALUE"'", "effect": "'"$TAINT_EFFECT"'"}]' "$WORK_DIR/kubeadm.conf"
    echo "Injected taint: $ADD_TAINT"
fi

# Inject provider-id into kubelet extra args if specified.
if [ -n "$PROVIDER_ID" ]; then
    yq -i '(select(.kind == "JoinConfiguration") | .nodeRegistration.kubeletExtraArgs.provider-id) = "'"$PROVIDER_ID"'"' "$WORK_DIR/kubeadm.conf"
    echo "Injected provider-id: $PROVIDER_ID"
fi

# Run the new node container with the same flags Kind uses.
echo "Starting container '$NODE_NAME'..."
docker run \
    --name "$NODE_NAME" \
    --hostname "$NODE_NAME" \
    --label io.x-k8s.kind.role=worker \
    --label io.x-k8s.kind.cluster="$CLUSTER_NAME" \
    --privileged \
    --security-opt seccomp=unconfined \
    --security-opt apparmor=unconfined \
    --tmpfs /tmp \
    --tmpfs /run \
    --volume /var \
    --volume /lib/modules:/lib/modules:ro \
    -e KIND_EXPERIMENTAL_CONTAINERD_SNAPSHOTTER \
    --detach \
    --tty \
    --net kind \
    --restart=on-failure:1 \
    --init=false \
    "$IMAGE" > /dev/null

# Get the new container's IP on the kind network.
NEW_IP=$(docker inspect "$NODE_NAME" \
    --format '{{(index .NetworkSettings.Networks "kind").IPAddress}}')
echo "New node IP: $NEW_IP"

# Replace the template IP with the actual IP.
sed -i -r "s/${ORIGINAL_IP}/${NEW_IP}/g" "$WORK_DIR/kubeadm.conf"

# Copy the updated kubeadm.conf into the new container.
docker cp "$WORK_DIR/kubeadm.conf" "$NODE_NAME:/kind/kubeadm.conf" 2>/dev/null

# Wait briefly for the container to fully initialize.
sleep 5

# Ensure containerd is configured to read the registry hosts directory.
# Nodes created by `kind create cluster` have config_path set but
# dynamically-added worker nodes do not, causing image pull failures from
# local registries.
if ! docker exec "$NODE_NAME" grep -q config_path /etc/containerd/config.toml; then
    echo "Patching containerd config_path on '$NODE_NAME'..."
    docker exec "$NODE_NAME" bash -c \
        'printf "\n[plugins.\"io.containerd.grpc.v1.cri\".registry]\n  config_path = \"/etc/containerd/certs.d\"\n" >> /etc/containerd/config.toml'
    docker exec "$NODE_NAME" systemctl restart containerd
    sleep 2
fi

# Configure the local container registry mirror (ccr-registry) so the node
# can pull images from localhost:5000 via the ccr-registry container.
REGISTRY_NAME="ccr-registry"
if docker inspect "$REGISTRY_NAME" &>/dev/null; then
    REGISTRY_PORT="5000"
    REGISTRY_DIR="/etc/containerd/certs.d/localhost:${REGISTRY_PORT}"
    docker exec "$NODE_NAME" mkdir -p "$REGISTRY_DIR"
    echo "[host.\"http://${REGISTRY_NAME}:${REGISTRY_PORT}\"]" | \
        docker exec -i "$NODE_NAME" tee "$REGISTRY_DIR/hosts.toml" >/dev/null
    echo "Configured $REGISTRY_NAME mirror on '$NODE_NAME'."
fi

# Join the cluster.
echo "Joining node '$NODE_NAME' to cluster '$CLUSTER_NAME'..."
docker exec --privileged "$NODE_NAME" \
    kubeadm join --config /kind/kubeadm.conf --skip-phases=preflight --v=6

echo "Node '$NODE_NAME' successfully added to cluster '$CLUSTER_NAME'."
