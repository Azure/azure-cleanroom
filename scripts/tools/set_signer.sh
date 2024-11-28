#!/bin/bash

set_signer() {

    TOOLS_DIR="$(dirname "$(realpath "${BASH_SOURCE[0]}")")"
    source $TOOLS_DIR/install_az_cli_extension.sh && install_az_cli_extension
    source $TOOLS_DIR/resolve_ccf_workspace.sh && resolve_ccf_workspace

    if [ -z "$CCF_ID_NAME" ]; then
        read -p "Enter the name of the identity: " CCF_ID_NAME
    fi

    if [ ! -f "$CCF_WORKSPACE/${CCF_ID_NAME}_cert.pem" ] || [ ! -f "$CCF_WORKSPACE/${CCF_ID_NAME}_privk.pem" ]; then
        echo "Attempting to set signer as identity which doesn't exist"
        return 1
    fi

    az cleanroom ccf provider configure \
        --signing-cert $CCF_WORKSPACE/${CCF_ID_NAME}_cert.pem \
        --signing-key $CCF_WORKSPACE/${CCF_ID_NAME}_privk.pem 2>/dev/null
}

if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
  set_signer
fi
