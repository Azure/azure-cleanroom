@description('The name of the AKS cluster')
param aksClusterName string

@description('The resource group containing the AKS cluster')
param aksResourceGroup string = resourceGroup().name

@description('The location to deploy the resources to')
param location string = resourceGroup().location

@description('The kubectl wait command parameters')
param waitCommand object

@description('How the deployment script should be forced to execute')
param forceUpdateTag string = utcNow()

@description('Tags to apply to the resources')
param tags object = {}

@description('ARM resource ID of the managed identity for deployment operations')
param managedIdentityId string

// Script to run kubectl wait command
resource kubectlWaitScript 'Microsoft.Resources/deploymentScripts@2023-08-01' = {
  name: 'kubectl-wait-${waitCommand.name}'
  location: location
  tags: tags
  kind: 'AzureCLI'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${managedIdentityId}': {}
    }
  }
  properties: {
    forceUpdateTag: forceUpdateTag
    azCliVersion: '2.50.0'
    timeout: 'PT${waitCommand.timeoutMinutes}M'
    retentionInterval: 'PT1H'
    cleanupPreference: 'OnSuccess'
    environmentVariables: [
      {
        name: 'AKS_CLUSTER_NAME'
        value: aksClusterName
      }
      {
        name: 'AKS_RESOURCE_GROUP'
        value: aksResourceGroup
      }
      {
        name: 'WAIT_NAMESPACE'
        value: waitCommand.namespace
      }
      {
        name: 'WAIT_RESOURCE_TYPE'
        value: waitCommand.resourceType
      }
      {
        name: 'WAIT_RESOURCE_NAME'
        value: waitCommand.resourceName
      }
      {
        name: 'WAIT_CONDITION'
        value: waitCommand.condition
      }
      {
        name: 'WAIT_TIMEOUT'
        value: '${waitCommand.timeoutMinutes}m'
      }
    ]
    scriptContent: '''
      set -e
      # set -x  # Enable command tracing

      echo "Starting kubectl wait operation..."
      echo "Cluster: $AKS_CLUSTER_NAME"
      echo "Resource Group: $AKS_RESOURCE_GROUP"
      echo "Namespace: $WAIT_NAMESPACE"
      echo "Resource: $WAIT_RESOURCE_TYPE/$WAIT_RESOURCE_NAME"
      echo "Condition: $WAIT_CONDITION"
      echo "Timeout: $WAIT_TIMEOUT"
      echo ""

      # Install kubectl using Azure CLI method
      echo "Installing kubectl..."
      # Use az aks install-cli which is available in Azure CLI environments
      az aks install-cli --install-location /usr/local/bin/kubectl
      
      # Make sure kubectl is executable
      chmod +x /usr/local/bin/kubectl
      
      # Verify kubectl installation
      echo "kubectl version:"
      kubectl version --client
      echo ""

      # Get AKS credentials
      echo "Getting AKS credentials..."
      az aks get-credentials --resource-group $AKS_RESOURCE_GROUP --name $AKS_CLUSTER_NAME --overwrite-existing

      # Wait for the resource to be ready with retry mechanism
      # This implements a workaround for https://github.com/kubernetes/kubectl/issues/1120
      # where kubectl wait can hang indefinitely if the object gets deleted during wait
      echo "Waiting for $WAIT_RESOURCE_TYPE/$WAIT_RESOURCE_NAME to be ready..."
      
      # Calculate total timeout in seconds
      TIMEOUT_MINUTES=${WAIT_TIMEOUT%m}
      TOTAL_TIMEOUT_SECONDS=$((TIMEOUT_MINUTES * 60))
      ELAPSED_SECONDS=0
      WAIT_INTERVAL=10
      
      echo "Total timeout: ${TOTAL_TIMEOUT_SECONDS}s, using ${WAIT_INTERVAL}s intervals"
      
      while [ $ELAPSED_SECONDS -lt $TOTAL_TIMEOUT_SECONDS ]; do
        echo "Attempt at ${ELAPSED_SECONDS}s / ${TOTAL_TIMEOUT_SECONDS}s..."
        
        # Try kubectl wait with short timeout
        if kubectl wait --timeout=${WAIT_INTERVAL}s -n $WAIT_NAMESPACE $WAIT_RESOURCE_TYPE/$WAIT_RESOURCE_NAME --for=condition=$WAIT_CONDITION 2>/dev/null; then
          echo "kubectl wait completed successfully!"
          exit 0
        fi
        
        # Check for common transient errors
        WAIT_OUTPUT=$(kubectl wait --timeout=${WAIT_INTERVAL}s -n $WAIT_NAMESPACE $WAIT_RESOURCE_TYPE/$WAIT_RESOURCE_NAME --for=condition=$WAIT_CONDITION 2>&1 || true)
        
        if echo "$WAIT_OUTPUT" | grep -q "no matching resources found"; then
          echo "Resource not found yet. Waiting 5s for resource to be created..."
          sleep 5
          ELAPSED_SECONDS=$((ELAPSED_SECONDS + 5))
        elif echo "$WAIT_OUTPUT" | grep -q "timed out waiting for the condition"; then
          echo "Wait timed out after ${WAIT_INTERVAL}s, retrying..."
          ELAPSED_SECONDS=$((ELAPSED_SECONDS + WAIT_INTERVAL))
        else
          echo "kubectl wait completed successfully!"
          exit 0
        fi
        
        # Small delay before next attempt to avoid hammering the API
        if [ $ELAPSED_SECONDS -lt $TOTAL_TIMEOUT_SECONDS ]; then
          sleep 2
          ELAPSED_SECONDS=$((ELAPSED_SECONDS + 2))
        fi
      done
      
      echo "ERROR: Timeout reached after ${TOTAL_TIMEOUT_SECONDS}s waiting for $WAIT_RESOURCE_TYPE/$WAIT_RESOURCE_NAME"
      exit 1
    '''
  }
}

@description('The name of the kubectl wait operation')
output operationName string = waitCommand.name

@description('The resource ID of the deployment script')
output scriptResourceId string = kubectlWaitScript.id
