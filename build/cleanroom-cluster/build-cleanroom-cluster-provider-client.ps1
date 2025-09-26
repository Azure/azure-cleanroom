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
    $imageName = "$repo/cleanroom-cluster/cleanroom-cluster-provider-client:$tag"
}
else {
    $imageName = "cleanroom-cluster/cleanroom-cluster-provider-client:$tag"
}

. $PSScriptRoot/../helpers.ps1

$root = git rev-parse --show-toplevel
$external = Join-Path $root -ChildPath "/external"
git submodule update --init --recursive $external/virtualnodesOnAzureContainerInstances

$buildRoot = "$root/build"

docker image build -t $imageName -f $buildRoot/docker/Dockerfile.cleanroom-cluster-provider-client "$root"

if ($push) {
    docker push $imageName

    $digest = Get-Digest -repo "$repo" -containerName "cleanroom-cluster/cleanroom-cluster-provider-client" -tag $tag
    $digestNoPrefix = $digest.Split(":")[1]

    @"
cleanroom-cluster-provider-client:
  version: $tag
  image: $repo/cleanroom-cluster/cleanroom-cluster-provider-client@$digest
"@ | Out-File "$sandbox_common/version.yaml"

    Push-Location $sandbox_common
    oras push "$repo/versions/cleanroom-cluster/cleanroom-cluster-provider-client:$digestNoPrefix,latest" ./version.yaml
    Pop-Location
}