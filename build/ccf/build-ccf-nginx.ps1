param(
    [parameter(Mandatory = $false)]
    [string]$tag = "latest",

    [parameter(Mandatory = $false)]
    [string]$repo = "docker.io",

    [parameter(Mandatory = $false)]
    [switch]$push,

    [string]$outDir = ""
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
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

if ($repo) {
    $imageName = "$repo/ccf/ccf-nginx:$tag"
}
else {
    $imageName = "ccf/ccf-nginx:$tag"
}

. $PSScriptRoot/../helpers.ps1

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"

docker image build -t $imageName -f $buildRoot/docker/Dockerfile.ccf-nginx "$root"

if ($push) {
    docker push $imageName

    $digest = Get-Digest -repo "$repo" -containerName "ccf/ccf-nginx" -tag $tag
    $digestNoPrefix = $digest.Split(":")[1]

    @"
ccf-nginx:
  version: $tag
  image: $repo/ccf/ccf-nginx@$digest
"@ | Out-File "$sandbox_common/version.yaml"

    Push-Location $sandbox_common
    oras push "$repo/versions/ccf/ccf-nginx:$digestNoPrefix,latest" ./version.yaml
    Pop-Location
}