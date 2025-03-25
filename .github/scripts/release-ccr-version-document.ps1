[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [string]
    $tag,

    [Parameter(Mandatory = $true)]
    [string]
    $environment,

    [Parameter(Mandatory = $true)]
    [string]
    $registryName
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
. $root/build/helpers.ps1

$repo = "$registryName.azurecr.io/$environment/azurecleanroom"
if ($environment -eq "unlisted") {
    $repo = "mcr.microsoft.com/azurecleanroom"
}

$digest = Get-Digest -repo "$registryName.azurecr.io/$environment/azurecleanroom" `
    -containerName "sidecar-digests" `
    -tag $tag

@"
ccr-containers:
  version: $tag
  image: $repo/sidecar-digests@$digest
"@ | Out-File "./ccr-containers-version.yml"

oras push "$registryName.azurecr.io/$environment/azurecleanroom/versions/ccr-containers:latest" ./"ccr-containers-version.yml"