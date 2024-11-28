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

$root = git rev-parse --show-toplevel
. $root/build/helpers.ps1

$repo = "$registryName.azurecr.io/$environment/cleanroom"
if ($environment -eq "unlisted") {
    $repo = "mcr.microsoft.com/cleanroom"
}

$digest = Get-Digest -repo "$registryName.azurecr.io/$environment/cleanroom" `
    -containerName "sidecar-digests" `
    -tag $tag

@"
ccr-containers:
  version: $tag
  image: $repo/sidecar-digests@$digest
"@ | Out-File "./ccr-containers-version.yml"

oras push "$registryName.azurecr.io/$environment/cleanroom/versions/ccr-containers:latest" ./"ccr-containers-version.yml"
CheckLastExitCode