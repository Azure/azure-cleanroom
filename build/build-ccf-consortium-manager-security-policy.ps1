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
    $outDir = $root + "/.policies/ccf-consortium-manager-security-policy"
    if (-not (Test-Path $outDir)) {
        New-Item -ItemType Directory $outDir 
    }
}

$ccfContainers = @{
    "ccf-consortium-manager" = "ccf/ccf-consortium-manager"
    "ccr-proxy"              = "ccr-proxy"
    "skr"                    = "skr"
}

$templatesDir = "$PSScriptRoot/templates/ccf-consortium-manager-policy"
$policyJson = Get-Content -Path "$templatesDir/ccf-consortium-manager-policy.json"
$containers = @()
foreach ($container in $ccfContainers.GetEnumerator()) {
    $digest = Get-Digest -repo $repo -containerName $container.Value -tag $tag
    $policyJson = $policyJson.Replace("`$containerRegistryUrl/$($container.Value)@`$digest", "$repo/$($container.Value)@$digest")
    $containers += [ordered]@{
        name   = $container.Name
        image  = "@@RegistryUrl@@/$($container.Value)" # @@RegistryUrl@@ gets replaced at runtime with the value to use.
        digest = "$digest"
    }
}

Write-Output $policyJson | Out-File $outDir/ccf-consortium-manager-security-policy.json
$policyJsons = Get-Content -Path $outDir/ccf-consortium-manager-security-policy.json | ConvertFrom-Json
$ccePolicyJson = [ordered]@{
    version    = "1.0"
    containers = $policyJsons
}
$ccePolicyJson | ConvertTo-Json -Depth 100 | Out-File ${outDir}/ccepolicy-input.json

Write-Host "Generating CCE Policy with --debug-mode parameter"
az confcom acipolicygen `
    -i ${outDir}/ccepolicy-input.json `
    --debug-mode `
    --outraw `
| Out-File ${outDir}/ccf-consortium-manager-security-policy.debug.rego

Write-Host "Generating CCE Policy"
az confcom acipolicygen `
    -i ${outDir}/ccepolicy-input.json `
    --outraw `
| Out-File ${outDir}/ccf-consortium-manager-security-policy.rego

$regoPolicy = (Get-Content -Path ${outDir}/ccf-consortium-manager-security-policy.rego -Raw).TrimEnd()
$regoPolicyDigest = $regoPolicy | sha256sum | cut -d ' ' -f 1
$debugRegoPolicy = (Get-Content -Path ${outDir}/ccf-consortium-manager-security-policy.debug.rego -Raw).TrimEnd()
$debugRegoPolicyDigest = $debugRegoPolicy | sha256sum | cut -d ' ' -f 1
$policyJson = Get-Content -Path "$templatesDir/ccf-consortium-manager-policy.json" | ConvertFrom-Json
$networkPolicy = [ordered]@{
    containers = $containers
    json       = $policyJson
    rego       = $regoPolicy
    rego_debug = $debugRegoPolicy
}

$policiesRepo = "$repo/policies/ccf"
$fileName = "ccf-consortium-manager-security-policy.yaml"
($networkPolicy | ConvertTo-Yaml).TrimEnd() | Out-File $outDir/$fileName
if ($push) {
    Push-Location
    Set-Location $outDir
    oras push "$policiesRepo/ccf-consortium-manager-security-policy:$tag,$regoPolicyDigest,$debugRegoPolicyDigest" ./$fileName
    Pop-Location
}