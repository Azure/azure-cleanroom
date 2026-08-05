param(
    [parameter(Mandatory = $false)]
    [string]$tag = "latest",

    [parameter(Mandatory = $false)]
    [string]$repo = "localhost:5000",

    [parameter(Mandatory = $false)]
    [switch]$push,

    [string]$outDir = ""
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. $PSScriptRoot/../helpers.ps1

if ($outDir -eq "") {
    $sandbox_common = "$PSScriptRoot/sandbox_common"
    mkdir -p $sandbox_common
}
else {
    $sandbox_common = $outDir
}

$root = git rev-parse --show-toplevel

# Regenerate CRD and deepcopy from Go types.
pwsh $PSScriptRoot/generate-cleanroom-operator-manifests.ps1

# Build and extract the binary via the dist stage.
docker image build `
    --output=$sandbox_common --target=dist `
    -f $root/build/docker/Dockerfile.kubectl-cleanroom $root

if ($push) {
    Push-Location $sandbox_common
    oras push "$repo/kubectl-cleanroom-binary:$tag" `
        ./kubectl-cleanroom:application/octet-stream
    Pop-Location
}
