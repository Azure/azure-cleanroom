#!/bin/bash

open_network() {

    TOOLS_DIR="$(dirname "$(realpath "${BASH_SOURCE[0]}")")"
    source $TOOLS_DIR/install_az_cli_extension.sh && install_az_cli_extension
    source $TOOLS_DIR/resolve_ccf_workspace.sh && resolve_ccf_workspace

    if [ -z "$CCF_NETWORK_ID" ]; then
        read -p "Enter the CCF network ID: " CCF_NETWORK_ID
        echo "CCF_NETWORK_ID=$CCF_NETWORK_ID" >> $CCF_ENV_FILE
    fi

    source $TOOLS_DIR/show_network.sh
    NETWORK_INFO=$(show_network)
    if [ -z "$NETWORK_INFO" ]; then
        echo "Attempting to open network which doesn't exist"
        return 1
    fi
    export CCF_ENDPOINT=$(echo $NETWORK_INFO | jq -r '.endpoint')

    source $TOOLS_DIR/gen_provider_config.sh && gen_provider_config

    # For SNP (caci) deployments, configure the join policy before opening the network
    # so that nodes can join.
    INFRA_TYPE=$(echo $NETWORK_INFO | jq -r '.infraType')
    if [ "$INFRA_TYPE" == "caci" ]; then
        # Set minimum TCB versions for Milan, Genoa and Turin platforms.
        for CPUID in "00a00f11" "00a10f11" "00b00f21"; do
            echo "Setting minimum TCB version for CPUID $CPUID."
            az cleanroom ccf network join-policy set-snp-minimum-tcb-version \
                --name $CCF_NETWORK_ID \
                --cpuid $CPUID \
                --tcb-version "0300000000000003" \
                --provider-config $CCF_WORKSPACE/provider_config.json \
                || echo "WARNING: Failed to set minimum TCB version for CPUID $CPUID. The CCF version may not support this platform yet."
        done

        # Add Azure Confidential ACI UVM endorsement.
        echo "Adding Azure UVM endorsement."
        az cleanroom ccf network join-policy add-snp-uvm-endorsement \
            --name $CCF_NETWORK_ID \
            --did "did:x509:0:sha256:I__iuL25oXEVFdTP_aBLx_eT1RPHbCQ_ECBQfYZpt9s::eku:1.3.6.1.4.1.311.76.59.1.2" \
            --feed "ContainerPlat-AMD-UVM" \
            --svn "0" \
            --provider-config $CCF_WORKSPACE/provider_config.json \
            || echo "WARNING: Failed to add Azure UVM endorsement. The CCF version may not support this feature."
    fi

    NETWORK_STATUS=$(curl -sk $CCF_ENDPOINT/node/network | jq -r '.service_status')
    echo $NETWORK_STATUS
    if [ "$NETWORK_STATUS" != "Open" ]; then
        az cleanroom ccf network transition-to-open \
            --name $CCF_NETWORK_ID \
            --provider-config $CCF_WORKSPACE/provider_config.json
    fi
}

if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
  open_network
fi
