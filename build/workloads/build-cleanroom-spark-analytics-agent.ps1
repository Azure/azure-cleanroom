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
    $imageName = "$repo/workloads/cleanroom-spark-analytics-agent:$tag"
}
else {
    $imageName = "workloads/cleanroom-spark-analytics-agent:$tag"
}

. $PSScriptRoot/../helpers.ps1

$root = git rev-parse --show-toplevel
$external = Join-Path $root -ChildPath "/external"
git submodule update --init --recursive $external/virtualnodesOnAzureContainerInstances

$buildRoot = "$root/build"

docker image build -t $imageName -f $buildRoot/docker/Dockerfile.cleanroom-spark-analytics-agent "$root"

# Package the helm chart as well.
Push-Location $root/src/workloads/cleanroom-spark-analytics-agent/helm/chart
$semanticVersion = Get-SemanticVersionFromTag $tag
Write-Host "Packaging helm chart with version $semanticVersion"
helm package . --version $semanticVersion .

$helmfile = Get-ChildItem -Path . -Filter "*.tgz" | Select-Object -First 1
$HELMFILE = $helmfile.FullName

Write-Host "Moving helmchart $HELMFILE to $sandbox_common/cleanroom-spark-analytics-agent.tgz"

Move-Item -Path $HELMFILE -Destination "$sandbox_common/cleanroom-spark-analytics-agent.tgz" -Force
Pop-Location

if ($push) {
    docker push $imageName

    $digest = Get-Digest -repo "$repo" -containerName "workloads/cleanroom-spark-analytics-agent" -tag $tag
    $digestNoPrefix = $digest.Split(":")[1]

    @"
cleanroom-spark-analytics-agent:
  version: $tag
  image: $repo/workloads/cleanroom-spark-analytics-agent@$digest
  helm: $repo/workloads/helm/cleanroom-spark-analytics-agent:$semanticVersion
"@ | Out-File "$sandbox_common/version.yaml"

    Push-Location $sandbox_common
    oras push "$repo/versions/workloads/cleanroom-spark-analytics-agent:$digestNoPrefix,latest" ./version.yaml
    helm push ./cleanroom-spark-analytics-agent.tgz "oci://$repo/workloads/helm"
    Pop-Location
}