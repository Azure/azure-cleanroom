@description('Radius-provided object containing information about the resource calling the Recipe')
param context object

@description('The location to deploy the resources to')
param location string = resourceGroup().location

@description('The name suffix for the managed identity')
param identityName string

@description('Tags to apply to the resources')
param tags object = {}

// Create managed identity
resource managedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}

// Role assignment to allow the managed identity to manage resources in the resource group
resource contributorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(managedIdentity.id, resourceGroup().id, 'b24988ac-6180-42a0-ab88-20f7382dd24c')
  scope: resourceGroup()
  properties: {
    principalId: managedIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'b24988ac-6180-42a0-ab88-20f7382dd24c'
    ) // Contributor
    principalType: 'ServicePrincipal'
  }
}

@description('The result of the Recipe. Must match the target resource\'s schema.')
output result object = {
  values: {
    identityId: managedIdentity.id
    principalId: managedIdentity.properties.principalId
    clientId: managedIdentity.properties.clientId
    identityName: identityName
    resourceName: context.resource.name
  }
  secrets: {}
  resources: [
    managedIdentity.id
    contributorRole.id
  ]
}
