[CmdletBinding()]
param
(
    [string]$deploymentConfigDir = "$PSScriptRoot/../../workloads/generated",
    [string]$outDir = "$PSScriptRoot/generated",
    [string]$location = "centralindia",
    [string]$models = "all",
    [string]$flexNodeVmSize = ""
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
    "--models", $models
)

if ($flexNodeVmSize -ne "") {
    $pythonArgs += @("--flex-node-vm-size", $flexNodeVmSize)
}

uv run --package test-kserve-inferencing --frozen --isolated python3 -u $PSScriptRoot/test-kserve-inferencing.py @pythonArgs