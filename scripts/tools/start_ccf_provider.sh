#!/bin/bash

start_ccf_provider() {

    TOOLS_DIR="$(dirname "$(realpath "${BASH_SOURCE[0]}")")"
    source $TOOLS_DIR/install_az_cli_extension.sh && install_az_cli_extension

    if ! docker ps \
        --filter "ancestor=mcr.microsoft.com/cleanroom/ccf/ccf-provider-client:latest" \
        --filter "ancestor=cleanroombuild.azurecr.io/workleap/azure-cli-credentials-proxy:1.1.0" \
        --format '{{.Names}}' | grep -q .; then
        az cleanroom ccf provider deploy
    else
        return 1
    fi
}

if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    if ! start_ccf_provider; then
        echo "CCF provider already started"
    fi
fi
