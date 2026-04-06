extension radius

@description('Radius-provided object containing information about the resource calling the Recipe')
param context object

@description('Chart version')
param chartVersion string = 'v0.15.0'

@description('Namespace for KServe')
param namespace string = 'kserve'

@description('AKS cluster name')
param aksClusterName string

@description('Resource group containing the AKS cluster')
param aksResourceGroup string = resourceGroup().name

@description('How the deployment script should be forced to execute')
param forceUpdateTag string = utcNow()

@description('A delay before the script import operation starts')
param initialScriptDelay string = '30s'

@description('ARM resource ID of the managed identity to use for deployment scripts')
param managedIdentityId string

// Deploy KServe CRDs first using helm-chart recipe
resource kserveCrdChart 'Applications.Core/extenders@2023-10-01-preview' = {
  name: 'kserve-crd-helm-deployment'
  properties: {
    application: context.application.id
    environment: context.environment.id
    recipe: {
      name: 'helm-chart-recipe'
      parameters: {
        chartName: 'kserve-crd'
        releaseName: 'kserve-crd'
        repoUrl: 'oci://ghcr.io/kserve/charts'
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

// Deploy KServe main chart after CRDs using helm-chart recipe
resource kserveChart 'Applications.Core/extenders@2023-10-01-preview' = {
  name: 'kserve-helm-deployment'
  properties: {
    application: context.application.id
    environment: context.environment.id
    recipe: {
      name: 'helm-chart-recipe'
      parameters: {
        chartName: 'kserve'
        releaseName: 'kserve'
        repoUrl: 'oci://ghcr.io/kserve/charts'
        chartVersion: chartVersion
        namespace: namespace
        aksClusterName: aksClusterName
        aksResourceGroup: aksResourceGroup
        forceUpdateTag: forceUpdateTag
        initialScriptDelay: initialScriptDelay
        helmArgs: '--create-namespace --set kserve.controller.deploymentMode=RawDeployment --set kserve.controller.gateway.ingressGateway.enableGatewayApi=true --set kserve.controller.gateway.ingressGateway.kserveGateway=kserve/kserve-ingress-gateway --set kserve.controller.gateway.ingressGateway.createGateway=true'
        managedIdentityId: managedIdentityId
      }
    }
  }
  dependsOn: [
    kserveCrdChart
  ]
}

// Wait for KServe controller to be ready
resource kserveControllerWait 'Applications.Core/extenders@2023-10-01-preview' = {
  name: 'kserve-controller-wait'
  properties: {
    application: context.application.id
    environment: context.environment.id
    recipe: {
      name: 'kubectl-wait-recipe'
      parameters: {
        aksClusterName: aksClusterName
        aksResourceGroup: aksResourceGroup
        waitCommand: {
          name: 'kserve-controller-ready'
          namespace: namespace
          resourceType: 'deployment'
          resourceName: 'kserve-controller-manager'
          condition: 'Available'
          timeoutMinutes: 5
        }
        forceUpdateTag: forceUpdateTag
        managedIdentityId: managedIdentityId
      }
    }
  }
  dependsOn: [
    kserveChart
  ]
}

// Wait for KServe localmodel controller to be ready
resource kserveLocalModelWait 'Applications.Core/extenders@2023-10-01-preview' = {
  name: 'kserve-localmodel-controller-wait'
  properties: {
    application: context.application.id
    environment: context.environment.id
    recipe: {
      name: 'kubectl-wait-recipe'
      parameters: {
        aksClusterName: aksClusterName
        aksResourceGroup: aksResourceGroup
        waitCommand: {
          name: 'kserve-localmodel-controller-ready'
          namespace: namespace
          resourceType: 'deployment'
          resourceName: 'kserve-localmodel-controller-manager'
          condition: 'Available'
          timeoutMinutes: 5
        }
        forceUpdateTag: forceUpdateTag
        managedIdentityId: managedIdentityId
      }
    }
  }
  dependsOn: [
    kserveChart
  ]
}

@description('The result of the Recipe. Must match the target resource\'s schema.')
output result object = {
  values: {
    namespace: namespace
    chartVersion: chartVersion
    resourceName: context.resource.name
    aksClusterName: aksClusterName
    componentName: 'kserve'
    status: 'deployed'
    waitOperationsCompleted: true
  }
  secrets: {}
  resources: [
    kserveCrdChart.id
    kserveChart.id
    kserveControllerWait.id
    kserveLocalModelWait.id
  ]
}
