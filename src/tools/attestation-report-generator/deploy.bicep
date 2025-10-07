@description('Name of the container group')
param containerGroupName string = 'attestation-report-generator'

@description('Location for the container group')
param location string = resourceGroup().location

@description('Container image with confidential computing support')
param containerImage string = 'gsinhadev.azurecr.io/attestation-report-generator:latest'
param attestationContainerImage string = 'gsinhadev.azurecr.io/ccr-attestation:latest'

@description('CPU cores for the container')
param cpuCores int = 1

@description('Memory in GB')
param memoryGb int = 2

resource containerGroup 'Microsoft.ContainerInstance/containerGroups@2024-10-01-preview' = {
  name: containerGroupName
  location: location
  properties: {
    osType: 'Linux'
    sku: 'Confidential' // This enables confidential computing mode
    confidentialComputeProperties: {
      ccePolicy: 'cGFja2FnZSBwb2xpY3kKCmFwaV9zdm4gOj0gIjAuMTAuMCIKCm1vdW50X2RldmljZSA6PSB7ImFsbG93ZWQiOiB0cnVlfQptb3VudF9vdmVybGF5IDo9IHsiYWxsb3dlZCI6IHRydWV9CmNyZWF0ZV9jb250YWluZXIgOj0geyJhbGxvd2VkIjogdHJ1ZSwgImVudl9saXN0IjogbnVsbCwgImFsbG93X3N0ZGlvX2FjY2VzcyI6IHRydWV9CnVubW91bnRfZGV2aWNlIDo9IHsiYWxsb3dlZCI6IHRydWV9IAp1bm1vdW50X292ZXJsYXkgOj0geyJhbGxvd2VkIjogdHJ1ZX0KZXhlY19pbl9jb250YWluZXIgOj0geyJhbGxvd2VkIjogdHJ1ZSwgImVudl9saXN0IjogbnVsbH0KZXhlY19leHRlcm5hbCA6PSB7ImFsbG93ZWQiOiB0cnVlLCAiZW52X2xpc3QiOiBudWxsLCAiYWxsb3dfc3RkaW9fYWNjZXNzIjogdHJ1ZX0Kc2h1dGRvd25fY29udGFpbmVyIDo9IHsiYWxsb3dlZCI6IHRydWV9CnNpZ25hbF9jb250YWluZXJfcHJvY2VzcyA6PSB7ImFsbG93ZWQiOiB0cnVlfQpwbGFuOV9tb3VudCA6PSB7ImFsbG93ZWQiOiB0cnVlfQpwbGFuOV91bm1vdW50IDo9IHsiYWxsb3dlZCI6IHRydWV9CmdldF9wcm9wZXJ0aWVzIDo9IHsiYWxsb3dlZCI6IHRydWV9CmR1bXBfc3RhY2tzIDo9IHsiYWxsb3dlZCI6IHRydWV9CnJ1bnRpbWVfbG9nZ2luZyA6PSB7ImFsbG93ZWQiOiB0cnVlfQpsb2FkX2ZyYWdtZW50IDo9IHsiYWxsb3dlZCI6IHRydWV9CnNjcmF0Y2hfbW91bnQgOj0geyJhbGxvd2VkIjogdHJ1ZX0Kc2NyYXRjaF91bm1vdW50IDo9IHsiYWxsb3dlZCI6IHRydWV9CgoK'
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
              name: 'uds'
              mountPath: '/mnt/uds'
            }
          ]
        }
      }
      {
        name: 'ccr-attestation'
        properties: {
          image: attestationContainerImage
          resources: {
            requests: {
              cpu: cpuCores
              memoryInGB: memoryGb
            }
          }
          command: [
            'app'
            '-socket-address'
            '/mnt/uds/sock' // Keep the container running
          ]
          volumeMounts: [
            {
              name: 'uds'
              mountPath: '/mnt/uds'
            }
          ]
        }
      }
    ]
    ipAddress:{
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
        name: 'uds'
        emptyDir: {} // Use an empty directory for the Unix domain socket
      }
    ]
  }
}
