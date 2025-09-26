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
    $imageName = "$repo/workloads/cleanroom-spark-frontend:$tag"
}
else {
    $imageName = "workloads/cleanroom-spark-frontend:$tag"
}

. $PSScriptRoot/../helpers.ps1

$root = git rev-parse --show-toplevel

$buildRoot = "$root/build"

docker image build -t $imageName -f $buildRoot/docker/Dockerfile.cleanroom-spark-frontend "$root"

# Package the helm chart as well.
Push-Location $root/src/workloads/cleanroom-spark-frontend/helm/chart
$semanticVersion = Get-SemanticVersionFromTag $tag
Write-Host "Packaging helm chart with version $semanticVersion"
helm package . --version $semanticVersion .

$helmfile = Get-ChildItem -Path . -Filter "*.tgz" | Select-Object -First 1
$HELMFILE = $helmfile.FullName

# Rename the helm chart to cleanroom-spark-frontend.tgz
Write-Host "Renaming helmchart $HELMFILE to $sandbox_common/cleanroom-spark-frontend.tgz"

Move-Item -Path $HELMFILE -Destination "$sandbox_common/cleanroom-spark-frontend.tgz" -Force
Pop-Location

if ($push) {
    docker push $imageName

    $digest = Get-Digest -repo "$repo" -containerName "workloads/cleanroom-spark-frontend" -tag $tag
    $digestNoPrefix = $digest.Split(":")[1]

    @"
cleanroom_spark_frontend:
  version: $tag
  image: $repo/workloads/cleanroom-spark-frontend@$digest
  helm: $repo/workloads/helm/cleanroom-spark-frontend:$semanticVersion
"@ | Out-File "$sandbox_common/cleanroom-spark-frontend-versions.yaml"

    Push-Location $sandbox_common
    oras push "$repo/versions/workloads/cleanroom-spark-frontend:$digestNoPrefix,latest,$tag" ./cleanroom-spark-frontend-versions.yaml

    # The helm push picks up the version information from the chart itself.
    helm push ./cleanroom-spark-frontend.tgz "oci://$repo/workloads/helm"
    Pop-Location
}
