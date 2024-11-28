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

$root = git rev-parse --show-toplevel
. $root/build/helpers.ps1

$repo = "$registryName.azurecr.io/$environment/cleanroom"
if ($environment -eq "unlisted") {
    $repo = "mcr.microsoft.com/cleanroom"
}

$digest = Get-Digest -repo "$registryName.azurecr.io/$environment/cleanroom" `
    -containerName $containerName `
    -tag $tag


@"
${containerName}:
  version: $tag
  image: $repo/$containerName@$digest
"@ | Out-File ./$containerName-version.yml

oras push "$registryName.azurecr.io/$environment/cleanroom/versions/${containerName}:latest" ./$containerName-version.yml
CheckLastExitCode