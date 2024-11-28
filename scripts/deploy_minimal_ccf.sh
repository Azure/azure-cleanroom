#!/bin/bash

# Deploys the simplest possible C-ACI based CCF network, with a single operator, further members can
# be added later. The key benefit of this script is it's a single call to get an entire network
# created with all relevant configuration options, and identities stored in a workspace directory.

SCRIPT_DIR="$(dirname "$(realpath "${BASH_SOURCE[0]}")")"
source $SCRIPT_DIR/tools/install_az_cli_extension.sh && install_az_cli_extension
source $SCRIPT_DIR/tools/resolve_ccf_workspace.sh && resolve_ccf_workspace

# Check if the network already exists, otherwise create one
source $SCRIPT_DIR/tools/show_network.sh && export NETWORK_INFO=$(show_network)
if [ -n "$NETWORK_INFO" ]; then
    echo "Network already exists"
    echo $NETWORK_INFO | jq
else
    echo "Creating operator identity and members configuration..."
    source $SCRIPT_DIR/tools/create_identity.sh && CCF_ID_NAME="operator" create_identity
    source $SCRIPT_DIR/tools/gen_members_config.sh && gen_members_config

    echo "Creating new CCF network..."
    if [ -z "$CCF_NETWORK_ID" ]; then
        read -p "Enter the CCF network ID: " CCF_NETWORK_ID
        echo "CCF_NETWORK_ID=$CCF_NETWORK_ID" >> $CCF_ENV_FILE
    fi
    az cleanroom ccf network create \
        --name $CCF_NETWORK_ID \
        --members $CCF_WORKSPACE/members_config.json \
        --provider-config $CCF_WORKSPACE/provider_config.json
    export NETWORK_INFO=$(show_network)
fi

source $SCRIPT_DIR/tools/activate_member.sh && CCF_ID_NAME="operator" activate_member

source $SCRIPT_DIR/tools/open_network.sh && open_network