param(
    [parameter(Mandatory = $false)]
    [string]$tag = "latest",

    [parameter(Mandatory = $false)]
    [string]$repo = "localhost:5000",

    [parameter(Mandatory = $false)]
    [switch]$push,

    [parameter(Mandatory = $false)]
    [switch]$pushPolicy,

    [string]$outDir = ""
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"

. $buildRoot/helpers.ps1

if ($outDir -eq "") {
    $sandbox_common = "$PSScriptRoot/sandbox_common"
    if (-not (Test-Path $sandbox_common)) {
        mkdir -p $sandbox_common
    }
}
else {
    $sandbox_common = $outDir
}

if ($repo) {
    $imageName = "$repo/mock-server:$tag"
}
else {
    $imageName = "mock-server:$tag"
}

docker image build -t $imageName -f $buildRoot/docker/Dockerfile.mock_server "$root"

if ($push) {
    docker push $imageName
}
