[CmdletBinding()]
param
(
    [switch]
    $NoBuild,

    [ValidateSet("acr", "mcr")]
    [string]$registry,

    [string]$repo = "",

    [string]$tag = "latest",

    [switch]
    $allowAll
)

# https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

if ($repo -eq "" -and $registry -eq "acr") {
    throw "-repo must be specified for acr option."
}
if ($registry -eq "mcr") {
    $usingRegistry = "mcr"
    $registryArg = "mcr"
}
if ($registry -eq "acr") {
    $usingRegistry = $repo
    $registryArg = "acr"
}

rm -rf $PSScriptRoot/generated

$root = git rev-parse --show-toplevel

Write-Host "Using $usingRegistry registry for cleanroom container images."

$resourceGroupTags = ""
if ($env:GITHUB_ACTIONS -eq "true") {
    $ISV_RESOURCE_GROUP = "cl-ob-isv-${env:JOB_ID}-${env:RUN_ID}-${env:RUN_ATTEMPT}"
    $resourceGroupTags = "github_actions=multi-party-collab-${env:JOB_ID}-${env:RUN_ID}"
}
else {
    $user = $env:CODESPACES -eq "true" ? $env:GITHUB_USER : $env:USER
    $ISV_RESOURCE_GROUP = "cl-ob-isv-${user}"
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

$ccfProviderProjectName = "ob-query-analytics-ccf-provider"
$outDir = "$PSScriptRoot/generated"
pwsh $root/test/onebox/multi-party-collab/deploy-caci-cleanroom-governance.ps1 `
    -resourceGroup $ISV_RESOURCE_GROUP `
    -ccfName $CCF_NAME `
    -location $ISV_RESOURCE_GROUP_LOCATION `
    -NoBuild:$NoBuild `
    -registry $registry `
    -repo $repo `
    -tag $tag `
    -allowAll:$allowAll `
    -projectName "ob-consumer-client" `
    -initialMemberName "consumer" `
    -outDir $outDir `
    -ccfProviderProjectName $ccfProviderProjectName
$response = (az cleanroom ccf network show `
        --name $CCF_NAME `
        --provider-config $outDir/ccf/providerConfig.json `
        --provider-client $ccfProviderProjectName | ConvertFrom-Json)
$ccfEndpoint = $response.endpoint

az cleanroom governance client remove --name "ob-publisher-client"

$withSecurityPolicy = !$allowAll
pwsh $PSScriptRoot/run-scenario-generate-template-policy.ps1 `
    -registry $registryArg `
    -repo $repo `
    -tag $tag `
    -ccfEndpoint $ccfEndpoint `
    -withSecurityPolicy:$withSecurityPolicy

pwsh $PSScriptRoot/deploy-caci-cleanroom.ps1 -resourceGroup $ISV_RESOURCE_GROUP

Write-Host -ForegroundColor Green "Successfully deployed CACI cleanroom."