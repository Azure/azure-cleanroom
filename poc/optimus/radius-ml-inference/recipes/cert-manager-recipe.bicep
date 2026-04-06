extension radius

@description('Radius-provided object containing information about the resource calling the Recipe')
param context object

@description('Chart version')
param chartVersion string = 'v1.16.1'

@description('Namespace for cert-manager')
param namespace string = 'cert-manager'

@description('Install CRDs')
param installCRDs bool = true

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
resource certManagerChart 'Applications.Core/extenders@2023-10-01-preview' = {
  name: 'cert-manager-helm-deployment'
  properties: {
    application: context.application.id
    environment: context.environment.id
    recipe: {
      name: 'helm-chart-recipe'
      parameters: {
        chartName: 'cert-manager'
        releaseName: 'cert-manager'
        repoUrl: 'https://charts.jetstack.io'
        chartVersion: chartVersion
        namespace: namespace
        aksClusterName: aksClusterName
        aksResourceGroup: aksResourceGroup
        forceUpdateTag: forceUpdateTag
        initialScriptDelay: initialScriptDelay
        helmArgs: '--create-namespace --set crds.enabled=true'
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
    installCRDs: installCRDs
    resourceName: context.resource.name
    aksClusterName: aksClusterName
    componentName: 'cert-manager'
    status: 'deployed'
  }
  secrets: {}
  resources: [
    certManagerChart.id
  ]
}
