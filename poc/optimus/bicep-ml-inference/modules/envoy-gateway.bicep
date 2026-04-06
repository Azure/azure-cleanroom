@description('The name of the AKS cluster')
param aksClusterName string

@description('The resource group containing the AKS cluster')
param aksResourceGroup string = resourceGroup().name

@description('The location to deploy the resources to')
param location string = resourceGroup().location

@description('Envoy Gateway chart version')
param chartVersion string

@description('Namespace for Envoy Gateway')
param namespace string = 'envoy-gateway-system'

@description('Tags to apply to the resources')
param tags object = {}

@description('The managed identity ID for kubectl operations')
param deploymentScriptMIId string

@description('How the deployment script should be forced to execute')
param forceUpdateTag string = utcNow()

// Deploy Envoy Gateway using the generic Helm module
module envoyGatewayChart 'helm-chart.bicep' = {
  name: 'envoy-gateway-helm-deployment'
  params: {
    aksClusterName: aksClusterName
    aksResourceGroup: aksResourceGroup
    location: location
    chartName: 'gateway-helm'
    repoUrl: 'oci://docker.io/envoyproxy'
    chartVersion: chartVersion
    namespace: namespace
    helmValues: ''
    helmArgs: '--create-namespace'
    forceUpdateTag: forceUpdateTag
    tags: tags
    deploymentScriptMIId: deploymentScriptMIId
  }
}

// Wait for Envoy Gateway to be ready
module envoyGatewayWait 'kubectl-wait.bicep' = {
  name: 'envoy-gateway-wait'
  params: {
    aksClusterName: aksClusterName
    location: location
    waitCommand: {
      name: 'envoy-gateway-ready'
      namespace: namespace
      resourceType: 'deployment'
      resourceName: 'envoy-gateway'
      condition: 'Available'
      timeoutMinutes: 5
      managedIdentityId: deploymentScriptMIId
    }
    forceUpdateTag: forceUpdateTag
    tags: tags
  }
  dependsOn: [
    envoyGatewayChart
  ]
}

@description('The namespace where Envoy Gateway was deployed')
output namespace string = envoyGatewayChart.outputs.namespace

@description('The deployment script resource ID')
output deploymentScriptId string = envoyGatewayChart.outputs.deploymentScriptId

@description('The kubectl wait operation name')
output waitOperationName string = envoyGatewayWait.outputs.operationName

@description('The kubectl wait script resource ID')
output waitScriptId string = envoyGatewayWait.outputs.scriptResourceId
