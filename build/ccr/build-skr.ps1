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
    $imageName = "$repo/skr:$tag"
}
else {
    $imageName = "skr:$tag"
}

$root = git rev-parse --show-toplevel

docker build -t $imageName -f $PSScriptRoot/../docker/Dockerfile.skr $root
if ($push) {
    docker push $imageName
}