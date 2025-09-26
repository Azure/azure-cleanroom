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
$datastoreOutdir = "$outDir/datastores"
rm -rf $outDir
Write-Host "Using $registry registry for cleanroom container images."
$root = git rev-parse --show-toplevel
pwsh $root/test/onebox/multi-party-collab/deploy-virtual-cleanroom-governance.ps1 `
    -NoBuild:$NoBuild `
    -registry $registry `
    -repo $repo `
    -tag $tag `
    -ccfProjectName "ob-ccf-quote-of-the-day" `
    -projectName "ob-consumer-client" `
    -initialMemberName "consumer" `
    -outDir $outDir
$ccfEndpoint = $(Get-Content $outDir/ccf/ccf.json | ConvertFrom-Json).endpoint

az cleanroom governance client remove --name "ob-publisher-client"

pwsh $PSScriptRoot/run-scenario-generate-template-policy.ps1 -registry $registry -repo $repo -tag $tag -ccfEndpoint $ccfEndpoint

pwsh $root/test/onebox/multi-party-collab/convert-template.ps1 -outDir $outDir -repo $repo -tag $tag

pwsh $root/test/onebox/multi-party-collab/deploy-virtual-cleanroom.ps1 -outDir $outDir -repo $repo -tag $tag

Get-Job -Command "*kubectl port-forward ccr-client-proxy*" | Stop-Job
Get-Job -Command "*kubectl port-forward ccr-client-proxy*" | Remove-Job
kubectl port-forward ccr-client-proxy 10081:10080 &

# Need to wait a bit for the port-forward to start.
bash $root/src/scripts/wait-for-it.sh --timeout=20 --strict 127.0.0.1:10081 -- echo "ccr-client-proxy is available"

$expectedResponse = '{"message":"Application started successfully."}'
$response = curl -X POST -s http://ccr.cleanroom.local:8200/gov/demo-app/start --proxy http://127.0.0.1:10081
if ($response -ne $expectedResponse) {
    Write-Host -ForegroundColor Red "Did not get expected response. Received: $response."
    exit 1
}

# wait for demo-app endpoint to be up.
$timeout = New-TimeSpan -Minutes 5
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
while ((curl -o /dev/null -w "%{http_code}" -s http://ccr.cleanroom.local:8080 --proxy http://127.0.0.1:10081) -ne "404") {
    Write-Host "Waiting for demo-app endpoint to be up at http://ccr.cleanroom.local:8080"
    Start-Sleep -Seconds 3
    if ($stopwatch.elapsed -gt $timeout) {
        throw "Hit timeout waiting for demo-app endpoint to be up."
    }
}

# We get a random quote of the format {"quote":"<quote>","author":"<author>"}. We will validate that we get a non-empty quote and author.
$responseJson = curl -s http://ccr.cleanroom.local:8080/get_random_quote --proxy http://127.0.0.1:10081
$response = $responseJson | ConvertFrom-Json
Write-Host "Received quote: $($response.quote) by $($response.author)"
if ($($response.quote) -eq "" -or $($response.author) -eq "") {
    Write-Host -ForegroundColor Red "Did not get expected response. Received: $responseJson."
    exit 1
}

$expectedResponse = '{"detail":"{\"code\":\"RequestNotAllowed\",\"message\":\"Failed ccr policy check: Requested API is not allowed\"}"}'
$response = curl -s http://ccr.cleanroom.local:8080/search_quotes/confucius --proxy http://127.0.0.1:10081 
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

mkdir -p $outDir/results
az cleanroom telemetry download `
    --cleanroom-config $outDir/configurations/publisher-config `
    --datastore-config $datastoreOutdir/quote-of-the-day-publisher-datastore-config `
    --target-folder $outDir/results

az cleanroom logs download `
    --cleanroom-config $outDir/configurations/publisher-config `
    --datastore-config $datastoreOutdir/quote-of-the-day-publisher-datastore-config `
    --target-folder $outDir/results

Write-Host "Application logs:"
cat $outDir/results/application-telemetry*/demo-app.log

# Check that expected output files got created.
$expectedFiles = @(
    "$PSScriptRoot/generated/results/application-telemetry*/demo-app.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/application-telemetry*-blobfuse.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/infrastructure-telemetry*-blobfuse-launcher.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/infrastructure-telemetry*-blobfuse-launcher.traces",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/demo-app*-code-launcher.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/demo-app*-code-launcher.traces",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/demo-app*-code-launcher.metrics",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/infrastructure-telemetry*-blobfuse.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/application-telemetry*-blobfuse-launcher.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/application-telemetry*-blobfuse-launcher.traces",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/identity.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/identity.traces",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/identity.metrics"
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