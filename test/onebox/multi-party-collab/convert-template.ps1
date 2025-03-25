[CmdletBinding()]
param
(
    [string]
    $outDir = "$PSScriptRoot/generated",

    [string]
    $repo = "localhost:5000",

    [string]
    $tag = "latest",

    [string]
    $registry_local_endpoint = "ccr-registry:5000"
)


$root = git rev-parse --show-toplevel
Write-Host "Building aci-to-k8s image."
pwsh $root/build/onebox/build-aci-to-k8s.ps1

Write-Host "Converting template"
docker run --rm `
    -v ${outDir}/deployments:/workspace `
    -w /workspace `
    -u $(id -u $env:USER) `
    aci-to-k8s `
    --template-file ./cleanroom-arm-template.json `
    --registry-local-endpoint $registry_local_endpoint `
    --repo $repo `
    --tag $tag `
    --out-dir .
