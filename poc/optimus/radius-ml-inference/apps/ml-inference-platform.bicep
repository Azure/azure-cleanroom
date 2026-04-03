extension radius

@description('The Radius Application ID. Injected automatically by the rad CLI.')
param application string

@description('The ID of your Radius environment. Set automatically by the rad CLI.')
param environment string

@description('The node count for AKS cluster')
param nodeCount int = 3

@description('The VM size for AKS nodes')
param vmSize string = 'Standard_D4ds_v5'

@description('AKS cluster name for recipe deployment scripts')
param aksClusterName string = 'aks-ml-cluster'

@description('Resource group containing the AKS cluster')
param aksResourceGroup string = resourceGroup().name

@description('ARM resource ID of the managed identity for deployment scripts')
param managedIdentityId string

@description('Force update tag for deployment scripts')
param forceUpdateTag string = '1'

// AKS Cluster (only for aks-prod environment)
resource aksCluster 'Applications.Core/extenders@2023-10-01-preview' = {
  name: 'aks-ml-cluster'
  properties: {
    application: application
    environment: environment
    recipe: {
      name: 'aks-cluster-recipe'
      parameters: {
        clusterName: aksClusterName
        nodeCount: nodeCount
        vmSize: vmSize
      }
    }
  }
}

// KEDA for autoscaling
resource keda 'Applications.Core/extenders@2023-10-01-preview' = {
  name: 'keda'
  properties: {
    application: application
    environment: environment
    recipe: {
      name: 'keda-recipe'
      parameters: {
        aksClusterName: aksClusterName
        aksResourceGroup: aksResourceGroup
        managedIdentityId: managedIdentityId
        forceUpdateTag: forceUpdateTag
      }
    }
  }
  dependsOn: [aksCluster]
}

// cert-manager for TLS certificates
resource certManager 'Applications.Core/extenders@2023-10-01-preview' = {
  name: 'cert-manager'
  properties: {
    application: application
    environment: environment
    recipe: {
      name: 'cert-manager-recipe'
      parameters: {
        aksClusterName: aksClusterName
        aksResourceGroup: aksResourceGroup
        managedIdentityId: managedIdentityId
        forceUpdateTag: forceUpdateTag
      }
    }
  }
  dependsOn: [aksCluster]
}

// Envoy Gateway for Gateway API
resource envoyGateway 'Applications.Core/extenders@2023-10-01-preview' = {
  name: 'envoy-gateway'
  properties: {
    application: application
    environment: environment
    recipe: {
      name: 'envoy-gateway-recipe'
      parameters: {
        aksClusterName: aksClusterName
        aksResourceGroup: aksResourceGroup
        managedIdentityId: managedIdentityId
        forceUpdateTag: forceUpdateTag
      }
    }
  }
  dependsOn: [certManager]
}

// Gateway API CRDs and GatewayClass
resource gatewayApi 'Applications.Core/extenders@2023-10-01-preview' = {
  name: 'gateway-api'
  properties: {
    application: application
    environment: environment
    recipe: {
      name: 'gateway-api-recipe'
      parameters: {
        aksClusterName: aksClusterName
        aksResourceGroup: aksResourceGroup
        forceUpdateTag: forceUpdateTag
      }
    }
  }
  dependsOn: [envoyGateway]
}

// KServe for ML model serving
resource kserve 'Applications.Core/extenders@2023-10-01-preview' = {
  name: 'kserve'
  properties: {
    application: application
    environment: environment
    recipe: {
      name: 'kserve-recipe'
      parameters: {
        aksClusterName: aksClusterName
        aksResourceGroup: aksResourceGroup
        managedIdentityId: managedIdentityId
        forceUpdateTag: forceUpdateTag
      }
    }
  }
  dependsOn: [gatewayApi, keda, certManager]
}

// Output the application ID
output applicationId string = application
