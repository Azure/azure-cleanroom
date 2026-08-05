#!/bin/bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
    echo "usage: install-nvat.sh <version-file>" >&2
    exit 1
fi

version_file="$1"
if [[ ! -f "$version_file" ]]; then
    echo "NVAT version file not found: $version_file" >&2
    exit 1
fi

nvat_ref="$(tr -d ' \t\r\n' < "$version_file")"
if [[ -z "$nvat_ref" ]]; then
    echo "NVAT version file is empty: $version_file" >&2
    exit 1
fi

export DEBIAN_FRONTEND=noninteractive

apt-get update && \
    apt-get install -y --no-install-recommends \
        build-essential \
        ca-certificates \
        clang \
        cmake \
        curl \
        git \
        libclang-dev \
        libcurl4-openssl-dev \
        libssl-dev \
        libxml2-dev \
        libxmlsec1-dev \
        libxmlsec1-openssl \
        perl \
        pkg-config \
        zlib1g-dev && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y --profile minimal
export PATH="/root/.cargo/bin:${PATH}"

workdir="$(mktemp -d)"
trap 'rm -rf "$workdir"' EXIT

git clone --depth 1 --branch "${nvat_ref}" \
    https://github.com/NVIDIA/attestation-sdk.git \
    "${workdir}/attestation-sdk"

cmake \
    -S "${workdir}/attestation-sdk/nv-attestation-cli" \
    -B "${workdir}/attestation-sdk/nv-attestation-cli/build" \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX=/usr/local
cmake --build "${workdir}/attestation-sdk/nv-attestation-cli/build" --parallel
cmake --install "${workdir}/attestation-sdk/nv-attestation-cli/build"
