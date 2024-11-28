#!/bin/bash

deploy_gov_client() {

    TOOLS_DIR="$(dirname "$(realpath "${BASH_SOURCE[0]}")")"
    source $TOOLS_DIR/install_az_cli_extension.sh && install_az_cli_extension
    source $TOOLS_DIR/resolve_ccf_workspace.sh && resolve_ccf_workspace

    if [ -z "$CCF_ID_NAME" ]; then
        read -p "Enter the name of the identity: " CCF_ID_NAME
    fi

    if [ ! -f "$CCF_WORKSPACE/${CCF_ID_NAME}_cert.pem" ] || [ ! -f "$CCF_WORKSPACE/${CCF_ID_NAME}_privk.pem" ]; then
        echo "Attempting to deploy client for identity which doesn't exist"
        return 1
    fi

    source $TOOLS_DIR/show_network.sh
    NETWORK_INFO=$(show_network)
    if [ -z "$NETWORK_INFO" ]; then
        echo "Attempting to deploy client for network which doesn't exist"
        return 1
    fi
    export CCF_ENDPOINT=$(echo $NETWORK_INFO | jq -r '.endpoint')

    if [ ! -f "$CCF_WORKSPACE/service_cert.pem" ]; then
        source $TOOLS_DIR/get_service_cert.sh && get_service_cert
    fi

    az cleanroom governance client deploy \
        --ccf-endpoint $CCF_ENDPOINT \
        --signing-cert $CCF_WORKSPACE/${CCF_ID_NAME}_cert.pem \
        --signing-key $CCF_WORKSPACE/${CCF_ID_NAME}_privk.pem \
        --service-cert $CCF_WORKSPACE/service_cert.pem \
        --name "${CCF_ID_NAME}-client"
}

if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    deploy_gov_client
fi
