[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [ValidateSet("cgs-js-app", "cgs-constitution")]
    [string]
    $containerName,

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
    -containerName $containerName `
    -tag $tag


@"
${containerName}:
  version: $tag
  image: $repo/$containerName@$digest
"@ | Out-File ./$containerName-version.yml

oras push "$registryName.azurecr.io/$environment/azurecleanroom/versions/${containerName}:latest" ./$containerName-version.yml
