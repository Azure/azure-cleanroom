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
    $outDir = $root + "/.policies/cleanroom-spark-analytics-app-security-policy"
    if (-not (Test-Path $outDir)) {
        New-Item -ItemType Directory $outDir 
    }
}

$clContainer = "workloads/cleanroom-spark-analytics-app"

$templatesDir = "$PSScriptRoot/templates/cleanroom-spark-analytics-app"
$driverPolicyJson = Get-Content -Path "$templatesDir/driver-policy.json"
$digest = Get-Digest -repo $repo -containerName $clContainer -tag $tag
$driverPolicyJson = $driverPolicyJson.Replace("`$containerRegistryUrl/$clContainer@`$digest", "$repo/$clContainer@$digest")

$executorPolicyJson = Get-Content -Path "$templatesDir/executor-policy.json"
$executorPolicyJson = $executorPolicyJson.Replace("`$containerRegistryUrl/$clContainer@`$digest", "$repo/$clContainer@$digest")

$driverCcePolicyJson = [ordered]@{
    version    = "1.0"
    scenario   = "vn2"
    containers = (@($driverPolicyJson | ConvertFrom-Json))
}
$executorCcePolicyJson = [ordered]@{
    version    = "1.0"
    scenario   = "vn2"
    containers = (@($executorPolicyJson | ConvertFrom-Json))
}
$driverCcePolicyJson | ConvertTo-Json -Depth 100 | Out-File ${outDir}/spark-driver.json
$executorCcePolicyJson | ConvertTo-Json -Depth 100 | Out-File ${outDir}/spark-executor.json

Write-Host "Generating CCE Policy for spark-driver with image $clContainer"
az confcom acipolicygen `
    -i ${outDir}/spark-driver.json `
    --outraw  | Out-File ${outDir}/spark-driver-ccepolicy.rego

az confcom acipolicygen `
    -i ${outDir}/spark-driver.json `
    --debug-mode `
    --outraw  | Out-File ${outDir}/spark-driver-ccepolicy.debug.rego

Write-Host "Generating CCE Policy for spark-executor with image $clContainer"
az confcom acipolicygen `
    -i ${outDir}/spark-executor.json `
    --outraw  | Out-File ${outDir}/spark-executor-ccepolicy.rego

az confcom acipolicygen `
    -i ${outDir}/spark-executor.json `
    --debug-mode `
    --outraw  | Out-File ${outDir}/spark-executor-ccepolicy.debug.rego

$driverPolicy = Get-Container-Policy-From-Rego -regoFilePath ${outDir}/spark-driver-ccepolicy.rego -containerImage $repo/$clContainer@$digest
$executorPolicy = Get-Container-Policy-From-Rego -regoFilePath ${outDir}/spark-executor-ccepolicy.rego -containerImage $repo/$clContainer@$digest
$driverPolicyDebug = Get-Container-Policy-From-Rego -regoFilePath ${outDir}/spark-driver-ccepolicy.debug.rego -containerImage $repo/$clContainer@$digest
$executorPolicyDebug = Get-Container-Policy-From-Rego -regoFilePath ${outDir}/spark-executor-ccepolicy.debug.rego -containerImage $repo/$clContainer@$digest

$policy = [ordered]@{
    container = @{
        name   = "cleanroom-spark-analytics-app"
        image  = "@@RegistryUrl@@/$($clContainer)" # @@RegistryUrl@@ gets replaced at runtime with the value to use.
        digest = "$digest"
    }
    driver    = @{
        policy      = $driverPolicy
        policyDebug = $driverPolicyDebug

    }
    executor  = @{
        policy      = $executorPolicy
        policyDebug = $executorPolicyDebug
    }
}

$policiesRepo = "$repo/policies/workloads"
$fileName = "cleanroom-spark-analytics-app-security-policy.yaml"
($policy | ConvertTo-Yaml).TrimEnd() | Out-File $outDir/$fileName
if ($push) {
    Push-Location
    Set-Location $outDir
    oras push "$policiesRepo/cleanroom-spark-analytics-app-security-policy:$tag" ./$fileName
    Pop-Location
}