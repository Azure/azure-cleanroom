extension radius

@description('Radius-provided object containing information about the resource calling the Recipe')
param context object

@description('Chart version')
param chartVersion string = '1.5.3'

@description('Namespace for Envoy Gateway')
param namespace string = 'envoy-gateway-system'

@description('AKS cluster name')
param aksClusterName string

@description('Resource group containing the AKS cluster')
param aksResourceGroup string = resourceGroup().name

@description('How the deployment script should be forced to execute')
param forceUpdateTag string = utcNow()

@description('A delay before the script import operation starts')
param initialScriptDelay string = '30s'

@description('ARM resource ID of the managed identity to use for deployment scripts (optional)')
param managedIdentityId string = ''

// Use the helm-chart recipe
resource envoyGatewayChart 'Applications.Core/extenders@2023-10-01-preview' = {
  name: 'envoy-gateway-helm-deployment'
  properties: {
    application: context.application.id
    environment: context.environment.id
    recipe: {
      name: 'helm-chart-recipe'
      parameters: {
        chartName: 'gateway-helm'
        releaseName: 'envoy-gateway'
        repoUrl: 'oci://docker.io/envoyproxy'
        chartVersion: chartVersion
        namespace: namespace
        aksClusterName: aksClusterName
        aksResourceGroup: aksResourceGroup
        forceUpdateTag: forceUpdateTag
        initialScriptDelay: initialScriptDelay
        helmArgs: '--create-namespace'
        managedIdentityId: managedIdentityId
      }
    }
  }
}

// Wait for Envoy Gateway to be ready
resource envoyGatewayWait 'Applications.Core/extenders@2023-10-01-preview' = {
  name: 'envoy-gateway-wait'
  properties: {
    application: context.application.id
    environment: context.environment.id
    recipe: {
      name: 'kubectl-wait-recipe'
      parameters: {
        aksClusterName: aksClusterName
        aksResourceGroup: aksResourceGroup
        waitCommand: {
          name: 'envoy-gateway-ready'
          namespace: namespace
          resourceType: 'deployment'
          resourceName: 'envoy-gateway'
          condition: 'Available'
          timeoutMinutes: 5
        }
        forceUpdateTag: forceUpdateTag
        managedIdentityId: managedIdentityId
      }
    }
  }
  dependsOn: [
    envoyGatewayChart
  ]
}

@description('The result of the Recipe. Must match the target resource\'s schema.')
output result object = {
  values: {
    namespace: namespace
    chartVersion: chartVersion
    resourceName: context.resource.name
    aksClusterName: aksClusterName
    componentName: 'envoy-gateway'
    status: 'deployed'
    waitOperationCompleted: true
  }
  secrets: {}
  resources: [
    envoyGatewayChart.id
    envoyGatewayWait.id
  ]
}
