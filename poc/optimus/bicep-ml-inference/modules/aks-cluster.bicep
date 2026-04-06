@description('The name of the AKS cluster')
param clusterName string = 'aks-ml-cluster'

@description('The location to deploy the resources to')
param location string = resourceGroup().location

@description('The node count for AKS cluster')
param nodeCount int = 3

@description('The VM size for AKS nodes')
param vmSize string = 'Standard_D4ds_v5'

@description('The DNS prefix for the cluster')
param dnsPrefix string = 'mlcluster'

@description('Enable auto-upgrade for the cluster')
param autoUpgradeChannel string = 'stable'

@description('Tags to apply to the resources')
param tags object = {}

// Managed Identity for AKS
resource aksIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${clusterName}'
  location: location
  tags: tags
}

// AKS Cluster
resource aksCluster 'Microsoft.ContainerService/managedClusters@2023-11-01' = {
  name: clusterName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${aksIdentity.id}': {}
    }
  }
  sku: {
    name: 'Base'
    tier: 'Standard'
  }
  properties: {
    dnsPrefix: dnsPrefix
    autoUpgradeProfile: {
      upgradeChannel: autoUpgradeChannel
    }
    agentPoolProfiles: [
      {
        name: 'systempool'
        count: nodeCount
        vmSize: vmSize
        osType: 'Linux'
        mode: 'System'
        enableAutoScaling: true
        minCount: 1
        maxCount: 5
        nodeTaints: [
          'CriticalAddonsOnly=true:NoSchedule'
        ]
      }
      {
        name: 'workerpool'
        count: nodeCount
        vmSize: vmSize
        osType: 'Linux'
        mode: 'User'
        enableAutoScaling: true
        minCount: 1
        maxCount: 10
      }
    ]
    networkProfile: {
      networkPlugin: 'azure'
      networkPolicy: 'calico'
      loadBalancerSku: 'Standard'
      outboundType: 'loadBalancer'
    }
    addonProfiles: {
      azureKeyvaultSecretsProvider: {
        enabled: true
      }
      azurepolicy: {
        enabled: true
      }
    }
    oidcIssuerProfile: {
      enabled: true
    }
    securityProfile: {
      workloadIdentity: {
        enabled: true
      }
    }
  }
}

@description('The name of the AKS cluster')
output clusterName string = aksCluster.name

@description('The resource ID of the AKS cluster')
output clusterId string = aksCluster.id

@description('The FQDN of the AKS cluster')
output clusterFqdn string = aksCluster.properties.fqdn

@description('The API server address of the AKS cluster')
output apiServerAddress string = aksCluster.properties.fqdn

@description('The principal ID of the AKS managed identity')
output principalId string = aksIdentity.properties.principalId

@description('The resource ID of the AKS managed identity')
output identityId string = aksIdentity.id
