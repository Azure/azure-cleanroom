@description('The name of the AKS cluster')
param aksClusterName string

@description('The resource group containing the AKS cluster')
param aksResourceGroup string = resourceGroup().name

@description('The location to deploy the resources to')
param location string = resourceGroup().location

@description('KServe chart version')
param chartVersion string

@description('Namespace for KServe')
param namespace string = 'kserve'

@description('Tags to apply to the resources')
param tags object = {}

@description('How the deployment script should be forced to execute')
param forceUpdateTag string = utcNow()

@description('The resource ID of the managed identity to use for the deployment script')
param deploymentScriptMIId string

// Deploy KServe CRDs first
module kserveCrdChart 'helm-chart.bicep' = {
  name: 'kserve-crd-helm-deployment'
  params: {
    aksClusterName: aksClusterName
    aksResourceGroup: aksResourceGroup
    location: location
    chartName: 'kserve-crd'
    repoUrl: 'oci://ghcr.io/kserve/charts'
    chartVersion: chartVersion
    namespace: namespace
    helmValues: ''
    helmArgs: '--create-namespace'
    forceUpdateTag: forceUpdateTag
    tags: tags
    deploymentScriptMIId: deploymentScriptMIId
  }
}

// Deploy KServe main chart after CRDs
module kserveChart 'helm-chart.bicep' = {
  name: 'kserve-helm-deployment'
  params: {
    aksClusterName: aksClusterName
    aksResourceGroup: aksResourceGroup
    location: location
    chartName: 'kserve'
    repoUrl: 'oci://ghcr.io/kserve/charts'
    chartVersion: chartVersion
    namespace: namespace
    helmValues: ''
    helmArgs: '--create-namespace --set kserve.controller.deploymentMode=RawDeployment --set kserve.controller.gateway.ingressGateway.enableGatewayApi=true --set kserve.controller.gateway.ingressGateway.kserveGateway=kserve/kserve-ingress-gateway --set kserve.controller.gateway.ingressGateway.createGateway=true'
    forceUpdateTag: forceUpdateTag
    tags: tags
    deploymentScriptMIId: deploymentScriptMIId
  }
  dependsOn: [
    kserveCrdChart
  ]
}

// Wait for KServe controller to be ready
module kserveControllerWait 'kubectl-wait.bicep' = {
  name: 'kserve-controller-wait'
  params: {
    aksClusterName: aksClusterName
    location: location
    waitCommand: {
      name: 'kserve-controller-ready'
      namespace: namespace
      resourceType: 'deployment'
      resourceName: 'kserve-controller-manager'
      condition: 'Available'
      timeoutMinutes: 5
      managedIdentityId: deploymentScriptMIId
    }
    forceUpdateTag: forceUpdateTag
    tags: tags
  }
  dependsOn: [
    kserveChart
  ]
}

// Wait for KServe localmodel controller to be ready
module kserveLocalModelWait 'kubectl-wait.bicep' = {
  name: 'kserve-localmodel-controller-wait'
  params: {
    aksClusterName: aksClusterName
    location: location
    waitCommand: {
      name: 'kserve-localmodel-controller-ready'
      namespace: namespace
      resourceType: 'deployment'
      resourceName: 'kserve-localmodel-controller-manager'
      condition: 'Available'
      timeoutMinutes: 5
      managedIdentityId: deploymentScriptMIId
    }
    forceUpdateTag: forceUpdateTag
    tags: tags
  }
  dependsOn: [
    kserveChart
  ]
}

@description('The namespace where KServe was deployed')
output namespace string = kserveChart.outputs.namespace

@description('The KServe CRD deployment script resource ID')
output crdDeploymentScriptId string = kserveCrdChart.outputs.deploymentScriptId

@description('The KServe main deployment script resource ID')
output deploymentScriptId string = kserveChart.outputs.deploymentScriptId

@description('The kubectl wait operation name for main controller')
output waitOperationName string = kserveControllerWait.outputs.operationName

@description('The kubectl wait script resource ID for main controller')
output waitScriptId string = kserveControllerWait.outputs.scriptResourceId

@description('The kubectl wait operation name for localmodel controller')
output localmodelWaitOperationName string = kserveLocalModelWait.outputs.operationName

@description('The kubectl wait script resource ID for localmodel controller')
output localmodelWaitScriptId string = kserveLocalModelWait.outputs.scriptResourceId
