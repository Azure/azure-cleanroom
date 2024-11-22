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
