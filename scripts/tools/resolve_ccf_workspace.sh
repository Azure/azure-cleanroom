#!/bin/bash

resolve_ccf_workspace() {

    # Get the directory of this script
    TOOLS_DIR="$(dirname "$(realpath "${BASH_SOURCE[0]}")")"

    if [ -z "$CCF_WORKSPACE" ]; then
        read -p "Enter the path to the CCF workspace: " CCF_WORKSPACE
    fi
    mkdir -p $CCF_WORKSPACE

    # Define the environment file for this workspace
    export CCF_ENV_FILE="$CCF_WORKSPACE/.env"
    touch $CCF_ENV_FILE && source $CCF_ENV_FILE
}

if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    resolve_ccf_workspace
fi
