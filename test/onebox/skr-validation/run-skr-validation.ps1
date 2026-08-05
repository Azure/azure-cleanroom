# SKR (Secure Key Release) Validation Test
#
# Validates that CACI's SKR mechanism works by deploying a minimal container group
# with the SKR sidecar and a test client that calls POST /key/release.
# No CCF, CGS, governance, or cleanroom infrastructure needed.
#
# The CCE policy is generated during one-time setup (setup-resources.ps1) and
# passed as a parameter to the ARM template at deploy time.
#
# Prerequisites:
#   - Run setup-resources.ps1 first (one-time)
#
# Usage:
#   pwsh run-skr-validation.ps1
#   pwsh run-skr-validation.ps1 -location westeurope

[CmdletBinding()]
param
(
    [string]$resourceGroup = "cl-skr-validation-rg",

    [string]$location = "uksouth",

    [string]$outDir = "$PSScriptRoot/generated"
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
Import-Module $root/samples/common/infra-scripts/azure-helpers.psm1 -Force -DisableNameChecking

# Load resources from setup.
$resourcesFile = "$outDir/$resourceGroup/resources.json"
if (-not (Test-Path $resourcesFile)) {
    Write-Host "Resources file not found. Running setup-resources.ps1 first..."
    pwsh $PSScriptRoot/setup-resources.ps1 -resourceGroup $resourceGroup -location $location -outDir $outDir
}
$resources = Get-Content $resourcesFile | ConvertFrom-Json

$kvName = $resources.kvName
$kvEndpoint = $resources.kvEndpoint
$miId = $resources.miId
$maaEndpoint = $resources.maaEndpoint
$keyName = $resources.keyName
$ccePolicy = $resources.ccePolicy
# Handle case where ccePolicy was stored as an array (PowerShell line-by-line output capture).
if ($ccePolicy -is [array]) {
    $ccePolicy = ($ccePolicy | Where-Object { $_ }) -join ''
}
$ccePolicy = $ccePolicy.Trim()

Write-Host "Using KV: $kvName | MI: $($resources.miName) | MAA: $maaEndpoint | Key: $keyName"

if ([string]::IsNullOrEmpty($ccePolicy)) {
    throw "ccePolicy not found in resources.json. Re-run setup-resources.ps1."
}

# Step 1: Deploy CACI container group with the generated CCE policy.
$templateFile = "$PSScriptRoot/aci-arm-template.json"
$containerGroupName = "skr-validation-test"
Write-Host "Deploying CACI container group '$containerGroupName'..."

# Delete existing container group if present.
& {
    $PSNativeCommandUseErrorActionPreference = $false
    az container delete --name $containerGroupName --resource-group $resourceGroup --yes 2>$null
}

az deployment group create `
    --resource-group $resourceGroup `
    --template-file $templateFile `
    --parameters `
        containerGroupName=$containerGroupName `
        location=$location `
        managedIdentityResourceId=$miId `
        maaEndpoint=$maaEndpoint `
        akvEndpoint=$kvEndpoint `
        keyName=$keyName `
        ccePolicy=$ccePolicy

Write-Host "CACI container group deployed."

# Step 2: Wait for the test client to produce a key release result.
# The skr_client.sh runs in a loop, so we detect success from logs rather than
# waiting for container termination.
Write-Host "Waiting for SKR key release result..."
$timeout = New-TimeSpan -Minutes 10
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$succeeded = $false

while ($true) {
    if ($stopwatch.elapsed -gt $timeout) {
        Write-Host -ForegroundColor Red "Timeout waiting for SKR key release result."
        Write-Host "Container group state:"
        az container show --name $containerGroupName --resource-group $resourceGroup --query "containers[].{name:name, state:instanceView.currentState}" -o table
        Write-Host "SKR sidecar logs:"
        az container logs --name $containerGroupName --resource-group $resourceGroup --container-name skr-sidecar
        Write-Host "Test client logs:"
        az container logs --name $containerGroupName --resource-group $resourceGroup --container-name skr-test-client
        exit 1
    }

    $logs = az container logs `
        --name $containerGroupName `
        --resource-group $resourceGroup `
        --container-name skr-test-client 2>$null

    # The test client outputs {"key":"..."} when a key is successfully released.
    if ($logs -match '"key"') {
        $succeeded = $true
        break
    }

    # Check if container terminated with an error.
    $state = az container show `
        --name $containerGroupName `
        --resource-group $resourceGroup `
        --query "containers[?name=='skr-test-client'].instanceView.currentState.state" `
        --output tsv

    if ($state -eq "Terminated") {
        break
    }

    Write-Host "Waiting for key release... ($([int]$stopwatch.elapsed.TotalSeconds)s)"
    Start-Sleep -Seconds 10
}

# Step 3: Report results.
if ($succeeded) {
    Write-Host -ForegroundColor Green "SKR VALIDATION PASSED: Key release succeeded."
    Write-Host -ForegroundColor Green "CACI attestation and Secure Key Release are working correctly."
}
else {
    Write-Host -ForegroundColor Red "SKR VALIDATION FAILED: Key release failed."
    Write-Host "SKR sidecar logs:"
    az container logs --name $containerGroupName --resource-group $resourceGroup --container-name skr-sidecar
    Write-Host "Test client logs:"
    az container logs --name $containerGroupName --resource-group $resourceGroup --container-name skr-test-client
    exit 1
}
