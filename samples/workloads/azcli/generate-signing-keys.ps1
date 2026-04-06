# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
.SYNOPSIS
    Generate signing keys for pod policy verification using policy-signing-tool.sh.
.DESCRIPTION
    Uses policy-signing-tool.sh (openssl CLI) to generate an RSA-2048 key pair and
    self-signed certificate, then writes the configuration JSON consumed by
    other scripts.
.EXAMPLE
    ./generate-signing-keys.ps1 -outDir ./sandbox_common
#>
[CmdletBinding()]
param
(
    [string]
    $outDir = ""
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

if ($outDir -eq "") {
    $outDir = "$PSScriptRoot/sandbox_common"
    mkdir -p $outDir
}

$repoRoot = git rev-parse --show-toplevel
$signingTool = "$repoRoot/src/k8s-node/api-server-proxy/scripts/policy-signing-tool.sh"
$signingKeyDir = "$outDir/policy-signing-keys"

# Generate signing keys (idempotent — skips if keys already exist).
Write-Host "Generating signing keys using policy-signing-tool.sh..."
bash $signingTool --key-dir $signingKeyDir generate

# Get the certificate path from the tool.
$policySigningCertPath = $(bash $signingTool --key-dir $signingKeyDir cert)
if (-not (Test-Path $policySigningCertPath)) {
    Write-Error "Signing certificate not found at $policySigningCertPath"
    exit 1
}
Write-Host "Signing certificate: $policySigningCertPath"

# Output the configuration as JSON (matches the schema expected by other scripts).
$config = @{
    signingKeyDir         = $signingKeyDir
    policySigningCertPath = $policySigningCertPath
}

$configJson = $config | ConvertTo-Json -Depth 10
$configJson | Out-File "$outDir/signing-config.json" -Encoding utf8
Write-Host "Signing configuration written to: $outDir/signing-config.json"
