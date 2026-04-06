[CmdletBinding()]
param
(
    [Parameter(Mandatory = $true)]
    [string]$resourceGroup,

    [Parameter(Mandatory = $true)]
    [string]$storageAccountName,

    [string]$storageAccountResourceGroup = "azcleanroom-public-pr-rg",

    [string]$subscriptionName = "AzureCleanRoom-NonProd",

    [string]$resourceGroupTags = "",

    [Parameter(Mandatory = $true)]
    [string]$outDir
)

# https://learn.microsoft.com/en-us/archive/blogs/389thoughts/get-uniquestring-generate-unique-id-for-azure-deployments
function Get-UniqueString ([string]$id, $length = 13) {
    $hashArray = (new-object System.Security.Cryptography.SHA512Managed).ComputeHash($id.ToCharArray())
    -join ($hashArray[1..$length] | ForEach-Object { [char]($_ % 26 + [byte][char]'a') })
}

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

Write-Host "Setting subscription to '$subscriptionName'..."
az account set --subscription $subscriptionName

$uniqueString = Get-UniqueString("${resourceGroup}")
$managedIdentityName = "${uniqueString}-mi"

# Create resource group if it does not exist.
& {
    $PSNativeCommandUseErrorActionPreference = $false
    $script:rgExists = (az group show --name $resourceGroup 2>$null) | ConvertFrom-Json
}

if ($null -ne $rgExists) {
    Write-Host "Resource group '$resourceGroup' already exists, skipping creation."
}
else {
    Write-Host "Creating resource group '$resourceGroup'..."
    az group create --location westus --name $resourceGroup --tags $resourceGroupTags
}

# Check if the managed identity already exists; create only if it does not.
& {
    $PSNativeCommandUseErrorActionPreference = $false
    $script:miResult = (az identity show `
            --name $managedIdentityName `
            --resource-group $resourceGroup 2>$null) | ConvertFrom-Json
}

if ($null -ne $miResult) {
    Write-Host "Managed identity '$managedIdentityName' already exists, skipping creation."
}
else {
    Write-Host "Creating user-assigned managed identity '$managedIdentityName' in resource group '$resourceGroup'..."
    $script:miResult = $null
    & {
        $PSNativeCommandUseErrorActionPreference = $false
        # Add retry as at times the managed identity creation fails with a 499 error.
        foreach ($value in 1..5) {
            $script:miResult = (az identity create `
                    --name $managedIdentityName `
                    --resource-group $resourceGroup) | ConvertFrom-Json
            if ($script:miResult) { break } else { Write-Host "Managed identity creation failed. Will retry after 5s..."; Start-Sleep 5 }
        }
    }

    if ($null -eq $miResult) {
        throw "Managed identity creation failed after multiple retries."
    }
}

# Look up the storage account in its resource group.
$storageAccount = (az storage account show `
        --name $storageAccountName `
        --resource-group $storageAccountResourceGroup) | ConvertFrom-Json

# Assign Storage Blob Data Contributor on the storage account if not already assigned.
$miRole = "Storage Blob Data Contributor"
$miRoleAssignment = (az role assignment list `
        --assignee-object-id $miResult.principalId `
        --scope $storageAccount.id `
        --role $miRole `
        --fill-principal-name false `
        --fill-role-definition-name false) | ConvertFrom-Json

if ($miRoleAssignment.Length -eq 1) {
    Write-Host "'$miRole' permission for MI already exists, skipping assignment."
}
else {
    Write-Host "Assigning '$miRole' to managed identity '$managedIdentityName'..."
    az role assignment create `
        --role $miRole `
        --scope $storageAccount.id `
        --assignee-object-id $miResult.principalId `
        --assignee-principal-type ServicePrincipal
}

Write-Host "Managed identity setup complete."

# Write mi-resources.generated.json next to this script.
$resources = @{
    mi = $miResult
}

$resourcesFile = Join-Path $outDir "mi-resources.generated.json"
$resources | ConvertTo-Json -Depth 100 > $resourcesFile
Write-Host "MI resources written to $resourcesFile"
