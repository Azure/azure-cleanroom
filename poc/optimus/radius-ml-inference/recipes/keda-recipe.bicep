extension radius

@description('Radius-provided object containing information about the resource calling the Recipe')
param context object

@description('Chart version')
param chartVersion string = '2.18.0'

@description('Namespace for KEDA')
param namespace string = 'keda'

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
resource helmChart 'Applications.Core/extenders@2023-10-01-preview' = {
  name: 'keda-helm-deployment'
  properties: {
    application: context.application.id
    environment: context.environment.id
    recipe: {
      name: 'helm-chart-recipe'
      parameters: {
        chartName: 'keda'
        releaseName: 'keda'
        repoUrl: 'https://kedacore.github.io/charts'
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

@description('The result of the Recipe. Must match the target resource\'s schema.')
output result object = {
  values: {
    namespace: namespace
    chartVersion: chartVersion
    resourceName: context.resource.name
    aksClusterName: aksClusterName
    componentName: 'keda'
    status: 'deployed'
  }
  secrets: {}
  resources: [
    helmChart.id
  ]
}
