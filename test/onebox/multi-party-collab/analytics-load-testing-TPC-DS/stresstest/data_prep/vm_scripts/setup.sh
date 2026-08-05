#!/bin/bash
set -e

if ! mountpoint -q /data; then
    echo "Available block devices:"
    lsblk -o NAME,SIZE,TYPE,MOUNTPOINT,FSTYPE
    # Largest unmounted disk on the VM is the data disk (skip sda OS / sdb temp).
    DISK=$(lsblk -dnpo NAME,SIZE,TYPE,MOUNTPOINT | awk '$3=="disk" && $4==""' | sort -k2 -h -r | head -1 | awk '{print $1}')
    if [ -z "$DISK" ]; then
        echo "ERROR: No unmounted data disk found."
        lsblk -o NAME,SIZE,TYPE,MOUNTPOINT
        exit 1
    fi
    echo "Formatting and mounting data disk: $DISK"
    sudo mkfs.ext4 -F "$DISK"
    sudo mkdir -p /data
    sudo mount "$DISK" /data
    sudo chown azureuser:azureuser /data
else
    echo "Data disk already mounted at /data."
fi

sudo apt-get update -qq
sudo apt-get install -y -qq build-essential gcc make python3-pip python3-venv curl

if ! command -v azcopy &> /dev/null; then
    cd /tmp
    curl -sL https://aka.ms/downloadazcopy-v10-linux | tar xz --strip-components=1
    sudo mv azcopy /usr/local/bin/
fi

if [ ! -d /data/.venv ]; then
    python3 -m venv /data/.venv
fi
source /data/.venv/bin/activate
pip install pyarrow --quiet

echo "Setup complete."
df -h /data
