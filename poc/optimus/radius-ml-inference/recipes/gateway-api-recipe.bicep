@description('Radius-provided object containing information about the resource calling the Recipe')
param context object

@description('Gateway API version to install')
param gatewayApiVersion string = 'v1.2.1'

@description('Gateway class name to create')
param gatewayClassName string = 'envoy'

@description('Controller name for the gateway class')
param controllerName string = 'gateway.envoyproxy.io/gatewayclass-controller'

@description('The location to deploy the resources to')
param location string = resourceGroup().location

@description('AKS cluster name')
param aksClusterName string

@description('Resource group containing the AKS cluster')
param aksResourceGroup string = resourceGroup().name

@description('How the deployment script should be forced to execute')
param forceUpdateTag string = utcNow()

// Create managed identity for deployment script
resource managedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-gateway-api-${uniqueString(context.resource.id)}'
  location: location
}

// Create deployment script to install Gateway API CRDs and GatewayClass
resource gatewayApiScript 'Microsoft.Resources/deploymentScripts@2023-08-01' = {
  name: 'gateway-api-install-${uniqueString(context.resource.id)}'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${managedIdentity.id}': {}
    }
  }
  kind: 'AzureCLI'
  properties: {
    forceUpdateTag: forceUpdateTag
    azCliVersion: '2.50.0'
    timeout: 'PT30M'
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

@description('The result of the Recipe. Must match the target resource\'s schema.')
output result object = {
  values: {
    gatewayClassName: gatewayClassName
    gatewayApiVersion: gatewayApiVersion
    resourceName: context.resource.name
    aksClusterName: aksClusterName
    deploymentOutput: gatewayApiScript.properties.outputs
  }
  secrets: {}
  resources: [
    gatewayApiScript.id
    managedIdentity.id
  ]
}
