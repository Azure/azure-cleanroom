#!/bin/bash

create_identity() {

    TOOLS_DIR="$(dirname "$(realpath "${BASH_SOURCE[0]}")")"
    source $TOOLS_DIR/install_az_cli_extension.sh && install_az_cli_extension
    source $TOOLS_DIR/resolve_ccf_workspace.sh && resolve_ccf_workspace

    if [ -z "$CCF_ID_NAME" ]; then
        read -p "Enter the name of the identity: " CCF_ID_NAME
    fi

    if compgen -G "$CCF_WORKSPACE/$CCF_ID_NAME*" > /dev/null; then
        read -p "Identity already exists in this directory, overwrite? (y/n): " RESPONSE
        if [[ "$RESPONSE" != "y" ]]; then
            return 0
        fi
    fi

    az cleanroom governance member keygenerator-sh \
        | bash -s -- --name "$CCF_ID_NAME" --gen-enc-key --out $CCF_WORKSPACE
}

if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    create_identity
fi
