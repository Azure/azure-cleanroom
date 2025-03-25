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
    $imageName = "$repo/ccf/app/run-js/sandbox:$tag"
}
else {
    $imageName = "ccf/app/run-js/sandbox:$tag"
}

. $PSScriptRoot/../helpers.ps1

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"

docker image build -t $imageName --target sandbox -f $buildRoot/docker/Dockerfile.ccf_run_js_app.virtual "$root"

if ($push) {
    docker push $imageName

    $digest = Get-Digest -repo "$repo" -containerName "ccf/app/run-js/sandbox" -tag $tag
    $digestNoPrefix = $digest.Split(":")[1]

    @"
ccf/app/run-js/sandbox:
  version: $tag
  image: $repo/ccf/app/run-js/sandbox@$digest
"@ | Out-File "$sandbox_common/version.yaml"

    Push-Location $sandbox_common
    oras push "$repo/versions/ccf/app/run-js/sandbox:$digestNoPrefix,latest" ./version.yaml
    Pop-Location
}