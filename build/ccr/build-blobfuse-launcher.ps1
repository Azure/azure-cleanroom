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

if ($repo) {
    $imageName = "$repo/blobfuse-launcher:$tag"
}
else {
    $imageName = "blobfuse-launcher:$tag"
}

$root = git rev-parse --show-toplevel
$external = Join-Path $root -ChildPath "/external"
git submodule update --init --recursive $external/azure-storage-fuse

docker image build -t $imageName -f $PSScriptRoot/../docker/Dockerfile.blobfuse-launcher $root
if ($push) {
    docker push $imageName
}