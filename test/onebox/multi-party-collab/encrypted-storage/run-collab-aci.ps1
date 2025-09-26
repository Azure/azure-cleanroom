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

$registryArg
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

# https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

rm -rf $PSScriptRoot/generated

$root = git rev-parse --show-toplevel

Write-Host "Using $usingRegistry registry for cleanroom container images."

$outDir = "$PSScriptRoot/generated"
$datastoreOutdir = "$outDir/datastores"

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

$ccfProviderProjectName = "ob-encrypted-storage-ccf-provider"
pwsh $root/test/onebox/multi-party-collab/deploy-caci-cleanroom-governance.ps1 `
    -resourceGroup $ISV_RESOURCE_GROUP `
    -location $ISV_RESOURCE_GROUP_LOCATION `
    -ccfName $CCF_NAME `
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
    -outDir $outDir `
    -datastoreOutDir $datastoreOutdir `
    -withSecurityPolicy:$withSecurityPolicy

pwsh $PSScriptRoot/../deploy-caci-cleanroom.ps1 -resourceGroup $ISV_RESOURCE_GROUP -location $ISV_RESOURCE_GROUP_LOCATION -outDir $outDir

# The application is configured for auto-start. Hence, no need to issue the start API.
# curl -X POST -s http://ccr.cleanroom.local:8200/gov/demo-app/start --proxy http://127.0.0.1:10080

$script:waitForCleanRoomFailed = $false
$script:waitForCleanRoomExitCode = 0
& {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
    $PSNativeCommandUseErrorActionPreference = $false
    pwsh $PSScriptRoot/../wait-for-cleanroom.ps1 `
        -appName demo-app `
        -proxyUrl http://127.0.0.1:10080
    if ($LASTEXITCODE -gt 0) {
        $script:executionFailed = $true
        $script:waitForCleanRoomExitCode = $LASTEXITCODE
    }
}

# Wait for flush
Start-Sleep -Seconds 5
Write-Host "Exporting logs..."
$response = curl -X POST -s http://ccr.cleanroom.local:8200/gov/exportLogs --proxy http://127.0.0.1:10080
$expectedResponse = '{"message":"Application telemetry data exported successfully."}'
if ($response -ne $expectedResponse) {
    Write-Host -ForegroundColor Red "Did not get expected response. Received: $response."
    exit 1
}

Write-Host "Exporting telemetry..."
$response = curl -X POST -s http://ccr.cleanroom.local:8200/gov/exportTelemetry --proxy http://127.0.0.1:10080
$expectedResponse = '{"message":"Infrastructure telemetry data exported successfully."}'
if ($response -ne $expectedResponse) {
    Write-Host -ForegroundColor Red "Did not get expected response. Received: $response."
    exit 1
}

mkdir -p $outDir/results
$resultsDir = "$outDir/results"
az cleanroom datastore download `
    --config $datastoreOutdir/encrypted-storage-consumer-datastore-config `
    --name consumer-output `
    --dst $resultsDir

az cleanroom telemetry download `
    --cleanroom-config $outDir/configurations/publisher-config `
    --datastore-config $datastoreOutdir/encrypted-storage-publisher-datastore-config `
    --target-folder $resultsDir

az cleanroom logs download `
    --cleanroom-config $outDir/configurations/publisher-config `
    --datastore-config $datastoreOutdir/encrypted-storage-publisher-datastore-config `
    --target-folder $resultsDir

az cleanroom datastore decrypt `
    --config $datastoreOutdir/encrypted-storage-consumer-datastore-config `
    --name consumer-output `
    --source-path $resultsDir/consumer-output `
    --destination-path $outDir/results-decrypted

Write-Host "Application logs:"
cat $resultsDir/application-telemetry*/demo-app.log

# Check that expected output files got created.
$expectedFiles = @(
    "$outDir/results-decrypted/consumer-output/**/output.gz",
    "$resultsDir/application-telemetry*/demo-app.log",
    "$resultsDir/infrastructure-telemetry*/application-telemetry*-blobfuse.log",
    "$resultsDir/infrastructure-telemetry*/application-telemetry*-blobfuse-launcher.log",
    "$resultsDir/infrastructure-telemetry*/application-telemetry*-blobfuse-launcher.traces",
    "$resultsDir/infrastructure-telemetry*/demo-app*-code-launcher.log",
    "$resultsDir/infrastructure-telemetry*/demo-app*-code-launcher.traces",
    "$resultsDir/infrastructure-telemetry*/demo-app*-code-launcher.metrics",
    "$resultsDir/infrastructure-telemetry*/consumer-output*-blobfuse.log",
    "$resultsDir/infrastructure-telemetry*/consumer-output*-blobfuse-launcher.log",
    "$resultsDir/infrastructure-telemetry*/consumer-output*-blobfuse-launcher.traces",
    "$resultsDir/infrastructure-telemetry*/infrastructure-telemetry*-blobfuse.log",
    "$resultsDir/infrastructure-telemetry*/infrastructure-telemetry*-blobfuse-launcher.log",
    "$resultsDir/infrastructure-telemetry*/infrastructure-telemetry*-blobfuse-launcher.traces",
    "$resultsDir/infrastructure-telemetry*/publisher-input*-blobfuse.log",
    "$resultsDir/infrastructure-telemetry*/publisher-input*-blobfuse-launcher.log",
    "$resultsDir/infrastructure-telemetry*/publisher-input*-blobfuse-launcher.traces"
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

if ($script:waitForCleanRoomFailed) {
    Write-Host "waitforcleanroom.ps1 had exited with: $script:waitForCleanRoomExitCode"
    exit $script:waitForCleanRoomExitCode
}