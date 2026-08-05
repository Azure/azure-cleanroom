# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
.SYNOPSIS
    Path A: live-patch the cleanroom-spark-frontend resource config on an
    already-provisioned (private) AKS cluster, then restart the frontend so it
    re-reads the config. No image rebuild / re-provision.

.DESCRIPTION
    Resolves the AKS cluster from cl-cluster.json (same as run-local.ps1),
    fetches the live webserver-config ConfigMap, edits only
    settings.applications.analytics.sql.driver.memory and .executor.instances.max
    with a real YAML parser, applies the change via `az aks command invoke`,
    rolls the frontend, and verifies the new values are live.

.EXAMPLE
    pwsh ./apply-spark-resources.ps1

.EXAMPLE
    pwsh ./apply-spark-resources.ps1 -driverMemory 16g -executorInstancesMax 15
#>

[CmdletBinding()]
param(
    [string]$deploymentConfigDir = "$PSScriptRoot/../../../../workloads/generated",
    [string]$driverMemory = "16g",
    [string]$executorMemory = "16g",
    [int]$executorInstancesMax = 15,
    [string]$frontendNs = "cleanroom-spark-frontend",
    [string]$frontendDeploy = "cleanroom-spark-frontend"
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# --- Resolve AKS cluster (mirrors run-local.ps1) -----------------------------
$clClusterFile = "$deploymentConfigDir/cl-cluster/cl-cluster.json"
if (-not (Test-Path $clClusterFile)) {
    throw "$clClusterFile not found. Run setup first."
}
$aksId = (Get-Content $clClusterFile | ConvertFrom-Json).providerProperties.aksClusterId
if (-not $aksId) { throw "aksClusterId missing from cl-cluster.json." }
$aksRg = ($aksId -split '/')[4]
$aksName = ($aksId -split '/')[-1]

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) "spark-cfg-$(Get-Random)"
New-Item -ItemType Directory -Path $workDir -Force | Out-Null
try {
    # --- 1. Fetch live settings ---------------------------------------------
    Write-Output "Fetching webserver-config from $aksName/$frontendNs..."
    $settings = az aks command invoke -g $aksRg -n $aksName `
        --command "kubectl get configmap webserver-config -n $frontendNs -o jsonpath='{.data.settings}'" `
        -o tsv --query logs
    if (-not $settings) { throw "Empty settings returned; is the frontend deployed?" }
    $settingsPath = Join-Path $workDir "settings.yaml"
    # az returns logs as a string[] (one element per line); rejoin with
    # newlines so the YAML structure is preserved before parsing.
    Set-Content -Path $settingsPath -Value ($settings -join "`n") -NoNewline

    # --- 2. Edit only the two fields, emit a strategic-merge patch ----------
    $patchPath = Join-Path $workDir "patch.json"
    $py = @"
import json, yaml
with open(r'$settingsPath') as f:
    s = yaml.safe_load(f)
sql = s['applications']['analytics']['sql']
sql['driver']['memory'] = '$driverMemory'
sql['executor']['memory'] = '$executorMemory'
sql['executor']['instances']['max'] = '$executorInstancesMax'
new = yaml.safe_dump(s, default_flow_style=False, sort_keys=False)
json.dump({'data': {'settings': new}}, open(r'$patchPath', 'w'))
print('patched driver.memory=$driverMemory executor.memory=$executorMemory executor.instances.max=$executorInstancesMax')
"@
    $py | python3 -

    # --- 3. Apply patch (upload patch.json into the invoke context) ---------
    Write-Output "Applying ConfigMap patch..."
    az aks command invoke -g $aksRg -n $aksName `
        --command "kubectl patch configmap webserver-config -n $frontendNs --type merge --patch-file patch.json" `
        --file $patchPath `
        -o tsv --query logs

    # --- 4. Restart frontend & wait for rollout -----------------------------
    Write-Output "Restarting $frontendDeploy and waiting for rollout..."
    az aks command invoke -g $aksRg -n $aksName `
        --command "kubectl rollout restart deployment/$frontendDeploy -n $frontendNs; kubectl rollout status deployment/$frontendDeploy -n $frontendNs --timeout=180s" `
        -o tsv --query logs

    # --- 5. Verify the live values ------------------------------------------
    Write-Output "Verifying live values..."
    $verify = az aks command invoke -g $aksRg -n $aksName `
        --command "kubectl get configmap webserver-config -n $frontendNs -o jsonpath='{.data.settings}'" `
        -o tsv --query logs
    if (($verify -match "memory:\s*[`"']?$([regex]::Escape($driverMemory))\b") -and
        ($verify -match "memory:\s*[`"']?$([regex]::Escape($executorMemory))\b") -and
        ($verify -match "max:\s*[`"']?$executorInstancesMax\b")) {
        Write-Output "OK: driver.memory=$driverMemory, executor.memory=$executorMemory, executor.instances.max=$executorInstancesMax are live."
    }
    else {
        throw "Verification failed; expected values not found in live ConfigMap."
    }
}
finally {
    Remove-Item -Recurse -Force $workDir -ErrorAction SilentlyContinue
}
