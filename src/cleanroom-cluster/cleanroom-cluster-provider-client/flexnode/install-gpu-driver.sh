#!/bin/bash
# Installs NVIDIA GPU driver for confidential computing (CC) mode on H100.
# Reference: https://docs.nvidia.com/datacenter/cloud-native/gpu-operator/latest/gpu-operator-confidential-containers.html
set -e

echo "Installing NVIDIA GPU driver for confidential computing..."

# LKCA (Linux Kernel Crypto API) must be configured BEFORE driver install.
# This modprobe hook loads ecdsa_generic and ecdh before the nvidia module,
# which is required for CC (Confidential Computing) mode on H100.
sudo apt-get update
sudo apt-get install -y initramfs-tools

echo "install nvidia /sbin/modprobe ecdsa_generic ecdh; /sbin/modprobe --ignore-install nvidia" \
  | sudo tee /etc/modprobe.d/nvidia-lkca.conf > /dev/null

sudo update-initramfs -u -k $(uname -r)

# Install open-source driver (required for CC mode).
sudo apt-get install -y nvidia-driver-580-server-open \
  linux-modules-nvidia-580-server-open-$(uname -r)

# Enable persistence mode and set secure reset.
sudo nvidia-smi -pm 1
sudo nvidia-smi conf-compute -srs 1

# Verify driver and CC mode.
nvidia-smi
nvidia-smi conf-compute -f

# Install OpenSSL 3.4.1 to /opt/openssl for improved CPU-GPU bandwidth.
# The encrypted PCIe channel between CPU and H100 in CC mode uses OpenSSL
# for encryption. OpenSSL 3.4.1 (AVX512) doubles CPU-GPU bandwidth from
# ~4.4 GB/s to ~8.6-10 GB/s compared to the default OpenSSL 3.0.2.
# See: https://github.com/Azure/az-cgpu-onboarding/blob/main/docs/OpenSSL-Details.md
echo "Installing OpenSSL 3.4.1 for improved CPU-GPU bandwidth..."
OPENSSL_VER="3.4.1"
sudo apt-get install -y wget tar build-essential
wget "https://www.openssl.org/source/openssl-${OPENSSL_VER}.tar.gz"
tar -zxf "openssl-${OPENSSL_VER}.tar.gz"
cd "openssl-${OPENSSL_VER}"
./Configure --prefix=/opt/openssl --openssldir=/usr/local/ssl
make -j$(nproc)
sudo make install
cd ..
rm -rf "openssl-${OPENSSL_VER}" "openssl-${OPENSSL_VER}.tar.gz"
echo "OpenSSL 3.4.1 installed to /opt/openssl."

echo "GPU driver installation completed successfully."