[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [string]
    $rgName
)

$root = git rev-parse --show-toplevel
Import-Module $root/samples/common/infra-scripts/azure-helpers.psm1 -Force -DisableNameChecking

$objectId = GetLoggedInEntityObjectId
$rg = az group create --name $rgName --location westeurope
CheckLastExitCode

$rg = $rg | ConvertFrom-Json
az role assignment create `
    --role Contributor `
    --scope $rg.id `
    --assignee-object-id $objectId `
    --assignee-principal-type ServicePrincipal
CheckLastExitCode

# Assign permissions to the managed identity that queries for container exit codes in tests.
az role assignment create `
    --role Contributor `
    --scope $rg.id `
    --assignee-object-id "a701ad2d-dc45-4357-b99c-e8842559f31d" `
    --assignee-principal-type ServicePrincipal
CheckLastExitCode

# Wait a short duration to let the permissions take effect
Start-Sleep -Seconds 45