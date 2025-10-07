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

if ($repo) {
    $imageName = "$repo/local-idp:$tag"
}
else {
    $imageName = "local-idp:$tag"
}

. $PSScriptRoot/../helpers.ps1

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"

docker image build `
    -t $imageName `
    -f $buildRoot/docker/Dockerfile.local-idp "$root/src/tools/local-idp"

# Extract the open-api spec.
docker image build `
    --output="$root/src/tools/local-idp/app/schema" `
    --target=openapi-dist `
    -f $buildRoot/docker/Dockerfile.local-idp "$root/src/tools/local-idp"

if ($push) {
    docker push $imageName
}