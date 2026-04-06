@description('The name of the AKS cluster')
param aksClusterName string

@description('The resource group containing the AKS cluster')
param aksResourceGroup string = resourceGroup().name

@description('The location to deploy the resources to')
param location string = resourceGroup().location

@description('Prometheus chart version')
param chartVersion string

@description('Namespace for Prometheus')
param namespace string = 'monitoring'

@description('Tags to apply to the resources')
param tags object = {}

@description('How the deployment script should be forced to execute')
param forceUpdateTag string = utcNow()

@description('The resource ID of the managed identity to use for the deployment script')
param deploymentScriptMIId string

// Deploy Prometheus using the generic Helm module
module prometheusChart 'helm-chart.bicep' = {
  name: 'prometheus-helm-deployment'
  params: {
    aksClusterName: aksClusterName
    aksResourceGroup: aksResourceGroup
    location: location
    chartName: 'kube-prometheus-stack'
    repoUrl: 'https://prometheus-community.github.io/helm-charts'
    chartVersion: chartVersion
    namespace: namespace
    helmValues: '''
grafana:
  enabled: true
  adminPassword: admin
  service:
    type: LoadBalancer
  persistence:
    enabled: true
    size: 10Gi
  resources:
    limits:
      cpu: 500m
      memory: 512Mi
    requests:
      cpu: 100m
      memory: 256Mi

prometheus:
  prometheusSpec:
    storageSpec:
      volumeClaimTemplate:
        spec:
          storageClassName: default
          accessModes: ["ReadWriteOnce"]
          resources:
            requests:
              storage: 50Gi
    resources:
      limits:
        cpu: 2000m
        memory: 8Gi
      requests:
        cpu: 200m
        memory: 400Mi

alertmanager:
  alertmanagerSpec:
    storage:
      volumeClaimTemplate:
        spec:
          storageClassName: default
          accessModes: ["ReadWriteOnce"]
          resources:
            requests:
              storage: 50Gi
    resources:
      limits:
        cpu: 100m
        memory: 128Mi
      requests:
        cpu: 10m
        memory: 32Mi

nodeExporter:
  enabled: true

kubeStateMetrics:
  enabled: true
'''
    helmArgs: '--create-namespace'
    forceUpdateTag: forceUpdateTag
    tags: tags
    deploymentScriptMIId: deploymentScriptMIId
  }
}

@description('The namespace where Prometheus was deployed')
output namespace string = prometheusChart.outputs.namespace

@description('The deployment script resource ID')
output deploymentScriptId string = prometheusChart.outputs.deploymentScriptId
