param(
    [parameter(Mandatory = $true)]
    [string]$tag,

    [parameter(Mandatory = $true)]
    [string]$repo,

    [string]$outDir = "",

    [switch]$push
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"

. $buildRoot/helpers.ps1

if ($outDir -eq "") {
    $root = git rev-parse --show-toplevel
    $outDir = $root + "/.policies/kserve-inferencing-frontend-security-policy"
    if (-not (Test-Path $outDir)) {
        New-Item -ItemType Directory $outDir 
    }
}

$clContainers = @{
    "kserve-inferencing-frontend" = "workloads/kserve-inferencing-frontend"
    "ccr-proxy"                   = "ccr-proxy"
    "ccr-governance"              = "ccr-governance"
    "otel-collector"              = "otel-collector"
    "skr"                         = "skr"
}

$templatesDir = "$buildRoot/templates/kserve-inferencing-frontend-policy"
$policyJson = Get-Content -Path "$templatesDir/kserve-inferencing-frontend-policy.json"
$containers = @()
foreach ($container in $clContainers.GetEnumerator()) {
    $digest = Get-Digest -repo $repo -containerName $container.Value -tag $tag
    $policyJson = $policyJson.Replace("`$containerRegistryUrl/$($container.Value)@`$digest", "$repo/$($container.Value)@$digest")
    $containers += [ordered]@{
        name   = $container.Name
        image  = "@@RegistryUrl@@/$($container.Value)" # @@RegistryUrl@@ gets replaced at runtime with the value to use.
        digest = "$digest"
    }
}

Write-Output $policyJson | Out-File $outDir/kserve-inferencing-frontend-security-policy.json
$policyJsons = Get-Content -Path $outDir/kserve-inferencing-frontend-security-policy.json | ConvertFrom-Json
$ccePolicyJson = [ordered]@{
    version    = "1.0"
    scenario   = "vn2"
    containers = $policyJsons
}
$ccePolicyJson | ConvertTo-Json -Depth 100 | Out-File ${outDir}/ccepolicy-input.json

Write-Host "Generating CCE Policy with --debug-mode parameter"
az confcom acipolicygen `
    -i ${outDir}/ccepolicy-input.json `
    --debug-mode `
    --enable-stdio `
    --outraw `
| Out-File ${outDir}/kserve-inferencing-frontend-security-policy.debug.rego

Write-Host "Generating CCE Policy"
az confcom acipolicygen `
    -i ${outDir}/ccepolicy-input.json `
    --enable-stdio `
    --outraw `
| Out-File ${outDir}/kserve-inferencing-frontend-security-policy.rego

$regoPolicy = (Get-Content -Path ${outDir}/kserve-inferencing-frontend-security-policy.rego -Raw).TrimEnd()
$regoPolicyDigest = $regoPolicy | sha256sum | cut -d ' ' -f 1
$debugRegoPolicy = (Get-Content -Path ${outDir}/kserve-inferencing-frontend-security-policy.debug.rego -Raw).TrimEnd()
$debugRegoPolicyDigest = $debugRegoPolicy | sha256sum | cut -d ' ' -f 1
$policyJson = Get-Content -Path "$templatesDir/kserve-inferencing-frontend-policy.json" | ConvertFrom-Json
$networkPolicy = [ordered]@{
    containers = $containers
    json       = $policyJson
    rego       = $regoPolicy
    rego_debug = $debugRegoPolicy
}

$policiesRepo = "$repo/policies/workloads"
$fileName = "kserve-inferencing-frontend-security-policy.yaml"
($networkPolicy | ConvertTo-Yaml).TrimEnd() | Out-File $outDir/$fileName
if ($push) {
    Push-Location
    Set-Location $outDir
    oras push "$policiesRepo/kserve-inferencing-frontend-security-policy:$tag,$regoPolicyDigest,$debugRegoPolicyDigest" ./$fileName
    Pop-Location
}