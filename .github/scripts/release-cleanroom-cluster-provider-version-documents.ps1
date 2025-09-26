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
    -containerName "cleanroom-cluster/cleanroom-cluster-provider-client" `
    -tag $tag

@"
cleanroom-cluster-provider-client:
  version: $tag
  image: $repo/cleanroom-cluster/cleanroom-cluster-provider-client@$digest
"@ | Out-File "./cleanroom-cluster-provider-client-version.yaml"

oras push "$registryName.azurecr.io/$environment/azurecleanroom/versions/cleanroom-cluster/cleanroom-cluster-provider-client:latest" "./cleanroom-cluster-provider-client-version.yaml"