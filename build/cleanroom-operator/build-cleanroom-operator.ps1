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
    $imageName = "$repo/cleanroom-operator:$tag"
}
else {
    $imageName = "cleanroom-operator:$tag"
}

$root = git rev-parse --show-toplevel

# Regenerate CRD and deepcopy from Go types.
pwsh $PSScriptRoot/generate-cleanroom-operator-manifests.ps1

docker image build -t $imageName `
    -f $root/build/docker/Dockerfile.cleanroom-operator $root

if ($push) {
    docker push $imageName
}
