#!/bin/bash

activate_member() {

    TOOLS_DIR="$(dirname "$(realpath "${BASH_SOURCE[0]}")")"
    source $TOOLS_DIR/install_az_cli_extension.sh && install_az_cli_extension
    source $TOOLS_DIR/resolve_ccf_workspace.sh && resolve_ccf_workspace

    if [ -z "$CCF_ID_NAME" ]; then
        read -p "Enter the name of the identity: " CCF_ID_NAME
    fi

    source $TOOLS_DIR/deploy_gov_client.sh && deploy_gov_client

    IDENTITY_STATUS=$(curl -sk $CCF_ENDPOINT/gov/members \
        | jq -r '.[] | select(.member_data.identifier == "${CCF_ID_NAME}") | .status')
    if [ "$IDENTITY_STATUS" != "Active" ]; then
        az cleanroom governance member activate --governance-client "${CCF_ID_NAME}-client"
    fi

    source $TOOLS_DIR/set_signer.sh && set_signer
}

if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
  activate_member
fi
