#!/bin/bash

gen_provider_config() {

    # Get the directory of this script
    TOOLS_DIR="$(dirname "$(realpath "${BASH_SOURCE[0]}")")"
    source $TOOLS_DIR/resolve_ccf_workspace.sh && resolve_ccf_workspace

    if [ -z "$RESOURCE_GROUP" ]; then
        read -p "Enter the resource group name: " RESOURCE_GROUP
        echo "RESOURCE_GROUP=$RESOURCE_GROUP" >> $CCF_ENV_FILE
    fi

    if [ -z "$LOCATION" ]; then
        read -p "Enter the location to deploy to: " LOCATION
        echo "LOCATION=$LOCATION" >> $CCF_ENV_FILE
    fi

    if [ -z "$CCF_NETWORK_ID" ]; then
        read -p "Enter the CCF network ID: " CCF_NETWORK_ID
        echo "CCF_NETWORK_ID=$CCF_NETWORK_ID" >> $CCF_ENV_FILE
    fi

    if [ -z "$STORAGE_ACCOUNT_ID" ]; then
        source $TOOLS_DIR/deploy_ccf_storage_account.sh
        STORAGE_DEPLOYMENT_NAME="${CCF_NETWORK_ID}storage" deploy_ccf_storage_account
    fi

    if [ -z "$SUBSCRIPTION" ]; then
        export SUBSCRIPTION=$(az account show --query "id" --output tsv)
        echo "SUBSCRIPTION=$SUBSCRIPTION" >> $CCF_ENV_FILE
    fi

    cat <<EOF > "$CCF_WORKSPACE/provider_config.json"
{
    "location": "$LOCATION",
    "subscriptionId": "$SUBSCRIPTION",
    "resourceGroupName": "$RESOURCE_GROUP",
    "azureFiles": {
        "storageAccountId": "$STORAGE_ACCOUNT_ID"
    }
}
EOF
}

if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
  gen_provider_config
  echo "Provider config saved at: $CCF_WORKSPACE/provider_config.json"
fi
