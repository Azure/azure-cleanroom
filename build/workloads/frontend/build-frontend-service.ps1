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
    $imageName = "$repo/frontend-service:$tag"
}
else {
    $imageName = "frontend-service:$tag"
}

docker image build -t $imageName -f $buildRoot/docker/Dockerfile.frontend-service "$root"

# Package the helm chart as well.
Push-Location $root/src/workloads/frontend/helmchart
$semanticVersion = Get-SemanticVersionFromTag $tag
Write-Host "Packaging helm chart with version $semanticVersion"
helm package . --version $semanticVersion .

$helmfile = Get-ChildItem -Path . -Filter "*.tgz" | Select-Object -First 1
$HELMFILE = $helmfile.FullName

# Rename the helm chart to cleanroom-spark-frontend.tgz
Write-Host "Renaming helmchart $HELMFILE to $sandbox_common/frontend-service.tgz"

Move-Item -Path $HELMFILE -Destination "$sandbox_common/frontend-service.tgz" -Force
Pop-Location

if ($push) {
    docker push $imageName

    $digest = Get-Digest -repo "$repo" -containerName "frontend-service" -tag $tag
    $digestNoPrefix = $digest.Split(":")[1]

    @"
frontend-service:
  version: $tag
  image: $repo/frontend-service@$digest
  helm: $repo/workloads/helm/frontend-service:$semanticVersion
"@ | Out-File "$sandbox_common/version.yaml"

    Push-Location $sandbox_common
    oras push "$repo/versions/frontend-service:$digestNoPrefix,latest" ./version.yaml

    # The helm push picks up the version information from the chart itself.
    helm push ./frontend-service.tgz "oci://$repo/workloads/helm"
    Pop-Location
}

if ($pushPolicy) {
    pwsh $buildRoot/workloads/frontend/build-frontend-service-security-policy.ps1 -tag $tag -repo $repo -push:$push
}

# Building mock server as part of frontend service build
pwsh $buildRoot/build-mock-server.ps1 -tag $tag -repo $repo -push:$push -outDir $outDir