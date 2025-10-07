param(
  [Parameter(Mandatory = $true)]
  [string]$resourceGroup,
  [Parameter(Mandatory = $true)]
  [string]$governanceClient,
  [Parameter(Mandatory = $true)]
  [string]$subject,
  [Parameter(Mandatory = $true)]
  [string]$issuerUrl,
  [Parameter()]
  [ValidateSet("managed_identity", "service_principal")]
  [string]$identityType = "managed_identity",
  [string]$kvType,
  [string]$outDir = ""
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

if ($outDir -eq "") {
  $outDir = "$PSScriptRoot/demo-resources/$resourceGroup"
}
else {
  $outDir = "$outDir/$resourceGroup"
}
. $outDir/names.generated.ps1

$root = git rev-parse --show-toplevel
Import-Module $root/samples/common/infra-scripts/azure-helpers.psm1 -Force -DisableNameChecking

function Assign-Permission-KeyVault {
  param(
    [string]$role,
    [string]$objectId,
    [string]$principalType
  )

  Write-Host "Assigning permissions to key vault $KEYVAULT_NAME and resource group $resourceGroup"
  $keyVaultResult = (az keyvault show --name $KEYVAULT_NAME --resource-group $resourceGroup) | ConvertFrom-Json
  $roleAssignment = (az role assignment list `
      --assignee-object-id $objectId `
      --scope $keyVaultResult.id `
      --role $role `
      --fill-principal-name false `
      --fill-role-definition-name false) | ConvertFrom-Json

  if ($roleAssignment.Length -eq 1) {
    Write-Host "$role permission on the key vault already exists, skipping assignment"
  }
  else {
    Write-Host "Assigning $role on the Key Vault"
    az role assignment create `
      --role $role `
      --scope $keyVaultResult.id `
      --assignee-object-id $objectId `
      --assignee-principal-type $principalType
  }
}

function Assign-Permission-HSM {
  param(
    [string]$role,
    [string]$objectId,
    [string]$principalType
  )

  $roleAssignment = (az keyvault role assignment list `
      --assignee-object-id $objectId `
      --hsm-name $MHSM_NAME `
      --role $role) | ConvertFrom-Json

  if ($roleAssignment.Length -eq 1) {
    Write-Host "$role permission on the HSM already exists, skipping assignment"
  }
  else {
    Write-Host "Assigning $role on the HSM"
    az keyvault role assignment create `
      --role $role `
      --scope "/" `
      --assignee-object-id $objectId `
      --hsm-name $MHSM_NAME `
      --assignee-principal-type ServicePrincipal
  }
}

function Assign-Secrets-User-Permission-KeyVault {
  param(
    [string]$objectId,
    [string]$principalType
  )

  $keyVaultResult = (az keyvault show `
      --name $KEYVAULT_NAME `
      --resource-group $resourceGroup) | ConvertFrom-Json

  $role = "Key Vault Secrets User"
  $roleAssignment = (az role assignment list `
      --assignee-object-id $objectId `
      --scope $keyVaultResult.id `
      --role $role `
      --fill-principal-name false `
      --fill-role-definition-name false) | ConvertFrom-Json

  if ($roleAssignment.Length -eq 1) {
    Write-Host "$role permission on the key vault already exists, skipping assignment"
  }
  else {
    Write-Host "Assigning $role on the Key Vault"
    az role assignment create `
      --role $role `
      --scope $keyVaultResult.id `
      --assignee-object-id $objectId `
      --assignee-principal-type ServicePrincipal
  }
}

function Assign-Permission-StorageAccount {
  param(
    [string]$objectId,
    [string]$principalType
  )

  $storageAccount = (az storage account show `
      --name $STORAGE_ACCOUNT_NAME `
      --resource-group $resourceGroup) | ConvertFrom-Json

  $role = "Storage Blob Data Contributor"
  $roleAssignment = (az role assignment list `
      --assignee-object-id $objectId `
      --scope $storageAccount.id `
      --role $role `
      --fill-principal-name false `
      --fill-role-definition-name false) | ConvertFrom-Json

  if ($roleAssignment.Length -eq 1) {
    Write-Host "$role permission on the storage account already exists, skipping assignment"
  }
  else {
    Write-Host "Assigning $role on the storage account"
    az role assignment create `
      --role $role `
      --scope $storageAccount.id `
      --assignee-object-id $objectId `
      --assignee-principal-type $principalType
  }
}

$isMhsm = $($kvType -eq "mhsm")
if ($identityType -eq "managed_identity") {
  $appId = (az identity show --name $MANAGED_IDENTITY_NAME --resource-group $resourceGroup | ConvertFrom-Json).principalId
}
else {
  $app = az ad sp list --display-name $ENTERPRISE_APP_NAME | ConvertFrom-Json
  $appId = $app.id
}

# Cleanroom needs both read/write permissions on storage account, hence assigning Storage Blob Data Contributor.
Assign-Permission-StorageAccount `
  -objectId $appId `
  -principalType ServicePrincipal

if ($isMhsm) {
  Write-Host "Assigning permissions on the HSM"
  Assign-Permission-HSM `
    -role "Managed HSM Crypto User" `
    -objectId $appId `
    -principalType ServicePrincipal
}
else {
  Write-Host "Assigning permissions on the Key Vault"
  Assign-Permission-KeyVault `
    -role "Key Vault Crypto Officer" `
    -objectId $appId `
    -principalType ServicePrincipal
}
Write-Host "Assigning Secrets User permission on the Key Vault"
Assign-Secrets-User-Permission-KeyVault `
  -objectId $appId `
  -principalType ServicePrincipal


if ($env:COLLAB_FORCE_MANAGED_IDENTITY -eq "true") {
  Write-Host "Skipping setting up federation for $subject due to MSFT tenant policy"
}
else {
  if ($identityType -eq "managed_identity") {
    Write-Host "Setting up federation on managed identity $MANAGED_IDENTITY_NAME with issuerUrl $issuerUrl and subject $subject"
    az identity federated-credential create `
      --name "$subject-federation" `
      --identity-name $MANAGED_IDENTITY_NAME `
      --resource-group $resourceGroup `
      --issuer $issuerUrl `
      --subject $subject `
      --audiences "api://AzureADTokenExchange"
  }
  else {
    $parameters = @{
      name      = "$subject-federation"
      issuer    = $issuerUrl
      subject   = $subject
      audiences = @("api://AzureADTokenExchange")
    }

    $jsonParameters = $parameters | ConvertTo-Json

    write-Host "Creating federated credential with parameters: $jsonParameters"
    az ad app federated-credential create `
      --id $app.appId `
      --parameters $jsonParameters
  }
}

if ($env:GITHUB_ACTIONS -eq "true") {
  $sleepTime = 30
  # See Note at https://learn.microsoft.com/en-us/azure/aks/workload-identity-deploy-cluster#create-the-federated-identity-credential
  Write-Host "Waiting for $sleepTime seconds for federated identity credential to propagate after it is added"
  Start-Sleep -Seconds $sleepTime
}