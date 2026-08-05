#!/bin/bash
# Installs NVIDIA GPU driver for confidential computing (CC) mode on H100.
# Reference: https://docs.nvidia.com/datacenter/cloud-native/gpu-operator/latest/gpu-operator-confidential-containers.html
#
# Usage: install-gpu-driver.sh [--driver-version VERSION] [--driver-branch BRANCH]
#   --driver-version  NVIDIA driver version number (default: 580)
#   --driver-branch   NVIDIA driver branch (default: server-open)
set -e

DRIVER_VERSION="${NVIDIA_DRIVER_VERSION:-580}"
DRIVER_BRANCH="${NVIDIA_DRIVER_BRANCH:-server-open}"

while [[ $# -gt 0 ]]; do
    case $1 in
        --driver-version) DRIVER_VERSION="$2"; shift 2 ;;
        --driver-branch) DRIVER_BRANCH="$2"; shift 2 ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
done

DRIVER_PKG="nvidia-driver-${DRIVER_VERSION}-${DRIVER_BRANCH}"
MODULES_PKG="linux-modules-nvidia-${DRIVER_VERSION}-${DRIVER_BRANCH}-$(uname -r)"

echo "Installing NVIDIA GPU driver for confidential computing..."
echo "  Driver version : ${DRIVER_VERSION}"
echo "  Driver branch  : ${DRIVER_BRANCH}"
echo "  Driver package : ${DRIVER_PKG}"
echo "  Modules package: ${MODULES_PKG}"

# LKCA (Linux Kernel Crypto API) must be configured BEFORE driver install.
# This modprobe hook loads ecdsa_generic and ecdh before the nvidia module,
# which is required for CC (Confidential Computing) mode on H100.
apt-get update
apt-get install -y initramfs-tools

echo "install nvidia /sbin/modprobe ecdsa_generic ecdh; /sbin/modprobe --ignore-install nvidia" \
  > /etc/modprobe.d/nvidia-lkca.conf

update-initramfs -u -k "$(uname -r)"

# Install open-source driver (required for CC mode).
# DEBIAN_FRONTEND=noninteractive prevents the DKMS Secure Boot MOK
# enrollment prompt.
DEBIAN_FRONTEND=noninteractive apt-get install -y "${DRIVER_PKG}" "${MODULES_PKG}"

# Note: nvidia-smi persistence mode (-pm 1) and CC secure reset
# (conf-compute -srs 1) require a physical GPU and are handled by
# cleanroom-boot at boot on the target node.

# Install OpenSSL 3.4.1 to /opt/openssl for improved CPU-GPU bandwidth.
# The encrypted PCIe channel between CPU and H100 in CC mode uses OpenSSL
# for encryption. OpenSSL 3.4.1 (AVX512) doubles CPU-GPU bandwidth from
# ~4.4 GB/s to ~8.6-10 GB/s compared to the default OpenSSL 3.0.2.
# See: https://github.com/Azure/az-cgpu-onboarding/blob/main/docs/OpenSSL-Details.md
echo "Installing OpenSSL 3.4.1 for improved CPU-GPU bandwidth..."
OPENSSL_VER="3.4.1"
apt-get install -y wget tar build-essential
wget "https://www.openssl.org/source/openssl-${OPENSSL_VER}.tar.gz"
tar -zxf "openssl-${OPENSSL_VER}.tar.gz"
cd "openssl-${OPENSSL_VER}"
./Configure --prefix=/opt/openssl --openssldir=/usr/local/ssl
make -j"$(nproc)"
make install
cd ..
rm -rf "openssl-${OPENSSL_VER}" "openssl-${OPENSSL_VER}.tar.gz"
echo "OpenSSL 3.4.1 installed to /opt/openssl."

echo "GPU driver installation completed successfully."
