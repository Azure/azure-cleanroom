[CmdletBinding()]
param
(
    [switch]
    $NoBuild,

    [ValidateSet("acr", "mcr")]
    [string]$registry,

    [string]$repo = "",

    [string]$tag = "latest"
)

$registryArg
if ($repo -eq "" -and $registry -eq "acr") {
    throw "-repo must be specified for acr option."
}
if ($registry -eq "mcr") {
    $usingRegistry = "mcr"
    $registryArg = "mcr"
}
else {
    $registryArg = "local"
    if ($repo -eq "") {
        throw "-repo must be specified for acr option."
    }
    $usingRegistry = $repo
}

rm -rf $PSScriptRoot/generated

$root = git rev-parse --show-toplevel
. $root/test/onebox/multi-party-collab/deploy-aci-cleanroom-governance.ps1

Write-Host "Using $usingRegistry registry for cleanroom container images."

$resourceGroupTags = ""
if ($env:GITHUB_ACTIONS -eq "true") {
    $ISV_RESOURCE_GROUP = "cl-ob-triton-isv-${env:JOB_ID}-${env:RUN_ID}-${env:RUN_ATTEMPT}"
    $resourceGroupTags = "github_actions=multi-party-collab-${env:JOB_ID}-${env:RUN_ID}"
}
else {
    $ISV_RESOURCE_GROUP = "cl-ob-trition-isv-${env:USER}"
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

$outDir = "$PSScriptRoot/generated"
$result = Deploy-Aci-Governance `
    -resourceGroup $ISV_RESOURCE_GROUP `
    -ccfName $CCF_NAME `
    -location $ISV_RESOURCE_GROUP_LOCATION `
    -NoBuild:$NoBuild `
    -repo $repo `
    -tag $tag `
    -allowAll:$allowAll `
    -projectName "ob-consumer-client" `
    -initialMemberName "consumer" `
    -outDir $outDir
az cleanroom governance client remove --name "ob-publisher-client"

pwsh $PSScriptRoot/run-scenario-generate-template-policy.ps1 `
    -registry $registryArg `
    -repo $repo `
    -tag $tag `
    -ccfEndpoint $result.ccfEndpoint `
    -caci

if ($LASTEXITCODE -gt 0) {
    Write-Host -ForegroundColor Red "run-scenario-generate-template-policy returned non-zero exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

pwsh $root/build/ccr/build-ccr-client-proxy.ps1 

$failed = $false
pwsh $PSScriptRoot/deploy-caci-cleanroom.ps1 -resourceGroup $ISV_RESOURCE_GROUP -location $ISV_RESOURCE_GROUP_LOCATION
if ($LASTEXITCODE -gt 0) {
    $failed = $true
}

# Check that expected output files got created.
$expectedFiles = @(
    # TODO (gsinha): Add any output files to validate here once log files can be collected for running containers.
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

if ($failed) {
    Write-Host -ForegroundColor Red "deploy-caci-cleanroom returned non-zero exit code"
    exit 1
}