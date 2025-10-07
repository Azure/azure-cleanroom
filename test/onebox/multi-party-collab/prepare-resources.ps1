param(
    [Parameter(Mandatory = $true)]
    [string]$resourceGroup,

    [Parameter(Mandatory = $true)]
    [ValidateSet("mhsm", "akvpremium")]
    [string]$kvType,

    
    [Parameter()]
    [ValidateSet("managed_identity", "service_principal")]
    [string]$identityType = "managed_identity",

    [string]$outDir = "",

    [Parameter()]
    [string]$backupKv = "",

    [string]$overridesFilePath = "",

    [string]$resourceGroupTags = ""
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
Import-Module $root/samples/common/infra-scripts/azure-helpers.psm1 -Force -DisableNameChecking

mkdir -p $outDir/$resourceGroup

pwsh $PSScriptRoot/generate-names.ps1 `
    -resourceGroup $resourceGroup `
    -kvType $kvType `
    -overridesFilePath $overridesFilePath `
    -backupKv $backupKv `
    -outDir $outDir/$resourceGroup

. $outDir/$resourceGroup/names.generated.ps1
$sandbox_common = $outDir

# create resource group
Write-Host "Creating resource group $resourceGroup in $RESOURCE_GROUP_LOCATION"
az group create --location $RESOURCE_GROUP_LOCATION --name $resourceGroup --tags $resourceGroupTags

$objectId = GetLoggedInEntityObjectId
$result = @{
    kek          = @{}
    dek          = @{}
    sa           = @{}
    mi           = @{}
    maa_endpoint = ""
}

if ($kvType -eq "mhsm") {
    Write-Host "Creating HSM $MHSM_NAME in resource group $HSM_RESOURCE_GROUP"
    $keyStore = Create-Hsm `
        -resourceGroup $HSM_RESOURCE_GROUP `
        -hsmName $MHSM_NAME `
        -adminObjectId $objectId `
        -outDir $sandbox_common

    $result.kek.kv = $keyStore
    # Creating the Key Vault upfront so as not to run into naming issues
    # while storing the wrapped DEK
    Write-Host "Creating Key Vault to store the wrapped DEK"
    $result.dek.kv = Create-KeyVault `
        -resourceGroup $resourceGroup `
        -keyVaultName $KEYVAULT_NAME `
        -adminObjectId $objectId
}
else {
    Write-Host "Creating Key Vault $KEYVAULT_NAME in resource group $resourceGroup"
    $result.kek.kv = Create-KeyVault `
        -resourceGroup $resourceGroup `
        -keyVaultName $KEYVAULT_NAME `
        -sku premium `
        -adminObjectId $objectId
    $result.dek.kv = $result.kek.kv
}

$storageAccount = Create-Storage-Resources `
    -resourceGroup $resourceGroup `
    -storageAccountName @($STORAGE_ACCOUNT_NAME) `
    -objectId $objectId
$result.sa = $storageAccount

if ($identityType -eq "service_principal") {
    # Create the enterprise application for one lake instead of managed identity
    Write-Host "Creating enterprise application for OneLake $ENTERPRISE_APP_NAME"
    $app = az ad app create --display-name $ENTERPRISE_APP_NAME | ConvertFrom-Json

    # Create a service principal for the application
    $sp = az ad sp create --id $app.appId | ConvertFrom-Json
    $result.app = $app

}
else {
    Write-Host "Creating managed identity $MANAGED_IDENTITY_NAME in resource group $resourceGroup"
    $script:managedIdentityResult = $null;
    & {
        # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
        $PSNativeCommandUseErrorActionPreference = $false
        # Add retry as at times the managed identity creation fails with a 499 error.
        foreach ($value in 1..5) {
            $script:managedIdentityResult = az identity create -n $MANAGED_IDENTITY_NAME -g $resourceGroup | ConvertFrom-Json;
            if ($script:managedIdentityResult) { break } else { Write-Host "Managed identity creation failed. Will retry after 5s..."; Start-Sleep 5 }
        }
    }

    if ($null -eq $script:managedIdentityResult) {
        throw "Managed identity creation failed after multiple retries."
    }

    $result.mi = $script:managedIdentityResult
}

$result.maa_endpoint = $MAA_URL

$result | ConvertTo-Json -Depth 100 > $outDir/$resourceGroup/resources.generated.json
return $result