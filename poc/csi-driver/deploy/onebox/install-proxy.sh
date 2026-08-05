#!/bin/bash
# install-proxy.sh - Runs as a DaemonSet init container.
# Copies the blobfuse-proxy binary, blobfuse2 binary, and encryptor plugin to
# the host, writes a systemd service unit, and starts (or restarts) the service.
#
# TODO(node-prep): Long-term, node preparation (installing blobfuse2, the encryptor
# plugin, and the proxy service unit) should be handled by a dedicated node
# provisioning step rather than an init container. This approach requires
# privileged DaemonSet pods and host filesystem access; a node prep tool or
# image baked into the node image would be more robust and portable.
set -e

HOST_BIN_DIR=/opt/cleanroom/bin
HOST_LIB_DIR=/opt/cleanroom/lib
SYSTEMD_DIR=/etc/systemd/system
SOCKET_DIR=/run/blobfuse-proxy

echo "Installing blobfuse-proxy host service..."

mkdir -p "${HOST_BIN_DIR}" "${HOST_LIB_DIR}" "${SOCKET_DIR}"

# Compute checksum of the existing proxy binary before we overwrite it.
# Used below to decide whether a service restart is needed.
PROXY_CHECKSUM_BEFORE=""
if [ -f "${HOST_BIN_DIR}/blobfuse-proxy" ]; then
    PROXY_CHECKSUM_BEFORE=$(sha256sum "${HOST_BIN_DIR}/blobfuse-proxy" | awk '{print $1}')
fi

# Copy binaries from the container image to the host.
# Remove before copying: Linux raises ETXTBSY if we overwrite a running executable
# in-place. rm unlinks the directory entry (the running process keeps its inode),
# then cp creates a new file at the same path — safe and atomic enough here.
rm -f "${HOST_BIN_DIR}/blobfuse-proxy"
cp /app/blobfuse-proxy     "${HOST_BIN_DIR}/blobfuse-proxy"
rm -f "${HOST_BIN_DIR}/blobfuse2"
cp /usr/local/bin/blobfuse2 "${HOST_BIN_DIR}/blobfuse2"
rm -f "${HOST_LIB_DIR}/encryptor.so"
cp /app/encryptor.so       "${HOST_LIB_DIR}/encryptor.so"
chmod +x "${HOST_BIN_DIR}/blobfuse-proxy" "${HOST_BIN_DIR}/blobfuse2"

PROXY_CHECKSUM_AFTER=$(sha256sum "${HOST_BIN_DIR}/blobfuse-proxy" | awk '{print $1}')
PROXY_BINARY_CHANGED=true
if [ "${PROXY_CHECKSUM_BEFORE}" = "${PROXY_CHECKSUM_AFTER}" ]; then
    PROXY_BINARY_CHANGED=false
fi

echo "Binaries installed to ${HOST_BIN_DIR}"

# Write the systemd service unit file.
cat > "${SYSTEMD_DIR}/blobfuse-proxy.service" << 'SERVICE_EOF'
[Unit]
Description=blobfuse-proxy: blobfuse2 mount manager for Clean Room CSI driver
After=network.target

[Service]
ExecStart=/opt/cleanroom/bin/blobfuse-proxy --socket /run/blobfuse-proxy/blobfuse-proxy.sock
# KillMode=process: only kill the proxy process itself on stop/restart.
# Child blobfuse2 daemons are NOT killed and survive proxy restarts.
KillMode=process
Restart=on-failure
RestartSec=5
RuntimeDirectory=blobfuse-proxy
RuntimeDirectoryMode=0750

[Install]
WantedBy=multi-user.target
SERVICE_EOF

echo "Service unit written to ${SYSTEMD_DIR}/blobfuse-proxy.service"

# Reload systemd and enable/start the service on the host.
# nsenter -t 1 --mount --pid: enter the host's mount and PID namespaces
# so that systemctl talks to the host's PID 1 (systemd).
nsenter -t 1 --mount --pid -- systemctl daemon-reload
nsenter -t 1 --mount --pid -- systemctl enable blobfuse-proxy.service

if nsenter -t 1 --mount --pid -- systemctl is-active --quiet blobfuse-proxy.service; then
    if [ "${PROXY_BINARY_CHANGED}" = "true" ]; then
        echo "Restarting blobfuse-proxy service (binary changed)..."
        nsenter -t 1 --mount --pid -- systemctl restart blobfuse-proxy.service
    else
        echo "Skipping restart: blobfuse-proxy binary is unchanged."
    fi
else
    echo "Starting blobfuse-proxy service..."
    nsenter -t 1 --mount --pid -- systemctl start blobfuse-proxy.service
fi

echo "blobfuse-proxy host service is active."
