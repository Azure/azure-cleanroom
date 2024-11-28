#!/bin/bash

# Get the directory of this script
TOOLS_DIR="$(dirname "$(realpath "${BASH_SOURCE[0]}")")"

install_az_cli_extension() {
    # Check if Az CLI extension is installed
    if ! az cleanroom -h > /dev/null 2>&1; then
        pwsh "$TOOLS_DIR/../../build/build-azcliext-cleanroom.ps1"
    else
        return 1
    fi
}

# If the script is run directly, call the function and print the message if already installed
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    if ! install_az_cli_extension; then
        echo "Az CLI extension already installed"
    fi
fi
