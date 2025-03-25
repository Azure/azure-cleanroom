#!/bin/bash

deploy_ccf_storage_account() {

    # Get the directory of this script
    TOOLS_DIR="$(dirname "$(realpath "${BASH_SOURCE[0]}")")"
    source $TOOLS_DIR/resolve_ccf_workspace.sh && resolve_ccf_workspace

    if [ -z "$RESOURCE_GROUP" ]; then
        read -p "Enter the resource group name: " RESOURCE_GROUP
        echo "RESOURCE_GROUP=$RESOURCE_GROUP" >> $CCF_ENV_FILE
    fi

    if [ -z "$STORAGE_DEPLOYMENT_NAME" ]; then
        read -p "Enter the deployment name: " STORAGE_DEPLOYMENT_NAME
    fi

    if [ -z "$STORAGE_ACCOUNT_NAME" ] || [ -z "$STORAGE_ACCOUNT_ID" ]; then
        STORAGE_DEPLOYMENT_OUTPUTS=$(az deployment group create \
            --name $STORAGE_DEPLOYMENT_NAME \
            --resource-group $RESOURCE_GROUP \
            --template-file $(realpath $TOOLS_DIR/azure/ccfStorageAccount.bicep) \
            --query "properties.outputs")

        export STORAGE_ACCOUNT_NAME=$(echo "$STORAGE_DEPLOYMENT_OUTPUTS" | jq -r '.storageAccountName.value')
        echo "STORAGE_ACCOUNT_NAME=$STORAGE_ACCOUNT_NAME" >> $CCF_ENV_FILE

        export STORAGE_ACCOUNT_ID=$(echo "$STORAGE_DEPLOYMENT_OUTPUTS" | jq -r '.storageAccountID.value')
        echo "STORAGE_ACCOUNT_ID=$STORAGE_ACCOUNT_ID" >> $CCF_ENV_FILE
    fi
}

if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    deploy_ccf_storage_account

    echo "STORAGE_ACCOUNT_NAME: $STORAGE_ACCOUNT_NAME"
    echo "STORAGE_ACCOUNT_ID: $STORAGE_ACCOUNT_ID"
fi
