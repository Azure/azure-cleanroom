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

# https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$outDir = "$PSScriptRoot/generated"
rm -rf $outDir
Write-Host "Using $registry registry for cleanroom container images."
$root = git rev-parse --show-toplevel
pwsh $root/test/onebox/multi-party-collab/deploy-virtual-cleanroom-governance.ps1 `
    -NoBuild:$NoBuild `
    -registry $registry `
    -repo $repo `
    -tag $tag `
    -ccfProjectName "ob-ccf-encrypted-storage" `
    -projectName "ob-consumer-client" `
    -initialMemberName "consumer" `
    -outDir $outDir
$ccfEndpoint = $(Get-Content $outDir/ccf/ccf.json | ConvertFrom-Json).endpoint
az cleanroom governance client remove --name "ob-publisher-client"

$datastoreOutdir = "$outDir/datastores"

pwsh $PSScriptRoot/run-scenario-generate-template-policy.ps1 -registry $registry -repo $repo -tag $tag -ccfEndpoint $ccfEndpoint -outDir $outDir -datastoreOutDir $datastoreOutdir

$registry_local_endpoint = ""
if ($registry -eq "local") {
    $registry_local_endpoint = "ccr-registry:5000"
}

pwsh $PSScriptRoot/../convert-template.ps1 -outDir $outDir -registry_local_endpoint $registry_local_endpoint -repo $repo -tag $tag

pwsh $PSScriptRoot/../deploy-virtual-cleanroom.ps1 -outDir $outDir -repo $repo -tag $tag

Get-Job -Command "*kubectl port-forward ccr-client-proxy*" | Stop-Job
Get-Job -Command "*kubectl port-forward ccr-client-proxy*" | Remove-Job
kubectl port-forward ccr-client-proxy 10081:10080 &

# Need to wait a bit for the port-forward to start.
bash $root/src/scripts/wait-for-it.sh --timeout=20 --strict 127.0.0.1:10081 -- echo "ccr-client-proxy is available"

# The application is configured for auto-start. Hence, no need to issue the start API.
# curl -X POST -s http://ccr.cleanroom.local:8200/gov/demo-app/start --proxy http://127.0.0.1:10081

$script:waitForCleanRoomFailed = $false
$script:waitForCleanRoomExitCode = 0
& {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
    $PSNativeCommandUseErrorActionPreference = $false
    pwsh $PSScriptRoot/../wait-for-cleanroom.ps1 `
        -appName demo-app `
        -proxyUrl http://127.0.0.1:10081
    if ($LASTEXITCODE -gt 0) {
        $script:executionFailed = $true
        $script:waitForCleanRoomExitCode = $LASTEXITCODE
    }
}

# Wait for flush
Start-Sleep -Seconds 5
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
if (!$skiplogs) {
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
}
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