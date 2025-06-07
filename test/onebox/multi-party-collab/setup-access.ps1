param(
  [Parameter(Mandatory = $true)]
  [string]$resourceGroup,
  [Parameter(Mandatory = $true)]
  [string]$governanceClient,
  [Parameter(Mandatory = $true)]
  [string]$contractId,
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
      --assignee $objectId `
      --scope $keyVaultResult.id `
      --role $role) | ConvertFrom-Json

  if ($roleAssignment.Length -eq 1) {
    Write-Host "Key Vault Crypto Officer permission on the key vault already exists, skipping assignment"
  }
  else {
    Write-Host "Assigning Key Vault Crypto Officer on the Key Vault"
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
  az role assignment create `
    --role "Key Vault Secrets User" `
    --scope $keyVaultResult.id `
    --assignee-object-id $objectId `
    --assignee-principal-type ServicePrincipal
}

function Assign-Permission-StorageAccount {
  param(
    [string]$objectId,
    [string]$principalType
  )

  $storageAccount = (az storage account show `
      --name $STORAGE_ACCOUNT_NAME `
      --resource-group $resourceGroup) | ConvertFrom-Json

  Write-Host "Assigning Storage Blob Data Contributor on the storage account"
  az role assignment create `
    --role "Storage Blob Data Contributor" `
    --scope $storageAccount.id `
    --assignee-object-id $objectId `
    --assignee-principal-type $principalType
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

# Set OIDC issuer.
$currentUser = (az account show) | ConvertFrom-Json
$tenantId = $currentUser.tenantid
$tenantData = (az cleanroom governance oidc-issuer show `
    --governance-client $governanceClient `
    --query "tenantData" | ConvertFrom-Json)
if ($null -ne $tenantData -and $tenantData.tenantId -eq $tenantId) {
  Write-Host -ForegroundColor Yellow "OIDC issuer already set for the tenant, skipping."
  $issuerUrl = $tenantData.issuerUrl
}
else {
  Write-Host "Setting up OIDC issuer for the tenant $tenantId"
  $storageAccountResult = $null
  # for MSFT tenant 72f988bf-86f1-41af-91ab-2d7cd011db47 we must also use pre-provisioned storage account.
  if ($env:USE_PREPROVISIONED_OIDC -eq "true" -or $tenantId -eq "72f988bf-86f1-41af-91ab-2d7cd011db47") {
    Write-Host "Use pre-provisioned storage account for OIDC setup"
    $preprovisionedSAName = "cleanroomoidc"
    $storageAccountResult = (az storage account show `
        --name $preprovisionedSAName) | ConvertFrom-Json

    $status = (az storage blob service-properties show `
        --account-name $preprovisionedSAName `
        --auth-mode login `
        --query "staticWebsite.enabled" `
        --output tsv)
    if ($status -ne "true") {
      throw "Preprovisioned storage account $preprovisionedSAName should have static website enabled."
    }
  }
  else {
    $storageAccountResult = (az storage account create `
        --resource-group "$resourceGroup" `
        --name "${OIDC_STORAGE_ACCOUNT_NAME}" ) | ConvertFrom-Json

    Write-Host "Setting up static website on storage account to setup oidc documents endpoint"
    az storage blob service-properties update `
      --account-name $storageAccountResult.name `
      --static-website `
      --404-document error.html `
      --index-document index.html `
      --auth-mode login
  }

  $objectId = GetLoggedInEntityObjectId
  Write-Host "Assigning 'Storage Blob Data Contributor' permissions to logged in user"
  az role assignment create `
    --role "Storage Blob Data Contributor" `
    --scope $storageAccountResult.id `
    --assignee-object-id $objectId `
    --assignee-principal-type $(Get-Assignee-Principal-Type)

  if ($env:GITHUB_ACTIONS -eq "true") {
    & {
      # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
      $PSNativeCommandUseErrorActionPreference = $false
      $timeout = New-TimeSpan -Seconds 120
      $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
      $hasAccess = $false
      while (!$hasAccess) {
        # Do container/blob creation check to determine whether the permissions have been applied or not.
        az storage container create --name ghaction-c --account-name $storageAccountResult.name --auth-mode login
        az storage blob upload --data "teststring" --overwrite -c ghaction-c -n ghaction-b --account-name $storageAccountResult.name --auth-mode login
        if ($LASTEXITCODE -gt 0) {
          if ($stopwatch.elapsed -gt $timeout) {
            throw "Hit timeout waiting for rbac permissions to be applied on the storage account."
          }
          $sleepTime = 10
          Write-Host "Waiting for $sleepTime seconds before checking if permissions got applied..."
          Start-Sleep -Seconds $sleepTime
        }
        else {
          Write-Host "Blob creation check returned $LASTEXITCODE. Assuming permissions got applied."
          $hasAccess = $true
        }
      }
    }
  }

  $webUrl = (az storage account show `
      --name $storageAccountResult.name `
      --query "primaryEndpoints.web" `
      --output tsv)
  Write-Host "Storage account static website URL: $webUrl"

  @"
{
"issuer": "$webUrl${OIDC_CONTAINER_NAME}",
"jwks_uri": "$webUrl${OIDC_CONTAINER_NAME}/openid/v1/jwks",
"response_types_supported": [
"id_token"
],
"subject_types_supported": [
"public"
],
"id_token_signing_alg_values_supported": [
"RS256"
]
}
"@ > $outDir/openid-configuration.json

  az storage blob upload `
    --container-name '$web' `
    --file $outDir/openid-configuration.json `
    --name ${OIDC_CONTAINER_NAME}/.well-known/openid-configuration `
    --account-name $storageAccountResult.name `
    --overwrite `
    --auth-mode login

  $ccfEndpoint = (az cleanroom governance client show --name $governanceClient | ConvertFrom-Json)
  $url = "$($ccfEndpoint.ccfEndpoint)/app/oidc/keys"
  curl -s -k $url | jq > $outDir/jwks.json

  az storage blob upload `
    --container-name '$web' `
    --file $outDir/jwks.json `
    --name ${OIDC_CONTAINER_NAME}/openid/v1/jwks `
    --account-name $storageAccountResult.name `
    --overwrite `
    --auth-mode login

  az cleanroom governance oidc-issuer set-issuer-url `
    --governance-client $governanceClient `
    --url "$webUrl${OIDC_CONTAINER_NAME}"
  $tenantData = (az cleanroom governance oidc-issuer show `
      --governance-client $governanceClient `
      --query "tenantData" | ConvertFrom-Json)
  $issuerUrl = $tenantData.issuerUrl
}

if ($env:COLLAB_FORCE_MANAGED_IDENTITY -eq "true") {
  Write-Host "Skipping setting up federation for $contractId due to MSFT tenant policy"
}
else {
  if ($identityType -eq "managed_identity") {
    Write-Host "Setting up federation on managed identity with issuerUrl $issuerUrl and subject $contractId"
    az identity federated-credential create `
      --name "$contractId-federation" `
      --identity-name $MANAGED_IDENTITY_NAME `
      --resource-group $resourceGroup `
      --issuer $issuerUrl `
      --subject $contractId `
      --audience api://AzureADTokenExchange
  }
  else {
    $parameters = @{
      name      = "$contractId-federation"
      issuer    = $issuerUrl
      subject   = $contractId
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

function Get-Assignee-Principal-Type {
  if ($env:GITHUB_ACTIONS -eq "true") {
    return "ServicePrincipal"
  }
  else {
    return "User"
  }
}
