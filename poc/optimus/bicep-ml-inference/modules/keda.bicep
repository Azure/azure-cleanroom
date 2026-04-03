@description('The name of the AKS cluster')
param aksClusterName string

@description('The resource group containing the AKS cluster')
param aksResourceGroup string = resourceGroup().name

@description('The location to deploy the resources to')
param location string = resourceGroup().location

@description('KEDA chart version')
param chartVersion string

@description('Namespace for KEDA')
param namespace string = 'keda'

@description('Tags to apply to the resources')
param tags object = {}

@description('How the deployment script should be forced to execute')
param forceUpdateTag string = utcNow()

@description('The resource ID of the managed identity to use for the deployment script')
param deploymentScriptMIId string

// Deploy KEDA using the generic Helm module
module kedaChart 'helm-chart.bicep' = {
  name: 'keda-helm-deployment'
  params: {
    aksClusterName: aksClusterName
    aksResourceGroup: aksResourceGroup
    location: location
    chartName: 'keda'
    repoUrl: 'https://kedacore.github.io/charts'
    chartVersion: chartVersion
    namespace: namespace
    helmValues: ''
    helmArgs: '--create-namespace'
    forceUpdateTag: forceUpdateTag
    tags: tags
    deploymentScriptMIId: deploymentScriptMIId
  }
}

@description('The namespace where KEDA was deployed')
output namespace string = kedaChart.outputs.namespace

@description('The deployment script resource ID')
output deploymentScriptId string = kedaChart.outputs.deploymentScriptId
