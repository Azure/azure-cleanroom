# Cleanup script for SKR validation test.
# Deletes the CACI container group. Optionally deletes the resource group.

[CmdletBinding()]
param
(
    [string]$resourceGroup = "cl-skr-validation-rg",

    [string]$containerGroupName = "skr-validation-test",

    [switch]$deleteResourceGroup
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# Delete CACI container group.
Write-Host "Deleting container group '$containerGroupName' in '$resourceGroup'..."
& {
    $PSNativeCommandUseErrorActionPreference = $false
    az container delete --name $containerGroupName --resource-group $resourceGroup --yes 2>$null
}

if ($deleteResourceGroup) {
    Write-Host "Deleting resource group '$resourceGroup'..."
    az group delete --name $resourceGroup --yes --no-wait
    Write-Host "Resource group deletion initiated (async)."
}
else {
    Write-Host "Resource group '$resourceGroup' preserved. Use -deleteResourceGroup to remove."
}
