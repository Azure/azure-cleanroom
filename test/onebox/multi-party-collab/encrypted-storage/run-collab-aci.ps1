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

rm -rf $PSScriptRoot/generated

$root = git rev-parse --show-toplevel
. $root/test/onebox/multi-party-collab/deploy-aci-cleanroom-governance.ps1

Write-Host "Using $usingRegistry registry for cleanroom container images."

$outDir = "$PSScriptRoot/generated"
$datastoreOutdir = "$outDir/datastores"

$resourceGroupTags = ""
if ($env:GITHUB_ACTIONS -eq "true") {
    $ISV_RESOURCE_GROUP = "cl-ob-isv-${env:JOB_ID}-${env:RUN_ID}-${env:RUN_ATTEMPT}"
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
    -repo $repo `
    -tag $tag `
    -allowAll:$allowAll `
    -projectName "ob-consumer-client" `
    -initialMemberName "consumer" `
    -outDir $outDir

az cleanroom governance client remove --name "ob-publisher-client"

$withSecurityPolicy = !$allowAll
pwsh $PSScriptRoot/run-scenario-generate-template-policy.ps1 `
    -registry $registryArg `
    -repo $repo `
    -tag $tag `
    -ccfEndpoint $result.ccfEndpoint `
    -outDir $outDir `
    -datastoreOutDir $datastoreOutdir `
    -withSecurityPolicy:$withSecurityPolicy

if ($LASTEXITCODE -gt 0) {
    Write-Host -ForegroundColor Red "run-scenario-generate-template-policy returned non-zero exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

pwsh $PSScriptRoot/../deploy-caci-cleanroom.ps1 -resourceGroup $ISV_RESOURCE_GROUP -location $ISV_RESOURCE_GROUP_LOCATION -outDir $outDir

# The application is configured for auto-start. Hence, no need to issue the start API.
# curl -X POST -s http://ccr.cleanroom.local:8200/gov/demo-app/start --proxy http://127.0.0.1:10080

pwsh $PSScriptRoot/../wait-for-cleanroom.ps1 `
    -appName demo-app `
    -proxyUrl http://127.0.0.1:10080

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
az cleanroom datastore download `
    --config $datastoreOutdir/encrypted-storage-consumer-datastore-config `
    --name consumer-output `
    --dst $outDir/results

az cleanroom telemetry download `
    --cleanroom-config $outDir/configurations/publisher-config `
    --datastore-config $datastoreOutdir/encrypted-storage-publisher-datastore-config `
    --target-folder $outDir/results

az cleanroom logs download `
    --cleanroom-config $outDir/configurations/publisher-config `
    --datastore-config $datastoreOutdir/encrypted-storage-publisher-datastore-config `
    --target-folder $outDir/results

az cleanroom datastore decrypt `
    --config $datastoreOutdir/encrypted-storage-consumer-datastore-config `
    --name consumer-output `
    --source-path $outDir/results `
    --destination-path $outDir/results-decrypted

az cleanroom logs decrypt `
    --cleanroom-config $outDir/configurations/publisher-config `
    --datastore-config $datastoreOutdir/encrypted-storage-publisher-datastore-config `
    --source-path $outDir/results `
    --destination-path $outDir/results-decrypted
    
az cleanroom telemetry decrypt `
    --cleanroom-config $outDir/configurations/publisher-config `
    --datastore-config $datastoreOutdir/encrypted-storage-publisher-datastore-config `
    --source-path $outDir/results `
    --destination-path $outDir/results-decrypted

Write-Host "Application logs:"
cat $outDir/results-decrypted/application-telemetry*/**/demo-app.log

# Check that expected output files got created.
$expectedFiles = @(
    "$PSScriptRoot/generated/results-decrypted/consumer-output/**/output.gz",
    "$PSScriptRoot/generated/results-decrypted/application-telemetry*/**/demo-app.log",
    "$PSScriptRoot/generated/results-decrypted/infrastructure-telemetry*/**/application-telemetry*-blobfuse.log",
    "$PSScriptRoot/generated/results-decrypted/infrastructure-telemetry*/**/application-telemetry*-blobfuse-launcher.log",
    "$PSScriptRoot/generated/results-decrypted/infrastructure-telemetry*/**/application-telemetry*-blobfuse-launcher.traces",
    "$PSScriptRoot/generated/results-decrypted/infrastructure-telemetry*/**/demo-app*-code-launcher.log",
    "$PSScriptRoot/generated/results-decrypted/infrastructure-telemetry*/**/demo-app*-code-launcher.traces",
    "$PSScriptRoot/generated/results-decrypted/infrastructure-telemetry*/**/demo-app*-code-launcher.metrics",
    "$PSScriptRoot/generated/results-decrypted/infrastructure-telemetry*/**/consumer-output*-blobfuse.log",
    "$PSScriptRoot/generated/results-decrypted/infrastructure-telemetry*/**/consumer-output*-blobfuse-launcher.log",
    "$PSScriptRoot/generated/results-decrypted/infrastructure-telemetry*/**/consumer-output*-blobfuse-launcher.traces",
    "$PSScriptRoot/generated/results-decrypted/infrastructure-telemetry*/**/infrastructure-telemetry*-blobfuse.log",
    "$PSScriptRoot/generated/results-decrypted/infrastructure-telemetry*/**/infrastructure-telemetry*-blobfuse-launcher.log",
    "$PSScriptRoot/generated/results-decrypted/infrastructure-telemetry*/**/infrastructure-telemetry*-blobfuse-launcher.traces",
    "$PSScriptRoot/generated/results-decrypted/infrastructure-telemetry*/**/publisher-input*-blobfuse.log",
    "$PSScriptRoot/generated/results-decrypted/infrastructure-telemetry*/**/publisher-input*-blobfuse-launcher.log",
    "$PSScriptRoot/generated/results-decrypted/infrastructure-telemetry*/**/publisher-input*-blobfuse-launcher.traces"
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