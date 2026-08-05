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
    $imageName = "$repo/workloads/ohttp-client:$tag"
}
else {
    $imageName = "workloads/ohttp-client:$tag"
}

$root = git rev-parse --show-toplevel
docker image build -t $imageName `
    -f $root/build/docker/Dockerfile.ohttp-client $root
if ($push) {
    docker push $imageName
}
