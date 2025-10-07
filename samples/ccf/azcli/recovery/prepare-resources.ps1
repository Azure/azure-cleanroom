[CmdletBinding()]
param
(
    [Parameter(Mandatory = $true)]
    [string]$resourceGroup,

    [string]
    [ValidateSet('virtual', 'caci')]
    $infraType = "caci",

    [string]$outDir = ""
)

function Get-UniqueString ([string]$id, $length = 13) {
    $hashArray = (new-object System.Security.Cryptography.SHA512Managed).ComputeHash($id.ToCharArray())
    -join ($hashArray[1..$length] | ForEach-Object { [char]($_ % 26 + [byte][char]'a') })
}

function Assign-Permission-KeyVault {
    param(
        [string]$role,
        [string]$objectId,
        [string]$principalType
    )

    $keyVaultResult = (az keyvault show --name $KEYVAULT_NAME --resource-group $resourceGroup) | ConvertFrom-Json
    $roleAssignment = (az role assignment list `
            --assignee-object-id $objectId `
            --scope $keyVaultResult.id `
            --role $role `
            --fill-principal-name false `
            --fill-role-definition-name false) | ConvertFrom-Json

    if ($roleAssignment.Length -eq 1) {
        Write-Host "$role permission on the key vault already exists, skipping assignment."
    }
    else {
        Write-Host "Assigning $role on the Key Vault."
        az role assignment create `
            --role $role `
            --scope $keyVaultResult.id `
            --assignee-object-id $objectId `
            --assignee-principal-type $principalType
    }
}

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$build = "$root/build"

. $root/build/helpers.ps1
Import-Module $root/samples/common/infra-scripts/azure-helpers.psm1 -Force -DisableNameChecking

if ($outDir -eq "") {
    $outDir = "$PSScriptRoot/sandbox_common"
}

$sandbox_common = $outDir
mkdir -p $sandbox_common

Write-Output "Creating Key Vault instance for use with CCF recovery service."
$uniqueString = Get-UniqueString("${resourceGroup}")
$KEYVAULT_NAME = "${uniqueString}akv"
$MANAGED_IDENTITY_NAME = "${uniqueString}-mi"
$objectId = GetLoggedInEntityObjectId
$kv = Create-KeyVault `
    -resourceGroup $resourceGroup `
    -keyVaultName $KEYVAULT_NAME `
    -adminObjectId $objectId `
    -sku premium
$kvId = $kv.id

$miId = "none"
if ($infraType -eq "caci") {
    Write-Host "Creating managed identity $MANAGED_IDENTITY_NAME in resource group $resourceGroup."
    $script:mi = $null;
    & {
        # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
        $PSNativeCommandUseErrorActionPreference = $false
        # Add retry as at times the managed identity creation fails with a 499 error.
        foreach ($value in 1..5) {
            $script:mi = az identity create -n $MANAGED_IDENTITY_NAME -g $resourceGroup --location westeurope | ConvertFrom-Json;
            if ($script:mi) { break } else { Write-Host "Managed identity creation failed. Will retry after 5s..."; Start-Sleep 5 }
        }
    }

    if ($null -eq $script:mi) {
        throw "Managed identity creation failed after multiple retries."
    }

    $miId = $script:mi.id
    $appId = (az identity show `
            --name  $MANAGED_IDENTITY_NAME `
            --resource-group $resourceGroup | ConvertFrom-Json).principalId

    $sleepTime = 10
    if ($env:GITHUB_ACTIONS -eq "true") {
        $sleepTime = 45
    }
    Write-Host "Waiting $sleepTime seconds after MI creation with objectId $appId else rbac assignment fails at times."
    Start-Sleep -Seconds $sleepTime

    Write-Host "Assigning permissions for managing keys to the managed identity on the Key Vault."
    Assign-Permission-KeyVault `
        -role "Key Vault Crypto Officer" `
        -objectId $appId `
        -principalType ServicePrincipal

    Write-Host "Assigning permissions for managing secrets to the managed identity on the Key Vault."
    Assign-Permission-KeyVault `
        -role "Key Vault Secrets Officer" `
        -objectId $appId `
        -principalType ServicePrincipal
}

if ($env:GITHUB_ACTIONS -eq "true") {
    # A new KV is used for each github action run so using a fixed name is ok.
    $confidentialRecovererMemberName = "conf-recoverer"
}
else {
    # Use a unique value for local runs as as the keys for this member get tied to the
    # recovery service hostdata value for the run.
    $confidentialRecovererMemberName = "conf-recoverer-" + (New-Guid).ToString().Substring(0, 8)
}

$resources = @{}
$resources.kvId = $kvId
$resources.confidentialRecovererMemberName = $confidentialRecovererMemberName
$resources.miId = $miId
$resources.maaEndpoint = "sharedneu.neu.attest.azure.net"
$resources | ConvertTo-Json -Depth 100 > $sandbox_common/recoveryResources.json