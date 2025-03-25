#!/bin/bash
set -o errexit

# Script adapted from https://kind.sigs.k8s.io/docs/user/local-registry/

# 1. Create registry container unless it already exists
reg_name='ccr-registry'
cluster_name='cleanroom'
reg_port='5000'
BASEDIR=$(dirname "$0")
reg_image='registry:2.7'
if [[ "${GITHUB_ACTIONS}" = "true" ]]; then
    reg_image='cleanroombuild.azurecr.io/registry:2.7'
fi
if [ "$(docker inspect -f '{{.State.Running}}' "${reg_name}" 2>/dev/null || true)" != 'true' ]; then
  docker run \
    -d --restart=always -p "127.0.0.1:${reg_port}:5000" --network bridge --name "${reg_name}" \
    "${reg_image}"
fi

node_image='kindest/node:v1.32.2'
if [[ "${GITHUB_ACTIONS}" = "true" ]]; then
    node_image='cleanroombuild.azurecr.io/kindest/node:v1.32.2'
fi

# 2. Create kind cluster with containerd registry config dir enabled
kind create cluster --config=$BASEDIR/kind-config.yaml --name $cluster_name --image "${node_image}"

# 3. Add the registry config to the nodes
#
# This is necessary because localhost resolves to loopback addresses that are
# network-namespace local.
# In other words: localhost in the container is not localhost on the host.
#
# We want a consistent name that works from both ends, so we tell containerd to
# alias localhost:${reg_port} to the registry container when pulling images
REGISTRY_DIR="/etc/containerd/certs.d/localhost:${reg_port}"
for node in $(kind get nodes --name $cluster_name); do
  docker exec "${node}" mkdir -p "${REGISTRY_DIR}"
  cat <<EOF | docker exec -i "${node}" cp /dev/stdin "${REGISTRY_DIR}/hosts.toml"
[host."http://${reg_name}:5000"]
EOF
done

# 4. Connect the registry to the cluster network if not already connected
# This allows kind to bootstrap the network but ensures they're on the same network
if [ "$(docker inspect -f='{{json .NetworkSettings.Networks.kind}}' "${reg_name}")" = 'null' ]; then
  docker network connect "kind" "${reg_name}"
fi

# 5. Document the local registry
# https://github.com/kubernetes/enhancements/tree/master/keps/sig-cluster-lifecycle/generic/1755-communicating-a-local-registry
cat <<EOF | kubectl apply -f -
apiVersion: v1
kind: ConfigMap
metadata:
  name: local-registry-hosting
  namespace: kube-public
data:
  localRegistryHosting.v1: |
    host: "localhost:${reg_port}"
    help: "https://kind.sigs.k8s.io/docs/user/local-registry/"
EOF

# 6. Create a config map for the local registry to indicate to the containers to access it with
# "http" instead of "https".
# This config map can be mounted to the containers at /etc/containers/registry.conf.d/.
# https://github.com/containers/podman/blob/main/troubleshooting.md#4-http-server-gave-http-response-to-https-client
cat <<EOF | kubectl apply -f -
apiVersion: v1
kind: ConfigMap
metadata:
  name: ccr-registry-conf
  namespace: default
data:
  ccr-registry.conf: |
    [[registry]]
    location = "${reg_name}:5000"
    insecure = true
EOF
