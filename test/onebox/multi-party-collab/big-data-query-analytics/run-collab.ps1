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
#rm -rf $outDir
Write-Host "Using $registry registry for cleanroom container images."

$root = git rev-parse --show-toplevel
pwsh $root/test/onebox/multi-party-collab/deploy-virtual-cleanroom-governance.ps1 `
    -NoBuild:$NoBuild `
    -registry $registry `
    -repo $repo `
    -tag $tag `
    -ccfProjectName "ob-big-data-analytics-ccf-provider" `
    -projectName "ob-cr-owner-client" `
    -initialMemberName "cr-owner" `
    -outDir $outDir
$ccfEndpoint = $(Get-Content $outDir/ccf/ccf.json | ConvertFrom-Json).endpoint

pwsh $root/test/onebox/multi-party-collab/deploy-virtual-cleanroom-cluster.ps1 `
    -NoBuild:$NoBuild `
    -registry $registry `
    -repo $repo `
    -tag $tag `
    -clusterProviderProjectName "ob-big-data-analytics-cluster-provider" `
    -clusterName "ob-big-data-analytics-cluster" `
    -outDir $outDir

$contractId = (New-Guid).ToString().Substring(0, 8)
pwsh $PSScriptRoot/run-scenario-generate-template-policy.ps1 `
    -registry $registry `
    -repo $repo `
    -tag $tag `
    -infraType "virtual" `
    -contractId $contractId `
    -outDir $outDir `
    -ccfEndpoint $ccfEndpoint

mkdir -p $outDir/results
az cleanroom datastore download `
    --config $datastoreOutdir/big-data-query-consumer-datastore-config `
    --name consumer-output `
    --dst $outDir/results

pwsh $PSScriptRoot/get-telemetry.ps1 `
    -outDir $outDir

# Check that expected output files got created.
$expectedFiles = @(
    "$PSScriptRoot/generated/results/consumer-output/**/*.csv"
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