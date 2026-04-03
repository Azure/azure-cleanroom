[CmdletBinding()]
param
(
    [ValidateSet('virtual', 'aks')]
    [string]$infraType = "virtual",

    [ValidateSet('mcr', 'local', 'acr')]
    [string]$registry = "local",

    [string]$repo = "localhost:5000",

    [string]$tag = "latest",

    [string]$outDir = "$PSScriptRoot/generated",

    [int]$maxWorkers = 2,

    [switch]
    $allowAll,

    [switch]
    $enableMonitoring,

    [string]$location = "centralindia"
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# Expand relative path to absolute path
if (-not [System.IO.Path]::IsPathRooted($outDir)) {
    $outDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $outDir))
}


python3 -u $PSScriptRoot/setup-env.py `
    --infra-type $infraType `
    --registry $registry `
    --repo $repo `
    --tag $tag `
    --out-dir $outDir `
    --no-build true `
    --allow-all $($allowAll ? "true" : "false") `
    --enable-monitoring $($enableMonitoring ? "true" : "false") `
    --max-workers $maxWorkers `
    --ccf-project-name ob-workload-ccf `
    --project-name ob-workload-owner-client `
    --initial-member-name workload-samples-owner `
    --cluster-provider-project-name ob-workload-cluster-provider `
    --cluster-name ob-workload-cluster `
    --ccf-provider-project-name ob-workload-ccf-provider `
    --location $location
