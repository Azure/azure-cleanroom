param(
    [parameter(Mandatory = $false)]
    [string]$tag = "latest",

    [parameter(Mandatory = $false)]
    [string]$repo = "localhost:5000",

    [parameter(Mandatory = $false)]
    [switch]$push
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. $PSScriptRoot/../helpers.ps1

if ($repo) {
    $imageName = "$repo/ccr-secrets:$tag"
}
else {
    $imageName = "ccr-secrets:$tag"
}

$root = git rev-parse --show-toplevel
Build-DockerImage `
    -t $imageName -f $PSScriptRoot/../docker/Dockerfile.ccr-secrets $root

if ($push) {
    docker push $imageName
}