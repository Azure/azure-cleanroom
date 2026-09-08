param(
    [Parameter(Mandatory = $true)]
    [string]$governanceClient,

    [Parameter(Mandatory = $true)]
    [string]$oidcContainerName,

    [string]$outDir = "$PSScriptRoot/generated"
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. $PSScriptRoot/helpers.ps1

$oidcStorageAccountName = "cleanroomoidc"
mkdir -p $outDir

$root = git rev-parse --show-toplevel
Import-Module $root/samples/common/infra-scripts/azure-helpers.psm1 -Force -DisableNameChecking

# Check whether an issuer is already set for the authenticated caller's tenant.
$oidcInfo = (az cleanroom governance oidc-issuer show `
        --governance-client $governanceClient | ConvertFrom-Json)

if ($null -ne $oidcInfo -and $null -ne $oidcInfo.tenantData.issuerUrl) {
    Write-Host -ForegroundColor Yellow "OIDC issuer already set for the caller's tenant, skipping."
    $issuerUrl = $oidcInfo.tenantData.issuerUrl
    Write-Output $issuerUrl > $outDir/issuer-url.txt
    return
}

# Use the pre-provisioned storage account for OIDC setup.
Write-Host "Using pre-provisioned storage account '$oidcStorageAccountName'..."
$storageAccountResult = (az storage account show `
        --name $oidcStorageAccountName) | ConvertFrom-Json

$status = (az storage blob service-properties show `
        --account-name $oidcStorageAccountName `
        --auth-mode login `
        --query "staticWebsite.enabled" `
        --output tsv)
if ($status -ne "true") {
    throw "Pre-provisioned storage account '$oidcStorageAccountName' should have static website enabled."
}

# Assign Storage Blob Data Contributor to logged-in user if not already assigned.
$objectId = GetLoggedInEntityObjectId
Ensure-RoleAssignment `
    -assigneeObjectId $objectId `
    -scope $storageAccountResult.id `
    -role "Storage Blob Data Contributor" `
    -principalType $(Get-Assignee-Principal-Type)

# Get the static website URL.
$webUrl = (az storage account show `
        --name $storageAccountResult.name `
        --query "primaryEndpoints.web" `
        --output tsv)
Write-Host "Storage account static website URL: $webUrl"

# Create and upload openid-configuration.json.
@"
{
  "issuer": "$webUrl${oidcContainerName}",
  "jwks_uri": "$webUrl${oidcContainerName}/openid/v1/jwks",
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
    --name ${oidcContainerName}/.well-known/openid-configuration `
    --account-name $storageAccountResult.name `
    --overwrite `
    --auth-mode login

# Fetch JWKS from the CCF endpoint and upload.
$ccfEndpoint = (az cleanroom governance client show --name $governanceClient | ConvertFrom-Json)
$url = "$($ccfEndpoint.ccfEndpoint)/app/oidc/keys"
curl -s -k $url | jq > $outDir/jwks.json

az storage blob upload `
    --container-name '$web' `
    --file $outDir/jwks.json `
    --name ${oidcContainerName}/openid/v1/jwks `
    --account-name $storageAccountResult.name `
    --overwrite `
    --auth-mode login

$issuerUrl = "$webUrl${oidcContainerName}"
az cleanroom governance oidc-issuer set-issuer-url `
    --governance-client $governanceClient `
    --url $issuerUrl
Write-Output $issuerUrl > $outDir/issuer-url.txt
Write-Host "OIDC issuer URL: $issuerUrl"
