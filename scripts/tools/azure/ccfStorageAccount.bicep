@description('Location of the storage account')
param location string = resourceGroup().location

resource storageAccount 'Microsoft.Storage/storageAccounts@2022-09-01' = {
  name: deployment().name
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    allowSharedKeyAccess: true // TODO: Update to not require this #533
    // networkAcls: { // TODO: Rework to accomodate this
    //   defaultAction: 'Deny'
    // }
  }
}

output storageAccountName string = storageAccount.name
output storageAccountID string = storageAccount.id
