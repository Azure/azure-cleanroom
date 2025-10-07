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

$outDir = "$PSScriptRoot/generated"
$datastoreOutdir = "$outDir/datastores"

$root = git rev-parse --show-toplevel

Write-Host "Using $usingRegistry registry for cleanroom container images."

function Get-UniqueString ([string]$id, $length = 13) {
    $hashArray = (new-object System.Security.Cryptography.SHA512Managed).ComputeHash($id.ToCharArray())
    -join ($hashArray[1..$length] | ForEach-Object { [char]($_ % 26 + [byte][char]'a') })
}

$CL_CLUSTER_RESOURCE_GROUP = ""
$resourceGroupTags = ""
if ($env:GITHUB_ACTIONS -eq "true") {
    # Need a short RG name for AKS cluster creation else MC resource group creation fails.
    $uniqueString = Get-UniqueString("cleanroom-cluster-${env:JOB_ID}-${env:RUN_ID}")
    $CL_CLUSTER_RESOURCE_GROUP = "rg-${uniqueString}"

    # Add run attempt in CCF RG so that re-run of the workflow creates a new CCF 
    # (as some actions on CCF cannot be repeated on re-reruns).
    $ISV_RESOURCE_GROUP = "cl-ob-isv-${env:JOB_ID}-${env:RUN_ID}-${env:RUN_ATTEMPT}"

    $resourceGroupTags = "github_actions=multi-party-collab-${env:JOB_ID}-${env:RUN_ID}"
}
else {
    $user = $env:CODESPACES -eq "true" ? $env:GITHUB_USER : $env:USER
    $ISV_RESOURCE_GROUP = "cl-ob-isv-ccf-${user}"
    $CL_CLUSTER_RESOURCE_GROUP = "cl-ob-isv-cl-${user}"
}

$CCF_NAME = "$(Get-UniqueString("${ISV_RESOURCE_GROUP}"))-ccf"
$CLUSTER_NAME = "$(Get-UniqueString("${CL_CLUSTER_RESOURCE_GROUP}"))-cluster"

$ISV_RESOURCE_GROUP_LOCATION = "westeurope"
Write-Host "Creating resource group $ISV_RESOURCE_GROUP in $ISV_RESOURCE_GROUP_LOCATION"
az group create --location $ISV_RESOURCE_GROUP_LOCATION --name $ISV_RESOURCE_GROUP --tags $resourceGroupTags

Write-Host "Creating resource group $CL_CLUSTER_RESOURCE_GROUP in $ISV_RESOURCE_GROUP_LOCATION"
az group create --location $ISV_RESOURCE_GROUP_LOCATION --name $CL_CLUSTER_RESOURCE_GROUP --tags $resourceGroupTags

$ccfProviderProjectName = "ob-big-data-analytics-ccf-provider"
pwsh $root/test/onebox/multi-party-collab/deploy-caci-cleanroom-governance.ps1 `
    -resourceGroup $ISV_RESOURCE_GROUP `
    -ccfName $CCF_NAME `
    -location $ISV_RESOURCE_GROUP_LOCATION `
    -NoBuild:$NoBuild `
    -registry $registry `
    -repo $repo `
    -tag $tag `
    -allowAll `
    -projectName "ob-cr-owner-client" `
    -initialMemberName "cr-owner" `
    -outDir $outDir `
    -ccfProviderProjectName $ccfProviderProjectName
$response = (az cleanroom ccf network show `
        --name $CCF_NAME `
        --provider-config $outDir/ccf/providerConfig.json `
        --provider-client $ccfProviderProjectName | ConvertFrom-Json)
$ccfEndpoint = $response.endpoint

pwsh $root/test/onebox/multi-party-collab/deploy-caci-cleanroom-cluster.ps1 `
    -resourceGroup $CL_CLUSTER_RESOURCE_GROUP `
    -clusterName $CLUSTER_NAME `
    -location $ISV_RESOURCE_GROUP_LOCATION `
    -NoBuild:$NoBuild `
    -registry $registry `
    -repo $repo `
    -tag $tag `
    -outDir $outDir `
    -clusterProviderProjectName "ob-big-data-analytics-cluster-provider"

$withSecurityPolicy = !$allowAll
$contractId = (New-Guid).ToString().Substring(0, 8)

pwsh $PSScriptRoot/run-scenario-generate-template-policy.ps1 `
    -registry $registryArg `
    -repo $repo `
    -tag $tag `
    -infraType "caci" `
    -ccfEndpoint $ccfEndpoint `
    -contractId $contractId `
    -outDir $outDir `
    -withSecurityPolicy:$withSecurityPolicy

mkdir -p $outDir/results
az cleanroom datastore download `
    --config $datastoreOutdir/big-data-query-consumer-datastore-config `
    --name consumer-output `
    --dst $outDir/results

pwsh $PSScriptRoot/get-telemetry.ps1 `
    -outDir $outDir

# Check that expected output files got created.
$expectedFiles = @(
    "$PSScriptRoot/generated/results/consumer-output/**/*.csv",
    "$PSScriptRoot/generated/telemetry/logs_cleanroom-spark-analytics-agent.json",
    "$PSScriptRoot/generated/telemetry/traces_cleanroom-spark-analytics-agent.json",
    "$PSScriptRoot/generated/telemetry/metrics_cleanroom-spark-frontend.json",
    "$PSScriptRoot/generated/telemetry/logs_cleanroom-spark-frontend.json",
    "$PSScriptRoot/generated/telemetry/traces_cleanroom-spark-frontend.json"
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

Write-Host "Verifying the contents of the query output"
# TODO Add for Blob output also later. Max tweets without range should be 28
# Import the CSV contents from S3 output
$firstFile = Get-ChildItem -Path "$outDir/s3queryOutput/$contractId/**/*" -Filter *.csv | `
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$data = Import-Csv $firstFile.FullName
# Find the maximum value in the "Number_Of_Mentions" column
$maxValue = ($data | Measure-Object -Property Number_Of_Mentions -Maximum).Maximum
if ($maxValue -ne 16) {
    Write-Host -ForegroundColor Red "Max tweets is not 16 but: "$maxValue
    exit 1
}

# TODO (gsinha): Add rest of the flow as things are implemented.