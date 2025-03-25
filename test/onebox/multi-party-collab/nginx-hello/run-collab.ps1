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
$datastoreOutdir = "$outDir/datastores"

$root = git rev-parse --show-toplevel
pwsh $root/test/onebox/multi-party-collab/deploy-virtual-cleanroom-governance.ps1 `
    -NoBuild:$NoBuild `
    -registry $registry `
    -repo $repo `
    -tag $tag `
    -ccfProjectName "ob-ccf-nginx-hello" `
    -projectName "ob-nginx-client" `
    -initialMemberName "nginx" `
    -outDir $outDir
$ccfEndpoint = $(Get-Content $outDir/ccf/ccf.json | ConvertFrom-Json).endpoint

$contractId = "collab1"
pwsh $PSScriptRoot/run-scenario-generate-template-policy.ps1 -registry $registry -repo $repo -tag $tag -outDir $outDir -ccfEndpoint $ccfEndpoint -datastoreOutDir $datastoreOutdir -contractId $contractId
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

$expectedResponse = '{"message":"Application started successfully."}'
$response = curl -X POST -s http://ccr.cleanroom.local:8200/gov/nginx-hello/start --proxy http://127.0.0.1:10081
if ($response -ne $expectedResponse) {
    Write-Host -ForegroundColor Red "Did not get expected response. Received: $response."
    exit 1
}

Write-Host "Started nginx-hello."

$expectedResponse = '{"status":"running","exit_code":0}'
$response = curl -s http://ccr.cleanroom.local:8200/gov/nginx-hello/status --proxy http://127.0.0.1:10081
if ($response -ne $expectedResponse) {
    Write-Host -ForegroundColor Red "Did not get expected response. Received: $response."
    exit 1
}

# wait for nginx-hello endpoint to be up.
$timeout = New-TimeSpan -Minutes 5
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
while ((curl -o /dev/null -w "%{http_code}" -s http://ccr.cleanroom.local:8080 --proxy http://127.0.0.1:10081) -ne "200") {
    Write-Host "Waiting for nginx-hello endpoint to be up at https://ccr.cleanroom.local:8080"
    Start-Sleep -Seconds 3
    if ($stopwatch.elapsed -gt $timeout) {
        throw "Hit timeout waiting for nginx-hello endpoint to be up."
    }
}

# The network policy only allows GET "/" path. Anything else is blocked.
# below request should pass
curl -s http://ccr.cleanroom.local:8080 --proxy http://127.0.0.1:10081

# below should fail
$expectedResponse = '{"code":"RequestNotAllowed","message":"Failed ccr policy check: Requested API is not allowed"}'
$response = curl -s http://ccr.cleanroom.local:8080/blah --proxy http://127.0.0.1:10081
if ($response -ne $expectedResponse) {
    Write-Host -ForegroundColor Red "Did not get expected response. Received: $response."
    exit 1
}

# below should fail
$expectedResponse = '{"code":"RequestNotAllowed","message":"Failed ccr policy check: Requested API is not allowed"}'
$response = curl -s http://ccr.cleanroom.local:8080/blah --proxy http://127.0.0.1:10081
if ($response -ne $expectedResponse) {
    Write-Host -ForegroundColor Red "Did not get expected response. Received: $response."
    exit 1
}

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

# Validate expected events were emitted to the ledger. Expected output format is:
# {"value":[{"seqno":76,"data":{"message":"Starting..."}}, ...]}"
Write-Host "Checking for expected events in the ledger..."
$events = az cleanroom governance contract event list `
    --contract-id $contractId `
    --all `
    --governance-client "ob-nginx-client"
$seqno = $events | jq '.value[] | select(.data.message == "Starting execution of nginx-hello container") | .seqno'
if ($null -eq $seqno) {
    Write-Host -ForegroundColor Red "Did not find expected event in the ledger."
    exit 1
}

# Try out disabling logging/telemetry consent and validate that the export APIs fail.
Write-Host "Disabling logging/telemetry export consent and validating export behavior."
az cleanroom governance contract runtime-option propose `
    --contract-id $contractId `
    --option telemetry `
    --action disable `
    --governance-client "ob-nginx-client"
az cleanroom governance contract runtime-option propose `
    --contract-id $contractId `
    --option logging `
    --action disable `
    --governance-client "ob-nginx-client"
$response = curl -X POST -s http://ccr.cleanroom.local:8200/gov/exportLogs --proxy http://127.0.0.1:10081
$expectedResponse = '{"code":"ConsentCheckFailed","message":"Consent status for logging is not enabled. Status is: ''disabled''."}'
if ($response -ne $expectedResponse) {
    Write-Host -ForegroundColor Red "Did not get expected response. Received: $response."
    exit 1
}

$response = curl -X POST -s http://ccr.cleanroom.local:8200/gov/exportTelemetry --proxy http://127.0.0.1:10081
$expectedResponse = '{"code":"ConsentCheckFailed","message":"Consent status for telemetry is not enabled. Status is: ''disabled''."}'
if ($response -ne $expectedResponse) {
    Write-Host -ForegroundColor Red "Did not get expected response. Received: $response."
    exit 1
}

# Try out disabling execution consent and validate that the start API fails.
az cleanroom governance contract runtime-option set `
    --contract-id $contractId `
    --option execution `
    --action disable `
    --governance-client "ob-nginx-client"
$response = curl -X POST -s http://ccr.cleanroom.local:8200/gov/nginx-hello/start --proxy http://127.0.0.1:10081
$expectedResponse = '{"code":"ConsentCheckFailed","message":"Consent status for execution is not enabled. Status is: ''disabled''."}'
if ($response -ne $expectedResponse) {
    Write-Host -ForegroundColor Red "Did not get expected response. Received: $response."
    exit 1
}

mkdir -p $outDir/results
az cleanroom telemetry download `
    --cleanroom-config $outDir/configurations/nginx-config `
    --datastore-config $datastoreOutdir/nginx-hello-nginx-datastore-config `
    --target-folder $outDir/results

az cleanroom logs download `
    --cleanroom-config $outDir/configurations/nginx-config `
    --datastore-config $datastoreOutdir/nginx-hello-nginx-datastore-config `
    --target-folder $outDir/results

# Check that expected output files got created.
$expectedFiles = @(
    "$PSScriptRoot/generated/results/application-telemetry*/**/nginx-hello.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/application-telemetry*-blobfuse.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/application-telemetry*-blobfuse-launcher.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/application-telemetry*-blobfuse-launcher.traces",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/nginx-hello*-code-launcher.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/nginx-hello*-code-launcher.traces",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/nginx-hello*-code-launcher.metrics",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/infrastructure-telemetry*-blobfuse.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/infrastructure-telemetry*-blobfuse-launcher.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/infrastructure-telemetry*-blobfuse-launcher.traces"
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