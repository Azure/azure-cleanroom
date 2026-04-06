@description('The name of the ML inference platform deployment')
param platformName string = 'ml-inference-platform'

@description('The location to deploy the resources to')
param location string = resourceGroup().location

@description('The name of the AKS cluster')
param aksClusterName string = 'aks-ml-cluster'

@description('The node count for AKS cluster')
param nodeCount int = 3

@description('The VM size for AKS nodes')
param vmSize string = 'Standard_D4ds_v5'

@description('Chart versions for Helm deployments')
param chartVersions object

@description('Tags to apply to all resources')
param tags object = {
  project: 'ml-inference-platform'
  environment: 'production'
  deployedBy: 'bicep'
}

@description('Enable Prometheus monitoring stack (Prometheus + Grafana)')
param enablePrometheus bool = false

@description('How the deployment script should be forced to execute')
param forceUpdateTag string = '1' // utcNow()

// Create shared managed identity for deployment scripts
module deploymentScriptIdentity 'modules/managed-identity.bicep' = {
  name: 'deployment-script-identity-deployment'
  params: {
    identityName: 'id-deployment-script'
    location: location
    tags: tags
  }
}

// Deploy AKS Cluster
module aksCluster 'modules/aks-cluster.bicep' = {
  name: 'aks-cluster-deployment'
  params: {
    clusterName: aksClusterName
    location: location
    nodeCount: nodeCount
    vmSize: vmSize
    tags: tags
  }
}

// Deploy cert-manager (prerequisite for TLS)
module certManager 'modules/cert-manager.bicep' = {
  name: 'cert-manager-deployment'
  params: {
    aksClusterName: aksClusterName
    location: location
    chartVersion: chartVersions.certManager
    forceUpdateTag: forceUpdateTag
    tags: tags
    deploymentScriptMIId: deploymentScriptIdentity.outputs.identityId
  }
  dependsOn: [
    aksCluster
  ]
}

// Deploy KEDA for autoscaling
module keda 'modules/keda.bicep' = {
  name: 'keda-deployment'
  params: {
    aksClusterName: aksClusterName
    location: location
    chartVersion: chartVersions.keda
    forceUpdateTag: forceUpdateTag
    tags: tags
    deploymentScriptMIId: deploymentScriptIdentity.outputs.identityId
  }
  dependsOn: [
    aksCluster
  ]
}

// Deploy Envoy Gateway for Gateway API
module envoyGateway 'modules/envoy-gateway.bicep' = {
  name: 'envoy-gateway-deployment'
  params: {
    aksClusterName: aksClusterName
    location: location
    chartVersion: chartVersions.envoyGateway
    forceUpdateTag: forceUpdateTag
    tags: tags
    deploymentScriptMIId: deploymentScriptIdentity.outputs.identityId
  }
  dependsOn: [
    certManager
  ]
}

// Deploy Gateway API CRDs and GatewayClass
module gatewayApi 'modules/gateway-api.bicep' = {
  name: 'gateway-api-deployment'
  params: {
    aksClusterName: aksClusterName
    location: location
    gatewayApiVersion: 'v1.2.1'
    gatewayClassName: 'envoy'
    controllerName: 'gateway.envoyproxy.io/gatewayclass-controller'
    forceUpdateTag: forceUpdateTag
    tags: tags
    deploymentScriptMIId: deploymentScriptIdentity.outputs.identityId
  }
  dependsOn: [
    envoyGateway
  ]
}

// Deploy KServe for ML model serving
module kserve 'modules/kserve.bicep' = {
  name: 'kserve-deployment'
  params: {
    aksClusterName: aksClusterName
    location: location
    chartVersion: chartVersions.kserve
    forceUpdateTag: forceUpdateTag
    tags: tags
    deploymentScriptMIId: deploymentScriptIdentity.outputs.identityId
  }
  dependsOn: [
    gatewayApi
    keda
  ]
}

// Deploy Prometheus for observability
module prometheus 'modules/prometheus.bicep' = if (enablePrometheus) {
  name: 'prometheus-deployment'
  params: {
    aksClusterName: aksClusterName
    location: location
    chartVersion: chartVersions.prometheus
    forceUpdateTag: forceUpdateTag
    tags: tags
    deploymentScriptMIId: deploymentScriptIdentity.outputs.identityId
  }
  dependsOn: [
    aksCluster
  ]
}

// Outputs
@description('The name of the deployed AKS cluster')
output aksClusterName string = aksCluster.outputs.clusterName

@description('The resource ID of the AKS cluster')
output aksClusterId string = aksCluster.outputs.clusterId

@description('The FQDN of the AKS cluster')
output aksClusterFqdn string = aksCluster.outputs.clusterFqdn

@description('Summary of deployed components')
output deploymentSummary object = {
  platform: platformName
  aksCluster: aksCluster.outputs.clusterName
  components: union(
    {
      certManager: {
        namespace: certManager.outputs.namespace
      }
      keda: {
        namespace: keda.outputs.namespace
      }
      envoyGateway: {
        namespace: envoyGateway.outputs.namespace
      }
      gatewayApi: {
        gatewayClassName: gatewayApi.outputs.gatewayClassName
        gatewayApiVersion: gatewayApi.outputs.gatewayApiVersion
      }
      kserve: {
        namespace: kserve.outputs.namespace
      }
    },
    enablePrometheus
      ? {
          prometheus: {
            namespace: prometheus!.outputs.namespace
          }
        }
      : {}
  )
}
