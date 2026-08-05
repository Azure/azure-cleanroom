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

$root = git rev-parse --show-toplevel

. $root/build/helpers.ps1

if ($outDir -eq "") {
    $sandbox_common = "$PSScriptRoot/sandbox_common"
    mkdir -p $sandbox_common
}
else {
    $sandbox_common = $outDir
}

if ($repo) {
    $imageName = "$repo/karpenter-provider-accr:$tag"
}
else {
    $imageName = "karpenter-provider-accr:$tag"
}

docker image build -t $imageName `
    -f $root/build/docker/Dockerfile.karpenter-provider-accr $root

# Package the helm chart.
Push-Location $root/src/cleanroom-operator/helm/karpenter-provider-accr
$semanticVersion = Get-SemanticVersionFromTag $tag
Write-Host "Packaging karpenter-provider-accr helm chart with version $semanticVersion"
helm package . --version $semanticVersion .

$helmfile = Get-ChildItem -Path . -Filter "*.tgz" | Select-Object -First 1
$HELMFILE = $helmfile.FullName

Write-Host "Moving helmchart $HELMFILE to $sandbox_common/karpenter-provider-accr.tgz"

Move-Item -Path $HELMFILE -Destination "$sandbox_common/karpenter-provider-accr.tgz" -Force
Pop-Location

if ($push) {
    docker push $imageName

    Push-Location $sandbox_common
    helm push ./karpenter-provider-accr.tgz "oci://$repo/helm"
    Pop-Location
}
