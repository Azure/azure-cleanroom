[CmdletBinding()]
param
(
    [switch]
    $NoBuild,

    [ValidateSet('mcr', 'local', 'acr')]
    [string]$registry = "local",

    [string]$repo = "localhost:5000",

    [string]$tag = "latest",

    [string]
    $ContractId = "collab1",

    [string]
    $OutDir = "$PSScriptRoot/generated",

    [switch]
    $useCsiDriver,

    [switch]
    $SkipCsiTests,

    [string]
    $SecondaryContractId = "",

    [switch]
    $useBlobfuseProxySidecar
)

# https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$outDir = $OutDir
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
$cgsEndpoint = $ccfEndpoint
$contractId = $ContractId
$serviceCertBase64 = Get-Content "$outDir/ccf/service_cert.pem" -Raw | base64 -w 0
az cleanroom governance client remove --name "ob-publisher-client"

$datastoreOutdir = "$outDir/datastores"

pwsh $PSScriptRoot/run-scenario-generate-template-policy.ps1 -registry $registry -repo $repo -tag $tag -ccfEndpoint $ccfEndpoint -outDir $outDir -datastoreOutDir $datastoreOutdir -contractId $contractId -useCsiDriver:$useCsiDriver -useBlobfuseProxySidecar:$useBlobfuseProxySidecar

$registry_local_endpoint = ""
if ($registry -eq "local") {
    $registry_local_endpoint = "ccr-registry:5000"
}

if ($useCsiDriver) {
    Write-Host "Deploying CSI driver DaemonSet..."
    pwsh $root/poc/csi-driver/deploy/onebox/deploy-csi-daemonset.ps1 `
        -repo $repo `
        -tag $tag `
        -cgsEndpoint $cgsEndpoint `
        -contractId $contractId `
        -serviceCertBase64 $serviceCertBase64
}

if ($useBlobfuseProxySidecar) {
    Write-Host "Using blobfuse-proxy sidecar (mount-all) mode — no DaemonSet needed."
}

pwsh $PSScriptRoot/../convert-template.ps1 -outDir $outDir -registry_local_endpoint $registry_local_endpoint -repo $repo -tag $tag

pwsh $PSScriptRoot/../deploy-virtual-cleanroom.ps1 -outDir $outDir -repo $repo -tag $tag

# Test concurrent-writer independence: a second writer pod targeting the same datasink
# must reach Running state with its own isolated blobfuse2 mount.
# These tests are CSI DaemonSet-specific and only run in --useCsiDriver mode.
if ($useCsiDriver -and -not $SkipCsiTests) {
    Write-Host "Running concurrent-writer independence test..."
    pwsh $PSScriptRoot/run-multi-writer-test.ps1

    Write-Host "Running multi-contract CSI test..."
    pwsh $PSScriptRoot/run-multi-contract-test.ps1 `
        -PrimaryContractId $contractId `
        -SecondaryContractId $SecondaryContractId

    Write-Host "Running CSI driver recovery test..."
    pwsh $PSScriptRoot/run-recovery-test.ps1
}

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
        --dst $resultsDir `
        --subdirectory output-data

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
if ($useCsiDriver -or $useBlobfuseProxySidecar) {
    # In CSI driver mode, blobfuse runs in the DaemonSet, so per-pod blobfuse
    # logs are not generated.
    # In blobfuse-proxy sidecar mode, there are no blobfuse-launcher sidecars,
    # so no blobfuse-launcher log files are produced either.
    # Only code-launcher and app logs are expected in both cases.
    $expectedFiles = @(
        "$outDir/results-decrypted/consumer-output/**/output.gz",
        "$resultsDir/application-telemetry*/demo-app.log",
        "$resultsDir/infrastructure-telemetry*/demo-app*-code-launcher.log",
        "$resultsDir/infrastructure-telemetry*/demo-app*-code-launcher.traces",
        "$resultsDir/infrastructure-telemetry*/demo-app*-code-launcher.metrics"
    )
}
else {
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
}

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