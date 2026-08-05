# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
.SYNOPSIS
    Drives a TPC-DS stresstest end-to-end against a local AKS cluster.

.DESCRIPTION
    Two-stage workflow (separation of setup vs. workload):

      1. run-scenario.ps1   -> cleanroom setup (datastores, contract, CA,
                               deployment template/policy, cluster spec).
                               Mints a fresh contractId per run so re-runs
                               do not collide with an already-Accepted
                               contract. Writes $outDir/submitSqlJobConfig.json.
      2. submit_tpcds_job.py -> the actual workload. Reads submitSqlJobConfig.json
                               and submits queries. Can be re-run many times
                               against the same setup.

    Re-running this script with -skipSetup reuses the existing setup and
    only re-submits queries (cheap inner-loop iteration).

    submit_tpcds_job.py can also be invoked directly:
        python3 submit_tpcds_job.py --iterations 5 --parallel 10

.EXAMPLE
    # First run: full setup
    pwsh ./run-local.ps1

.EXAMPLE
    # Subsequent runs: reuse setup, just re-submit workload
    pwsh ./run-local.ps1 -skipSetup -iterations 10 -parallel 5
#>

[CmdletBinding()]
param(
    [int]$scaleFactor = 700,
    [int]$iterations = 2,
    [int]$parallel = 5,
    [string]$queryIds = "query1 query14 query24 query64 query72",
    [string]$dataFormats = "csv,parquet",
    [string]$deploymentConfigDir = "$PSScriptRoot/../../../../workloads/generated",
    [string]$outDir = "$PSScriptRoot/../../../../workloads/generated/tpcds-analytics",
    [switch]$skipSetup
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

if (-not $skipSetup) {
    $cfg = Join-Path $deploymentConfigDir "deployment-config.json"
    if (-not (Test-Path $cfg)) {
        throw "deployment-config.json not found at $cfg. Run setup-env.ps1 first."
    }
    $cfgJson = Get-Content $cfg | ConvertFrom-Json

    pwsh "$PSScriptRoot/run-scenario.ps1" `
        -ccfEndpoint $cfgJson.ccf_endpoint `
        -ownerClient $cfgJson.project_name `
        -ownerName $cfgJson.initial_member_name `
        -deploymentConfigDir $deploymentConfigDir `
        -outDir $outDir `
        -registry $cfgJson.registry `
        -repo $cfgJson.repo `
        -tag $cfgJson.tag `
        -scaleFactor $scaleFactor `
        -queryIds $queryIds `
        -dataFormats $dataFormats
}

# Wipe stale SparkApplications so old FAILED/COMPLETED rows don't skew metrics
# or trigger spurious failure detection in verify_run.py.
$clClusterFile = "$deploymentConfigDir/cl-cluster/cl-cluster.json"
if (-not (Test-Path $clClusterFile)) {
    throw "SparkApp cleanup: $clClusterFile not found. Run setup first."
}
$clCluster = Get-Content $clClusterFile | ConvertFrom-Json
$aksId = $clCluster.providerProperties.aksClusterId
$ns = $clCluster.analyticsWorkloadProfile.namespace
if (-not $aksId -or -not $ns) {
    throw "SparkApp cleanup: aksClusterId or analytics namespace missing from cl-cluster.json."
}
# /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.ContainerService/managedClusters/<name>
$aksRg = ($aksId -split '/')[4]
$aksName = ($aksId -split '/')[-1]
Write-Output "Cleaning SparkApplications in $aksName/$ns before run..."
az aks command invoke -g $aksRg -n $aksName `
    --command "kubectl delete sparkapplications --all -n $ns --wait=false" `
    -o tsv --query logs

python3 "$PSScriptRoot/submit_tpcds_job.py" `
    --query-ids ($queryIds -split '\s+') `
    --iterations $iterations `
    --parallel $parallel `
    --data-format $dataFormats
