@description('Name of the container group')
param containerGroupName string = 'attestation-report-generator'

@description('Location for the container group')
param location string = resourceGroup().location

@description('Container image with confidential computing support')
param containerImage string = 'gsinhadev.azurecr.io/attestation-report-generator:latest'
param skrContainerImage string = 'mcr.microsoft.com/aci/skr:2.12'
#disable-next-line no-unused-params
param ccePolicy string = 'cGFja2FnZSBwb2xpY3kKCmFwaV9zdm4gOj0gIjAuMTAuMCIKCm1vdW50X2RldmljZSA6PSB7ImFsbG93ZWQiOiB0cnVlfQptb3VudF9vdmVybGF5IDo9IHsiYWxsb3dlZCI6IHRydWV9CmNyZWF0ZV9jb250YWluZXIgOj0geyJhbGxvd2VkIjogdHJ1ZSwgImVudl9saXN0IjogbnVsbCwgImFsbG93X3N0ZGlvX2FjY2VzcyI6IHRydWV9CnVubW91bnRfZGV2aWNlIDo9IHsiYWxsb3dlZCI6IHRydWV9IAp1bm1vdW50X292ZXJsYXkgOj0geyJhbGxvd2VkIjogdHJ1ZX0KZXhlY19pbl9jb250YWluZXIgOj0geyJhbGxvd2VkIjogdHJ1ZSwgImVudl9saXN0IjogbnVsbH0KZXhlY19leHRlcm5hbCA6PSB7ImFsbG93ZWQiOiB0cnVlLCAiZW52X2xpc3QiOiBudWxsLCAiYWxsb3dfc3RkaW9fYWNjZXNzIjogdHJ1ZX0Kc2h1dGRvd25fY29udGFpbmVyIDo9IHsiYWxsb3dlZCI6IHRydWV9CnNpZ25hbF9jb250YWluZXJfcHJvY2VzcyA6PSB7ImFsbG93ZWQiOiB0cnVlfQpwbGFuOV9tb3VudCA6PSB7ImFsbG93ZWQiOiB0cnVlfQpwbGFuOV91bm1vdW50IDo9IHsiYWxsb3dlZCI6IHRydWV9CmdldF9wcm9wZXJ0aWVzIDo9IHsiYWxsb3dlZCI6IHRydWV9CmR1bXBfc3RhY2tzIDo9IHsiYWxsb3dlZCI6IHRydWV9CnJ1bnRpbWVfbG9nZ2luZyA6PSB7ImFsbG93ZWQiOiB0cnVlfQpsb2FkX2ZyYWdtZW50IDo9IHsiYWxsb3dlZCI6IHRydWV9CnNjcmF0Y2hfbW91bnQgOj0geyJhbGxvd2VkIjogdHJ1ZX0Kc2NyYXRjaF91bm1vdW50IDo9IHsiYWxsb3dlZCI6IHRydWV9Cg=='

@description('CPU cores for the container')
param cpuCores int = 1

@description('Memory in GB')
param memoryGb int = 1

resource containerGroup 'Microsoft.ContainerInstance/containerGroups@2024-10-01-preview' = {
  name: containerGroupName
  location: location
  properties: {
    osType: 'Linux'
    sku: 'Confidential' // This enables confidential computing mode
    confidentialComputeProperties: {
      ccePolicy: ccePolicy
    }
    containers: [
      {
        name: 'attestation-report-generator'
        properties: {
          image: containerImage
          ports: [
            {
              port: 9300
            }
          ]
          resources: {
            requests: {
              cpu: cpuCores
              memoryInGB: memoryGb
            }
          }
          volumeMounts: [
            {
              name: 'shared'
              mountPath: '/shared'
            }
          ]
        }
      }
      {
        name: 'skr'
        properties: {
          image: skrContainerImage
          resources: {
            requests: {
              cpu: cpuCores
              memoryInGB: memoryGb
            }
          }
          command: [
            '/skr.sh'
          ]
          volumeMounts: [
            {
              name: 'shared'
              mountPath: '/shared'
            }
          ]
          environmentVariables: [
            {
              name: 'SkrSideCarArgs'
              value: 'ewogICAiY2VydGNhY2hlIjogewogICAgICAiZW5kcG9pbnQiOiAiYW1lcmljYXMuYWNjY2FjaGUuYXp1cmUubmV0IiwKICAgICAgInRlZV90eXBlIjogIlNldlNucFZNIiwKICAgICAgImFwaV92ZXJzaW9uIjogImFwaS12ZXJzaW9uPTIwMjAtMTAtMTUtcHJldmlldyIKICAgfQp9'
            }
            {
              name: 'Port'
              value: '8284'
            }
            {
              name: 'LogFile'
              value: '/shared/skr.log'
            }
            {
              name: 'LogLevel'
              value: 'Debug'
            }
          ]
        }
      }
    ]
    ipAddress: {
      ports: [
        {
          port: 9300
          protocol: 'TCP'
        }
      ]
      type: 'Public'
    }
    volumes: [
      {
        name: 'shared'
        emptyDir: {}
      }
    ]
  }
}
