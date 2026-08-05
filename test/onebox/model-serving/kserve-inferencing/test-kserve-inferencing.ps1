[CmdletBinding()]
param
(
    [string]$deploymentConfigDir = "$PSScriptRoot/../../workloads/generated",
    [string]$outDir = "$PSScriptRoot/generated",
    [string]$location = "centralindia",
    [ValidateSet("iris", "tinyllama-gpu", "gemma4-gpu", "phi4-gpu", "tinyllama", "default")]
    [string]$models = "default",
    [string]$flexNodeVmSize = "",
    [switch]$provisionFlexNodeUsingBakedImage,
    [switch]$noDelete,
    [switch]$openGrafana
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# Expand relative path to absolute path
if (-not [System.IO.Path]::IsPathRooted($outDir)) {
    $outDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $outDir))
}

# Fix the version of dependencies in uv
uv lock

# Run the scenario in an isolated environment using uv
$pythonArgs = @(
    "--deployment-config-dir", $deploymentConfigDir,
    "--out-dir", $outDir,
    "--models", $models,
    "--location", $location
)

if ($flexNodeVmSize -ne "") {
    $pythonArgs += @("--flex-node-vm-size", $flexNodeVmSize)
}

if ($provisionFlexNodeUsingBakedImage) {
    $pythonArgs += "--provision-flex-node-using-baked-image"
}

if ($noDelete) {
    $pythonArgs += "--no-delete"
}

if ($openGrafana) {
    $pythonArgs += "--open-grafana"
}

uv run --package test-kserve-inferencing --frozen --isolated python3 -u $PSScriptRoot/test-kserve-inferencing.py @pythonArgs