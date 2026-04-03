@description('The name of the AKS cluster')
param aksClusterName string

@description('The resource group containing the AKS cluster')
param aksResourceGroup string = resourceGroup().name

@description('The location to deploy the resources to')
param location string = resourceGroup().location

@description('Gateway API version to install')
param gatewayApiVersion string

@description('Gateway class name to create')
param gatewayClassName string

@description('Controller name for the gateway class')
param controllerName string

@description('Tags to apply to the resources')
param tags object = {}

@description('How the deployment script should be forced to execute')
param forceUpdateTag string = utcNow()

@description('The resource ID of the managed identity to use for the deployment script')
param deploymentScriptMIId string

// Create deployment script to install Gateway API and GatewayClass
resource gatewayApiScript 'Microsoft.Resources/deploymentScripts@2023-08-01' = {
  name: 'gateway-api-install'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${deploymentScriptMIId}': {}
    }
  }
  kind: 'AzureCLI'
  properties: {
    forceUpdateTag: forceUpdateTag
    azCliVersion: '2.50.0'
    timeout: 'PT15M'
    retentionInterval: 'P1D'
    cleanupPreference: 'OnSuccess'
    environmentVariables: [
      { name: 'RG', value: aksResourceGroup }
      { name: 'AKS_NAME', value: aksClusterName }
      { name: 'GATEWAY_API_VERSION', value: gatewayApiVersion }
      { name: 'GATEWAY_CLASS_NAME', value: gatewayClassName }
      { name: 'CONTROLLER_NAME', value: controllerName }
    ]
    scriptContent: '''
      #!/bin/bash
      set -e
      # set -x  # Enable command tracing
      
      echo "========================================="
      echo "Gateway API Installation"
      echo "========================================="
      echo "Resource Group: $RG"
      echo "AKS Cluster: $AKS_NAME"
      echo "Gateway API Version: $GATEWAY_API_VERSION"
      echo "Gateway Class Name: $GATEWAY_CLASS_NAME"
      echo "Controller Name: $CONTROLLER_NAME"
      echo "========================================="
      
      echo "Getting AKS credentials..."
      az aks get-credentials --resource-group "$RG" --name "$AKS_NAME" --overwrite-existing
      
      echo "Installing kubectl..."
      az aks install-cli
      
      echo "Verifying kubectl installation..."
      kubectl version --client
      
      echo "Installing Gateway API CRDs..."
      kubectl apply -f "https://github.com/kubernetes-sigs/gateway-api/releases/download/${GATEWAY_API_VERSION}/standard-install.yaml"
      
      echo "Waiting for Gateway API CRDs to be established..."
      kubectl wait --for condition=established --timeout=60s crd/gateways.gateway.networking.k8s.io
      kubectl wait --for condition=established --timeout=60s crd/httproutes.gateway.networking.k8s.io
      kubectl wait --for condition=established --timeout=60s crd/gatewayclasses.gateway.networking.k8s.io
      
      echo "Creating GatewayClass resource..."
      cat <<EOF | kubectl apply -f -
apiVersion: gateway.networking.k8s.io/v1
kind: GatewayClass
metadata:
  name: ${GATEWAY_CLASS_NAME}
spec:
  controllerName: ${CONTROLLER_NAME}
EOF
      
      echo "Verifying GatewayClass creation..."
      kubectl get gatewayclass
      
      echo "========================================="
      echo "Gateway API installation completed!"
      echo "========================================="
    '''
  }
}

@description('The gateway class name that was created')
output gatewayClassName string = gatewayClassName

@description('The Gateway API version that was installed')
output gatewayApiVersion string = gatewayApiVersion

@description('The deployment script resource ID')
output deploymentScriptId string = gatewayApiScript.id
