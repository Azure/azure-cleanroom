[CmdletBinding()]
param
(
    [string]$deploymentConfigDir = "$PSScriptRoot/../../workloads/generated",
    [string]$outDir = "$PSScriptRoot/generated",
    [int]$durationMinutes = 120,
    [int]$pauseMinutes = 30,
    [switch]$enableChaos
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# Expand relative path to absolute path
if (-not [System.IO.Path]::IsPathRooted($outDir)) {
    $outDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $outDir))
}

# Fix the version of dependencies in uv
uv lock

# Build chaos args
$chaosArgs = @()
if ($enableChaos) {
    $chaosArgs = @("--enable-chaos")
}

# Run the longhaul test in an isolated environment using uv
uv run --package test-big-data-query-analytics --frozen --isolated python3 -u `
    $PSScriptRoot/longhaul-test.py `
    --deployment-config-dir $deploymentConfigDir `
    --out-dir $outDir `
    --duration-minutes $durationMinutes `
    --pause-minutes $pauseMinutes `
    @chaosArgs
