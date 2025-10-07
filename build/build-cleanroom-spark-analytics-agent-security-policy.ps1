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

. $PSScriptRoot/helpers.ps1

if ($outDir -eq "") {
    $root = git rev-parse --show-toplevel
    $outDir = $root + "/.policies/cleanroom-spark-analytics-agent-security-policy"
    if (-not (Test-Path $outDir)) {
        New-Item -ItemType Directory $outDir 
    }
}

$clContainers = @{
    "cleanroom-spark-analytics-agent" = "workloads/cleanroom-spark-analytics-agent"
    "ccr-proxy"                       = "ccr-proxy"
    "ccr-attestation"                 = "ccr-attestation"
    "ccr-governance"                  = "ccr-governance"
    "skr"                             = "skr"
    "otel-collector"                  = "otel-collector"
}

$templatesDir = "$PSScriptRoot/templates/cleanroom-spark-analytics-agent-policy"
$policyJson = Get-Content -Path "$templatesDir/cleanroom-spark-analytics-agent-policy.json"
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

Write-Output $policyJson | Out-File $outDir/cleanroom-spark-analytics-agent-security-policy.json
$policyJsons = Get-Content -Path $outDir/cleanroom-spark-analytics-agent-security-policy.json | ConvertFrom-Json
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
    --outraw `
| Out-File ${outDir}/cleanroom-spark-analytics-agent-security-policy.debug.rego

Write-Host "Generating CCE Policy"
az confcom acipolicygen `
    -i ${outDir}/ccepolicy-input.json `
    --outraw `
| Out-File ${outDir}/cleanroom-spark-analytics-agent-security-policy.rego

$regoPolicy = (Get-Content -Path ${outDir}/cleanroom-spark-analytics-agent-security-policy.rego -Raw).TrimEnd()
$regoPolicyDigest = $regoPolicy | sha256sum | cut -d ' ' -f 1
$debugRegoPolicy = (Get-Content -Path ${outDir}/cleanroom-spark-analytics-agent-security-policy.debug.rego -Raw).TrimEnd()
$debugRegoPolicyDigest = $debugRegoPolicy | sha256sum | cut -d ' ' -f 1
$policyJson = Get-Content -Path "$templatesDir/cleanroom-spark-analytics-agent-policy.json" | ConvertFrom-Json
$networkPolicy = [ordered]@{
    containers = $containers
    json       = $policyJson
    rego       = $regoPolicy
    rego_debug = $debugRegoPolicy
}

$policiesRepo = "$repo/policies/workloads"
$fileName = "cleanroom-spark-analytics-agent-security-policy.yaml"
($networkPolicy | ConvertTo-Yaml).TrimEnd() | Out-File $outDir/$fileName
if ($push) {
    Push-Location
    Set-Location $outDir
    oras push "$policiesRepo/cleanroom-spark-analytics-agent-security-policy:$tag,$regoPolicyDigest,$debugRegoPolicyDigest" ./$fileName
    Pop-Location
}