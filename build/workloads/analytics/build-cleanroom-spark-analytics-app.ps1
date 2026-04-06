param(
    [parameter(Mandatory = $false)]
    [string]$tag = "latest",

    [parameter(Mandatory = $false)]
    [string]$repo = "localhost:5000",

    [parameter(Mandatory = $false)]
    [switch]$push,

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
    mkdir -p $sandbox_common
}
else {
    $sandbox_common = $outDir
}

if ($repo) {
    $imageName = "$repo/workloads/cleanroom-spark-analytics-app:$tag"
}
else {
    $imageName = "workloads/cleanroom-spark-analytics-app:$tag"
}

docker image build -t $imageName -f $buildRoot/docker/Dockerfile.cleanroom-spark-analytics-app "$root"

if ($push) {
    docker push $imageName

    $digest = Get-Digest -repo "$repo" -containerName "workloads/cleanroom-spark-analytics-app" -tag $tag
    $digestNoPrefix = $digest.Split(":")[1]

    $yamlContent = @"
version: $tag
image: $repo/workloads/cleanroom-spark-analytics-app@$digest
"@
    $yamlContent | Out-File "$sandbox_common/cleanroom-spark-analytics-app-versions.yaml" -NoNewline

    Push-Location $sandbox_common
    oras push "$repo/versions/workloads/cleanroom-spark-analytics-app:$digestNoPrefix,latest,$tag" ./cleanroom-spark-analytics-app-versions.yaml
    Pop-Location
}
