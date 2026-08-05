[CmdletBinding()]
param
(
    [int]$port = 8070,
    [switch]$keep
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel

# Build the OHTTP containers.
Write-Host "Building ohttp-gateway image..."
& $root/build/workloads/inferencing/build-ohttp-gateway.ps1
Write-Host "Building ohttp-client image..."
& $root/build/workloads/inferencing/build-ohttp-client.ps1

# Fix the version of dependencies in uv.
uv lock

# Run the tests in an isolated environment using uv.
$pythonArgs = @("--port", $port)
if ($keep) {
    $pythonArgs += @("--keep")
}

uv run --package test-ohttp --frozen --isolated python3 -u `
    $PSScriptRoot/test_ohttp.py @pythonArgs
