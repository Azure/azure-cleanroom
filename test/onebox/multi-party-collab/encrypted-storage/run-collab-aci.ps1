[CmdletBinding()]
param
(
    [switch]
    $NoBuild,

    [ValidateSet("acr", "mcr")]
    [string]$registry,

    [string]$registryUrl = "",

    [string]$tag = "latest",

    [switch]
    $allowAll
)

$registryArg
if ($registryUrl -eq "" -and $registry -eq "acr") {
    throw "-registryUrl must be specified for acr option."
}
if ($registry -eq "mcr") {
    $usingRegistry = "mcr"
    $registryArg = "mcr"
}
if ($registry -eq "acr") {
    $usingRegistry = $registryUrl
    $registryArg = "acr"
}

rm -rf $PSScriptRoot/generated

$root = git rev-parse --show-toplevel
. $root/test/onebox/multi-party-collab/deploy-aci-cleanroom-governance.ps1

Write-Host "Using $usingRegistry registry for cleanroom container images."

$resourceGroupTags = ""
if ($env:GITHUB_ACTIONS -eq "true") {
    $ISV_RESOURCE_GROUP = "cl-ob-isv-${env:JOB_ID}-${env:RUN_ID}"
    $resourceGroupTags = "github_actions=multi-party-collab-${env:JOB_ID}-${env:RUN_ID}"
}
else {
    $ISV_RESOURCE_GROUP = "cl-ob-isv-${env:USER}"
}

function Get-UniqueString ([string]$id, $length = 13) {
    $hashArray = (new-object System.Security.Cryptography.SHA512Managed).ComputeHash($id.ToCharArray())
    -join ($hashArray[1..$length] | ForEach-Object { [char]($_ % 26 + [byte][char]'a') })
}

$uniqueString = Get-UniqueString("${ISV_RESOURCE_GROUP}")
$CCF_NAME = "${uniqueString}-ccf"

$ISV_RESOURCE_GROUP_LOCATION = "westeurope"
Write-Host "Creating resource group $ISV_RESOURCE_GROUP in $ISV_RESOURCE_GROUP_LOCATION"
az group create --location $ISV_RESOURCE_GROUP_LOCATION --name $ISV_RESOURCE_GROUP --tags $resourceGroupTags

$result = Deploy-Aci-Governance `
    -resourceGroup $ISV_RESOURCE_GROUP `
    -location $ISV_RESOURCE_GROUP_LOCATION `
    -ccfName $CCF_NAME `
    -NoBuild:$NoBuild `
    -registry $registry `
    -registryUrl $registryUrl `
    -registryTag $tag `
    -allowAll:$allowAll `
    -projectName "ob-consumer-client" `
    -initialMemberName "consumer"
az cleanroom governance client remove --name "ob-publisher-client"

$withSecurityPolicy = !$allowAll
pwsh $PSScriptRoot/run-scenario-generate-template-policy.ps1 `
    -registry $registryArg `
    -registryUrl $registryUrl `
    -registryTag $tag `
    -ccfEndpoint $result.ccfEndpoint `
    -withSecurityPolicy:$withSecurityPolicy

if ($LASTEXITCODE -gt 0) {
    Write-Host -ForegroundColor Red "run-scenario-generate-template-policy returned non-zero exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

pwsh $PSScriptRoot/deploy-caci-cleanroom.ps1 -resourceGroup $ISV_RESOURCE_GROUP -location $ISV_RESOURCE_GROUP_LOCATION

# Check that expected output files got created.
$expectedFiles = @(
    "$PSScriptRoot/generated/results/consumer-output/**/output.gz",
    "$PSScriptRoot/generated/results/application-telemetry*/**/demo-app.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/application-telemetry*-blobfuse.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/application-telemetry*-blobfuse-launcher.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/application-telemetry*-blobfuse-launcher.traces",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/demo-app-code-launcher.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/consumer-output*-blobfuse.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/consumer-output*-blobfuse-launcher.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/consumer-output*-blobfuse-launcher.traces",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/infrastructure-telemetry*-blobfuse.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/infrastructure-telemetry*-blobfuse-launcher.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/infrastructure-telemetry*-blobfuse-launcher.traces",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/publisher-input*-blobfuse.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/publisher-input*-blobfuse-launcher.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/publisher-input*-blobfuse-launcher.traces"
)

$missingFiles = @()
foreach ($file in $expectedFiles) {
    if (!(Test-Path $file)) {
        $missingFiles += $file
    }
}

if ($missingFiles.Count -gt 0) {
    Write-Host -ForegroundColor Red "Did not find the following expected file(s). Check clean room logs for any failure(s):"
    foreach ($file in $missingFiles) {
        Write-Host -ForegroundColor Red $file
    }
    
    exit 1
}