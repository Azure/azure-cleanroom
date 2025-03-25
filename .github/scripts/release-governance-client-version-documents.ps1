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

foreach ($containerName in @("cgs-client", "cgs-ui")) {
    $digest = Get-Digest -repo "$registryName.azurecr.io/$environment/azurecleanroom" `
        -containerName $containerName `
        -tag $tag

    $digestNoPrefix = $digest.Split(":")[1]

    @"
${containerName}:
  version: $tag
  image: $repo/$containerName@$digest
"@ | Out-File ./$containerName-version.yml

    oras push "$registryName.azurecr.io/$environment/azurecleanroom/versions/${containerName}:$digestNoPrefix,latest" ./$containerName-version.yml
}

