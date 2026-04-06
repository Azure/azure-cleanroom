@description('The name of the AKS cluster')
param aksClusterName string

@description('The resource group containing the AKS cluster')
param aksResourceGroup string = resourceGroup().name

@description('The location to deploy the resources to')
param location string = resourceGroup().location

@description('cert-manager chart version')
param chartVersion string

@description('Namespace for cert-manager')
param namespace string = 'cert-manager'

@description('Tags to apply to the resources')
param tags object = {}

@description('How the deployment script should be forced to execute')
param forceUpdateTag string = utcNow()

@description('The resource ID of the managed identity to use for the deployment script')
param deploymentScriptMIId string

// Deploy cert-manager using the generic Helm module
module certManagerChart 'helm-chart.bicep' = {
  name: 'cert-manager-helm-deployment'
  params: {
    aksClusterName: aksClusterName
    aksResourceGroup: aksResourceGroup
    location: location
    chartName: 'cert-manager'
    repoUrl: 'https://charts.jetstack.io'
    chartVersion: chartVersion
    namespace: namespace
    helmValues: ''
    helmArgs: '--create-namespace --set crds.enabled=true'
    forceUpdateTag: forceUpdateTag
    tags: tags
    deploymentScriptMIId: deploymentScriptMIId
  }
}

@description('The namespace where cert-manager was deployed')
output namespace string = certManagerChart.outputs.namespace

@description('The deployment script resource ID')
output deploymentScriptId string = certManagerChart.outputs.deploymentScriptId
