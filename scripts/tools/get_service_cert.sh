#!/bin/bash

get_service_cert() {

    TOOLS_DIR="$(dirname "$(realpath "${BASH_SOURCE[0]}")")"
    source $TOOLS_DIR/resolve_ccf_workspace.sh && resolve_ccf_workspace

    source $TOOLS_DIR/show_network.sh
    export CCF_ENDPOINT=$(show_network | jq -r '.endpoint')

    curl -k "$CCF_ENDPOINT/node/network" --silent \
        | jq -r '.service_certificate' \
        | sed 's/\n$//' \
        > $CCF_WORKSPACE/service_cert.pem
}

if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    get_service_cert
    echo "Fetched service cert and saved in: $CCF_WORKSPACE/service.cert.pem"
fi
