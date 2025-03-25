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

$outDir = "$PSScriptRoot/generated"
$datastoreOutdir = "$outDir/datastores"

$root = git rev-parse --show-toplevel
. $root/test/onebox/multi-party-collab/deploy-aci-cleanroom-governance.ps1

Write-Host "Using $usingRegistry registry for cleanroom container images."

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

# Keeping this in westus while other samples run in westeurope to find out any issues if platform 
# changes underneath are rolling out in a staged manner and one datacenter sees it before the other.
$ISV_RESOURCE_GROUP_LOCATION = "westus"
Write-Host "Creating resource group $ISV_RESOURCE_GROUP in $ISV_RESOURCE_GROUP_LOCATION"
az group create --location $ISV_RESOURCE_GROUP_LOCATION --name $ISV_RESOURCE_GROUP --tags $resourceGroupTags

$result = Deploy-Aci-Governance `
    -resourceGroup $ISV_RESOURCE_GROUP `
    -ccfName $CCF_NAME `
    -location $ISV_RESOURCE_GROUP_LOCATION `
    -NoBuild:$NoBuild `
    -registry $registry `
    -repo $repo `
    -tag $tag `
    -allowAll:$allowAll `
    -projectName "ob-nginx-client" `
    -initialMemberName "nginx" `
    -outDir $outDir

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

pwsh $root/build/ccr/build-ccr-client-proxy.ps1 

$failed = $false
$dnsNameLabel = Get-UniqueString("cl-${ISV_RESOURCE_GROUP}")
pwsh $PSScriptRoot/../deploy-caci-cleanroom.ps1 -resourceGroup $ISV_RESOURCE_GROUP -location $ISV_RESOURCE_GROUP_LOCATION -dnsNameLabel $dnsNameLabel -outDir $outDir
if ($LASTEXITCODE -gt 0) {
    $failed = $true
}

curl -X POST -s http://ccr.cleanroom.local:8200/gov/nginx-hello/start --proxy http://127.0.0.1:10080

Write-Host "Started nginx-hello."

$expectedResponse = '{"status":"running","exit_code":0}'
$response = curl -s http://ccr.cleanroom.local:8200/gov/nginx-hello/status --proxy http://127.0.0.1:10080
if ($response -ne $expectedResponse) {
    Write-Host -ForegroundColor Red "Did not get expected response. Received: $response."
    exit 1
}

# wait for nginx-hello endpoint to be up.
$timeout = New-TimeSpan -Minutes 5
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
while ((curl -o /dev/null -w "%{http_code}" -s http://ccr.cleanroom.local:8080 --proxy http://127.0.0.1:10080) -ne "200") {
    Write-Host "Waiting for nginx-hello endpoint to be up at https://ccr.cleanroom.local:8080"
    Start-Sleep -Seconds 3
    if ($stopwatch.elapsed -gt $timeout) {
        throw "Hit timeout waiting for nginx-hello endpoint to be up."
    }
}

# The network policy only allows GET "/" path. Anything else is blocked.
# below request should pass
curl -s http://ccr.cleanroom.local:8080 --proxy http://127.0.0.1:10080

# below should fail
$expectedResponse = '{"code":"RequestNotAllowed","message":"Failed ccr policy check: Requested API is not allowed"}'
$response = curl -s http://ccr.cleanroom.local:8080/blah --proxy http://127.0.0.1:10080
if ($response -ne $expectedResponse) {
    Write-Host -ForegroundColor Red "Did not get expected response. Received: $response."
    exit 1
}

# below should fail
$expectedResponse = '{"code":"RequestNotAllowed","message":"Failed ccr policy check: Requested API is not allowed"}'
$response = curl -s http://ccr.cleanroom.local:8080/blah --proxy http://127.0.0.1:10080
if ($response -ne $expectedResponse) {
    Write-Host -ForegroundColor Red "Did not get expected response. Received: $response."
    exit 1
}

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

if ($failed) {
    Write-Host -ForegroundColor Red "deploy-caci-cleanroom returned non-zero exit code"
    exit 1
}