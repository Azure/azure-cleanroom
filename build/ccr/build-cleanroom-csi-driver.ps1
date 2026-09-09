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
    $imageName = "$repo/cleanroom-csi-driver:$tag"
}
else {
    $imageName = "cleanroom-csi-driver:$tag"
}

$root = git rev-parse --show-toplevel
$external = Join-Path $root -ChildPath "/external"
git submodule update --init --recursive $external/azure-storage-fuse
Build-DockerImage `
    -t $imageName -f $root/poc/csi-driver/Dockerfile $root
if ($push) {
    docker push $imageName
}
