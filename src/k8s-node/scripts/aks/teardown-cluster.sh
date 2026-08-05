#!/bin/bash
#
# Tears down the AKS test cluster and cleans up resources.
#
# This deletes the Azure resource group containing the AKS cluster,
# the flex node VM, and all associated resources.
#
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"

main() {
    log_info "=========================================="
    log_info "  AKS Cluster Teardown"
    log_info "=========================================="
    echo ""

    read_cluster_config

    log_info "Deleting resource group: $RESOURCE_GROUP"
    log_warn "This will delete ALL resources in the group (cluster, VMs, etc.)"
    echo ""

    az group delete \
        --name "$RESOURCE_GROUP" \
        --yes \
        --no-wait \
        --output none 2>/dev/null || true

    log_info "Resource group deletion initiated (--no-wait)"

    # Clean up local generated files.
    log_info "Cleaning up local generated files..."
    rm -rf "$SHARED_AKS_GENERATED_DIR"

    log_info "Cleanup complete!"
}

main "$@"
