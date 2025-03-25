[CmdletBinding()]
param
(
    [switch]
    $NoBuild,

    [ValidateSet('mcr', 'local', 'acr')]
    [string]$registry = "local",

    [string]$repo = "localhost:5000",

    [string]$tag = "latest"
)

$outDir = "$PSScriptRoot/generated"
rm -rf $outDir
Write-Host "Using $registry registry for cleanroom container images."
$root = git rev-parse --show-toplevel
$ccfOutDir = "$outDir/ccf"
$datastoreOutdir = "$outDir/datastores"
mkdir -p "$datastoreOutdir"
$samplePath = "$root/test/onebox/multi-party-collab"

if ($registry -ne "mcr") {
    $env:AZCLI_CLEANROOM_CONTAINER_REGISTRY_URL = "$repo"
    $env:AZCLI_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL = "$repo/sidecar-digests:$tag"
}

pwsh $root/src/tools/cleanroom-client/deploy-cleanroom-client.ps1 `
    -outDir $outDir `
    -datastoreOutDir $datastoreOutdir `
    -dataDir $samplePath

if ($LASTEXITCODE -gt 0) {
    Write-Host -ForegroundColor Red "deploy-cleanroom-client returned non-zero exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

pwsh $root/test/onebox/multi-party-collab/deploy-virtual-cleanroom-governance.ps1 `
    -NoBuild:$NoBuild `
    -registry $registry `
    -repo $repo `
    -tag $tag `
    -ccfProjectName "ob-ccf-encrypted-storage-cl-client" `
    -projectName "ob-consumer-client" `
    -initialMemberName "consumer" `
    -outDir $outDir
$ccfEndpoint = $(Get-Content $outDir/ccf/ccf.json | ConvertFrom-Json).endpoint
az cleanroom governance client remove --name "ob-publisher-client"

$cleanroomClientEndpoint = "localhost:8321"
pwsh $PSScriptRoot/run-scenario-generate-template-policy.ps1 `
    -registry $registry  `
    -repo $repo `
    -tag $tag `
    -ccfEndpoint $ccfEndpoint `
    -cleanroomClientEndpoint $cleanroomClientEndpoint `
    -ccfOutDir $ccfOutDir `
    -datastoreOutDir $datastoreOutdir

if ($LASTEXITCODE -gt 0) {
    Write-Host -ForegroundColor Red "run-scenario-generate-template-policy returned non-zero exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

$registry_local_endpoint = ""
if ($registry -eq "local") {
    $registry_local_endpoint = "ccr-registry:5000"
}

pwsh $root/test/onebox/multi-party-collab/convert-template.ps1 -outDir $outDir -registry_local_endpoint $registry_local_endpoint -repo $repo -tag $tag

pwsh $root/test/onebox/multi-party-collab/deploy-virtual-cleanroom.ps1 -outDir $outDir -repo $repo -tag $tag


Get-Job -Command "*kubectl port-forward ccr-client-proxy*" | Stop-Job
Get-Job -Command "*kubectl port-forward ccr-client-proxy*" | Remove-Job
kubectl port-forward ccr-client-proxy 10081:10080 &

# Need to wait a bit for the port-forward to start.
bash $root/src/scripts/wait-for-it.sh --timeout=20 --strict 127.0.0.1:10081 -- echo "ccr-client-proxy is available"
if ($LASTEXITCODE -gt 0) {
    Write-Host -ForegroundColor Red "Hit timeout waiting for ccr-client-proxy to be available. wait-for-it.sh returned returned non-zero exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# Run the application.
# curl -X POST -s http://ccr.cleanroom.local:8200/gov/demo-app/start --proxy http://127.0.0.1:10081

pwsh $root/test/onebox/multi-party-collab/wait-for-cleanroom.ps1 `
    -appName demo-app `
    -proxyUrl http://127.0.0.1:10081

Write-Host "Exporting logs..."
$response = curl -X POST -s http://ccr.cleanroom.local:8200/gov/exportLogs --proxy http://127.0.0.1:10081
$expectedResponse = '{"message":"Application telemetry data exported successfully."}'
if ($response -ne $expectedResponse) {
    Write-Host -ForegroundColor Red "Did not get expected response. Received: $response."
    exit 1
}

Write-Host "Exporting telemetry..."
$response = curl -X POST -s http://ccr.cleanroom.local:8200/gov/exportTelemetry --proxy http://127.0.0.1:10081
$expectedResponse = '{"message":"Infrastructure telemetry data exported successfully."}'
if ($response -ne $expectedResponse) {
    Write-Host -ForegroundColor Red "Did not get expected response. Received: $response."
    exit 1
}

curl --fail-with-body `
    -w "\n%{method} %{url} completed with %{response_code}\n" `
    -X POST $cleanroomClientEndpoint/datastore/download -H "content-type: application/json" -d @"
{
    "name" : "consumer-output",
    "configName": "$datastoreOutdir/encrypted-storage-cleanroom-client-consumer-datastore-config",
    "targetFolder": "$outDir/results"
}
"@
curl --fail-with-body `
    -w "\n%{method} %{url} completed with %{response_code}\n" `
    -X POST $cleanroomClientEndpoint/logs/download -H "content-type: application/json" -d @"
{
    "configName": "$outDir/configurations/publisher-config",
    "targetFolder": "$outDir/results",
    "datastoreConfigName": "$datastoreOutdir/encrypted-storage-cleanroom-client-publisher-datastore-config"
}
"@
curl --fail-with-body `
    -w "\n%{method} %{url} completed with %{response_code}\n" `
    -X POST $cleanroomClientEndpoint/telemetry/download -H "content-type: application/json" -d @"
{
    "configName": "$outDir/configurations/publisher-config",
    "targetFolder": "$outDir/results",
    "datastoreConfigName": "$datastoreOutdir/encrypted-storage-cleanroom-client-publisher-datastore-config"
}
"@

Write-Host "Application logs:"
cat $outDir/results/application-telemetry*/**/demo-app.log

# Check that expected output files got created.
$expectedFiles = @(
    "$PSScriptRoot/generated/results/consumer-output/**/output.gz",
    "$PSScriptRoot/generated/results/application-telemetry*/**/demo-app.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/application-telemetry*-blobfuse.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/application-telemetry*-blobfuse-launcher.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/application-telemetry*-blobfuse-launcher.traces",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/demo-app*-code-launcher.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/demo-app*-code-launcher.traces",
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