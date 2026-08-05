# One-time setup: creates permanent Azure resources for SKR validation.
# Resources: Key Vault (Premium SKU), Managed Identity, Resource Group.
# Generates the CCE policy from the ARM template using az confcom acipolicygen,
# then imports a key with an SKR release policy matching the generated CCE policy hash.
# No CCF, CGS, OIDC, or storage account needed.

[CmdletBinding()]
param
(
    [string]$resourceGroup = "cl-skr-validation-rg",

    [string]$location = "uksouth",

    [string]$keyName = "skr-test-key",

    [string]$outDir = "$PSScriptRoot/generated"
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
Import-Module $root/samples/common/infra-scripts/azure-helpers.psm1 -Force -DisableNameChecking

mkdir -p $outDir/$resourceGroup
$resourcesFile = "$outDir/$resourceGroup/resources.json"

# Create resource group (idempotent: if it already exists in a different location, use it as-is).
$script:existingRg = $null
& {
    $PSNativeCommandUseErrorActionPreference = $false
    $script:existingRg = az group show --name $resourceGroup 2>$null | ConvertFrom-Json
}
if ($script:existingRg) {
    Write-Host "Resource group $resourceGroup already exists in $($script:existingRg.location), using it."
    $location = $script:existingRg.location
}
else {
    Write-Host "Creating resource group $resourceGroup in $location"
    az group create --location $location --name $resourceGroup --tags "purpose=skr-validation"
}

$objectId = GetLoggedInEntityObjectId

# Create Key Vault with Premium SKU for HSM-backed keys supporting SKR release policies.
function Get-UniqueString ([string]$id, $length = 13) {
    $hashArray = (new-object System.Security.Cryptography.SHA512Managed).ComputeHash($id.ToCharArray())
    -join ($hashArray[1..$length] | ForEach-Object { [char]($_ % 26 + [byte][char]'a') })
}
$uniqueString = Get-UniqueString("${resourceGroup}")
$kvName = "${uniqueString}kv"
$miName = "${uniqueString}-mi"

# Fast path: if all required infra already exists and resources.json has ccePolicy, skip setup work.
$cachedResources = $null
$cachedCcePolicy = $null
if (Test-Path $resourcesFile) {
    $cachedResources = Get-Content $resourcesFile | ConvertFrom-Json
    $cachedCcePolicy = $cachedResources.ccePolicy
    if ($cachedCcePolicy -is [array]) {
        $cachedCcePolicy = ($cachedCcePolicy | Where-Object { $_ }) -join ''
    }
    if ($null -ne $cachedCcePolicy) {
        $cachedCcePolicy = $cachedCcePolicy.Trim()
    }
}

if (-not [string]::IsNullOrEmpty($cachedCcePolicy)) {
    $script:existingKv = $null
    $script:existingMi = $null
    $script:existingKeyFastPath = $null
    & {
        $PSNativeCommandUseErrorActionPreference = $false
        $script:existingKv = az keyvault show --name $kvName --resource-group $resourceGroup 2>$null | ConvertFrom-Json
        $script:existingMi = az identity show --name $miName --resource-group $resourceGroup 2>$null | ConvertFrom-Json
        $script:existingKeyFastPath = az keyvault key show --vault-name $kvName --name $keyName 2>$null | ConvertFrom-Json
    }

    $hasRoleAssignment = $false
    if ($script:existingKv -and $script:existingMi) {
        $roleAssignments = (az role assignment list `
            --assignee-object-id $script:existingMi.principalId `
            --scope $script:existingKv.id `
            --role "Key Vault Crypto Officer" `
            --fill-principal-name false `
            --fill-role-definition-name false) | ConvertFrom-Json

        $hasRoleAssignment = @($roleAssignments).Count -ge 1
    }

    # Only use the fast-path if the key's release policy already has the current CCE hash.
    # This guards against a stale hash stored in resources.json (e.g. after an image update).
    $hashMatchesCached = $false
    if ($script:existingKeyFastPath) {
        $fastPathPolicyJson = $script:existingKeyFastPath.releasePolicy.encodedPolicy
        if (-not [string]::IsNullOrEmpty($fastPathPolicyJson)) {
            $fastPathPolicy = $fastPathPolicyJson | ConvertFrom-Json
            $fastPathPolicyBytes = [System.Convert]::FromBase64String($cachedCcePolicy)
            $fastPathSha256 = [System.Security.Cryptography.SHA256]::Create()
            $fastPathHashBytes = $fastPathSha256.ComputeHash($fastPathPolicyBytes)
            $cachedCcePolicyHash = [System.BitConverter]::ToString($fastPathHashBytes).Replace("-", "").ToLower()
            $fastPathHash = $fastPathPolicy.anyOf[0].allOf | Where-Object { $_.claim -eq "x-ms-sevsnpvm-hostdata" } | Select-Object -ExpandProperty equals
            $hashMatchesCached = ($fastPathHash -eq $cachedCcePolicyHash)
        }
    }

    if ($script:existingKv -and $script:existingMi -and $script:existingKeyFastPath -and $hasRoleAssignment -and $hashMatchesCached) {
        Write-Host -ForegroundColor Green "All SKR resources already exist and are valid. Skipping setup."
        Write-Host "Using KV: $kvName | MI: $miName | Key: $keyName"
        return
    }

    Write-Host "Cached resources.json found, but one or more Azure dependencies are missing or stale. Running setup to repair."
}

Write-Host "Creating Key Vault $kvName in resource group $resourceGroup"
$kvResult = Create-KeyVault `
    -resourceGroup $resourceGroup `
    -keyVaultName $kvName `
    -sku premium `
    -adminObjectId $objectId

# Create Managed Identity.
Write-Host "Creating managed identity $miName in resource group $resourceGroup"
$script:miResult = $null
& {
    $PSNativeCommandUseErrorActionPreference = $false
    foreach ($value in 1..5) {
        $script:miResult = az identity create -n $miName -g $resourceGroup | ConvertFrom-Json
        if ($script:miResult) { break } else { Write-Host "Managed identity creation failed. Will retry after 5s..."; Start-Sleep 5 }
    }
}

if ($null -eq $script:miResult) {
    throw "Managed identity creation failed after multiple retries."
}

# Assign Key Vault Crypto Officer role to the managed identity.
$miObjectId = $script:miResult.principalId
$role = "Key Vault Crypto Officer"
$roleAssignment = (az role assignment list `
    --assignee-object-id $miObjectId `
    --scope $kvResult.id `
    --role $role `
    --fill-principal-name false `
    --fill-role-definition-name false) | ConvertFrom-Json

if ($roleAssignment.Length -eq 1) {
    Write-Host "$role permission on the key vault already exists, skipping assignment"
}
else {
    Write-Host "Assigning $role on the Key Vault to managed identity"
    az role assignment create `
        --role $role `
        --scope $kvResult.id `
        --assignee-object-id $miObjectId `
        --assignee-principal-type ServicePrincipal
}

# Determine MAA endpoint based on location.
$maaEndpoints = @{
    "westeurope"        = "sharedweu.weu.attest.azure.net"
    "northeurope"       = "sharedneu.neu.attest.azure.net"
    "eastus"            = "sharedeus.eus.attest.azure.net"
    "westus"            = "sharedwus.wus.attest.azure.net"
    "germanywestcentral" = "sharedgermanywc.germanywc.attest.azure.net"
    "centralindia"      = "sharedcin.cin.attest.azure.net"
    "eastasia"          = "sharedea.ea.attest.azure.net"
    "southeastasia"     = "sharedsea.sea.attest.azure.net"
    "uksouth"           = "shareduks.uks.attest.azure.net"
}
$maaEndpoint = $maaEndpoints[$location]
if ($null -eq $maaEndpoint) {
    $maaEndpoint = "sharedneu.neu.attest.azure.net"
    Write-Host -ForegroundColor Yellow "No MAA endpoint found for $location, defaulting to $maaEndpoint"
}

# Generate the CCE policy from the ARM template using az confcom.
$templateFile = "$PSScriptRoot/aci-arm-template.json"
Write-Host "Generating CCE policy from ARM template..."
$ccePolicyBase64 = az confcom acipolicygen --template-file $templateFile --approve-wildcards --print-policy
# az confcom output may be captured as an array in PowerShell (one element per output line).
# Join and trim to get a clean base64 string.
if ($ccePolicyBase64 -is [array]) {
    $ccePolicyBase64 = ($ccePolicyBase64 | Where-Object { $_ }) -join ''
}
$ccePolicyBase64 = $ccePolicyBase64.Trim()
Write-Host "CCE policy generated."

# Compute hash of the CCE policy for the SKR release policy.
$ccePolicyBytes = [System.Convert]::FromBase64String($ccePolicyBase64)
$sha256 = [System.Security.Cryptography.SHA256]::Create()
$hashBytes = $sha256.ComputeHash($ccePolicyBytes)
$ccePolicyHash = [System.BitConverter]::ToString($hashBytes).Replace("-", "").ToLower()
Write-Host "CCE Policy Hash: $ccePolicyHash"

# Build the SKR release policy JSON (needed for key creation or policy update).
$skrPolicy = @{
    anyOf = @(
        @{
            allOf = @(
                @{ claim = "x-ms-sevsnpvm-hostdata"; equals = $ccePolicyHash }
                @{ claim = "x-ms-compliance-status"; equals = "azure-compliant-uvm" }
                @{ claim = "x-ms-attestation-type"; equals = "sevsnpvm" }
            )
            authority = "https://$maaEndpoint"
        }
    )
    version = "1.0.0"
} | ConvertTo-Json -Depth 10

$skrPolicyFile = "$outDir/skr-release-policy.json"
$skrPolicy | Out-File -FilePath $skrPolicyFile -Encoding utf8

# Create key with SKR release policy matching the generated CCE policy hash.
# If the key already exists, check whether its release policy hash matches the current CCE policy
# hash. If not (e.g. image digest changed), update the release policy so SKR succeeds.
$script:existingKey = $null
& {
    $PSNativeCommandUseErrorActionPreference = $false
    $script:existingKey = az keyvault key show --vault-name $kvName --name $keyName 2>$null | ConvertFrom-Json
}

if ($script:existingKey) {
    # Check if the existing key's release policy has the current hash.
    $existingPolicyJson = $script:existingKey.releasePolicy.encodedPolicy
    $policyNeedsUpdate = $true
    if (-not [string]::IsNullOrEmpty($existingPolicyJson)) {
        $existingPolicy = $existingPolicyJson | ConvertFrom-Json
        $existingHash = $existingPolicy.anyOf[0].allOf | Where-Object { $_.claim -eq "x-ms-sevsnpvm-hostdata" } | Select-Object -ExpandProperty equals
        if ($existingHash -eq $ccePolicyHash) {
            $policyNeedsUpdate = $false
        }
    }
    if ($policyNeedsUpdate) {
        Write-Host "Key '$keyName' exists but release policy hash mismatch. Updating release policy to $ccePolicyHash..."
        az keyvault key set-attributes --vault-name $kvName --name $keyName --policy $skrPolicyFile
    }
    else {
        Write-Host "Key '$keyName' already exists with correct release policy hash. Skipping."
    }
}
else {
    Write-Host "Creating key '$keyName' in Key Vault '$kvName' with SKR release policy..."

    # Retry key creation: DNS for a newly created Key Vault can take 30-60s to propagate,
    # and RBAC role assignments also have a propagation delay. Both cause transient failures.
    # Use $script: scope so the flag is visible outside the scriptblock.
    $script:keyCreated = $false
    & {
        $PSNativeCommandUseErrorActionPreference = $false
        foreach ($attempt in 1..10) {
            az keyvault key create `
                --vault-name $kvName `
                --name $keyName `
                --kty RSA `
                --protection hsm `
                --exportable true `
                --policy $skrPolicyFile 2>&1
            if ($LASTEXITCODE -eq 0) {
                $script:keyCreated = $true
                break
            }
            Write-Host "Key creation attempt $attempt failed (DNS or RBAC may still be propagating). Retrying in 30s..."
            Start-Sleep 30
        }
    }
    if (-not $script:keyCreated) {
        throw "Failed to create key '$keyName' after multiple retries."
    }
}

# Save results including the generated CCE policy.
$result = @{
    resourceGroup = $resourceGroup
    location      = $location
    kvName        = $kvName
    kvId          = $kvResult.id
    kvEndpoint    = "$kvName.vault.azure.net"
    miName        = $miName
    miId          = $script:miResult.id
    miPrincipalId = $miObjectId
    maaEndpoint   = $maaEndpoint
    keyName       = $keyName
    ccePolicy     = $ccePolicyBase64
}

$result | ConvertTo-Json -Depth 10 > $resourcesFile

Write-Host -ForegroundColor Green "Setup complete. Resources saved to $resourcesFile"
Write-Host "KV: $kvName | MI: $miName | MAA: $maaEndpoint | Key: $keyName"
