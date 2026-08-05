#!/bin/bash
set -e

# install-cli.sh - Install kubectl-cleanroom from an OCI registry.
#
# Usage:
#   ./install-cli.sh --repo <registry> --tag <version> [--install-location <path>]
#
# Examples:
#   ./install-cli.sh --repo myregistry.azurecr.io --tag 0.20.0
#   ./install-cli.sh --repo localhost:5000 --tag latest --install-location ~/bin/kubectl-cleanroom

INSTALL_LOCATION="${HOME}/.local/bin/kubectl-cleanroom"
ARTIFACT_NAME="kubectl-cleanroom-binary"
REPO=""
TAG=""

usage() {
    echo "Usage: $0 --repo <registry> --tag <version> [--install-location <path>]"
    echo ""
    echo "Options:"
    echo "  --repo               Container registry URL (required)"
    echo "  --tag                Image tag / version (required)"
    echo "  --install-location   Path to install the binary (default: ~/.local/bin/kubectl-cleanroom)"
    exit 1
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --repo)
            REPO="$2"
            shift 2
            ;;
        --tag)
            TAG="$2"
            shift 2
            ;;
        --install-location)
            INSTALL_LOCATION="$2"
            shift 2
            ;;
        -h|--help)
            usage
            ;;
        *)
            echo "Unknown option: $1"
            usage
            ;;
    esac
done

if [[ -z "$REPO" || -z "$TAG" ]]; then
    echo "Error: --repo and --tag are required."
    usage
fi

# Check for Docker (needed later by dev up / operator).
if ! command -v docker &>/dev/null; then
    echo "WARNING: Docker is not installed or not on PATH."
    echo "Docker is required to run the management cluster."
    echo "Install it from https://docs.docker.com/get-docker/"
fi

ORAS_VERSION="1.2.2"
ARTIFACT="${REPO}/${ARTIFACT_NAME}:${TAG}"
TMPDIR=$(mktemp -d)
trap 'rm -rf "$TMPDIR"' EXIT

# Ensure oras is available; download a temporary copy if not found.
if command -v oras &>/dev/null; then
    ORAS_BIN="oras"
else
    echo "oras CLI not found; downloading a temporary copy..."
    ARCH=$(uname -m)
    case "$ARCH" in
        x86_64)  ARCH="amd64" ;;
        aarch64) ARCH="arm64" ;;
    esac
    OS=$(uname -s | tr '[:upper:]' '[:lower:]')
    ORAS_URL="https://github.com/oras-project/oras/releases/download"
    ORAS_URL="${ORAS_URL}/v${ORAS_VERSION}/oras_${ORAS_VERSION}_${OS}_${ARCH}.tar.gz"
    curl -fsSL "$ORAS_URL" | tar xz -C "$TMPDIR" oras
    ORAS_BIN="$TMPDIR/oras"
    echo "Using temporary oras from ${ORAS_BIN}"
fi

echo "Downloading kubectl-cleanroom from ${ARTIFACT} ..."
"$ORAS_BIN" pull "$ARTIFACT" --output "$TMPDIR"

if [[ ! -f "$TMPDIR/kubectl-cleanroom" ]]; then
    echo "Error: kubectl-cleanroom binary not found in artifact."
    exit 1
fi

# Ensure target directory exists.
INSTALL_DIR=$(dirname "$INSTALL_LOCATION")
mkdir -p "$INSTALL_DIR"

mv "$TMPDIR/kubectl-cleanroom" "$INSTALL_LOCATION"
chmod +x "$INSTALL_LOCATION"

echo "kubectl-cleanroom installed to ${INSTALL_LOCATION}"

# Check if it's on PATH.
if ! command -v kubectl-cleanroom &>/dev/null; then
    echo "WARNING: ${INSTALL_DIR} is not on your PATH."
    echo "Add it with: export PATH=\"${INSTALL_DIR}:\$PATH\""
fi
