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

. $PSScriptRoot/../../helpers.ps1

if ($repo) {
    $imageName = "$repo/workloads/ohttp-gateway:$tag"
}
else {
    $imageName = "workloads/ohttp-gateway:$tag"
}

$root = git rev-parse --show-toplevel
Build-DockerImage `
    -t $imageName `
    -f $root/build/docker/Dockerfile.ohttp-gateway $root
if ($push) {
    docker push $imageName
}
