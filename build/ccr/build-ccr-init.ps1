param(
    [parameter(Mandatory = $false)]
    [string]$tag = "latest",

    [parameter(Mandatory = $false)]
    [string]$repo = "docker.io",

    [parameter(Mandatory = $false)]
    [switch]$push
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. $PSScriptRoot/../helpers.ps1

$env:DOCKER_BUILDKIT = 1

if ($repo) {
    $imageName = "$repo/ccr-init:$tag"
}
else {
    $imageName = "ccr-init:$tag"
}

$root = git rev-parse --show-toplevel
docker image build -t $imageName `
    -f $PSScriptRoot/../docker/Dockerfile.ccr-init $root
if ($push) {
    docker push $imageName
}
