[CmdletBinding()]
param
(
    [string]$deploymentConfigDir = "$PSScriptRoot/../../workloads/generated",
    [string]$outDir = "$PSScriptRoot/generated",
    [string]$location = "centralindia",
    
    [ValidateSet('json', 'parquet')]
    [string[]]$additionalFormats = @()
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# Expand relative path to absolute path
if (-not [System.IO.Path]::IsPathRooted($outDir)) {
    $outDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $outDir))
}

# Fix the version of dependencies in uv
uv lock

# Build additional formats args
$additionalFormatsArgs = @()
if ($additionalFormats.Count -gt 0) {
    $additionalFormatsArgs = @("--additional-formats") + $additionalFormats
}

# Run the scenario in an isolated environment using uv
uv run --package test-big-data-query-analytics --frozen --isolated python3 -u $PSScriptRoot/test-big-data-analytics.py `
    --deployment-config-dir $deploymentConfigDir `
    --location $location `
    --out-dir $outDir `
    $additionalFormatsArgs
