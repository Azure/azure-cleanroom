#!/bin/bash

show_network() {

    TOOLS_DIR="$(dirname "$(realpath "${BASH_SOURCE[0]}")")"
    source $TOOLS_DIR/install_az_cli_extension.sh && install_az_cli_extension
    source $TOOLS_DIR/resolve_ccf_workspace.sh && resolve_ccf_workspace

    source $TOOLS_DIR/start_ccf_provider.sh && start_ccf_provider

    if [ -z "$CCF_NETWORK_ID" ]; then
        read -p "Enter the CCF network ID: " CCF_NETWORK_ID
        echo "CCF_NETWORK_ID=$CCF_NETWORK_ID" >> $CCF_ENV_FILE
    fi

    if [ ! -f "$CCF_WORKSPACE/provider_config.json" ]; then
        source $TOOLS_DIR/gen_provider_config.sh && gen_provider_config
    fi

    az cleanroom ccf network show \
        --name $CCF_NETWORK_ID \
        --provider-config $CCF_WORKSPACE/provider_config.json 2>/dev/null
}

if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    show_network
fi
