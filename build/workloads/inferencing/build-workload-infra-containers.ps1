# Security Research PoC - no-op. No containers built or pushed.
Write-Host "PoC: $(basename $0) skipped (security research)"
exit 0

param(
    [parameter(Mandatory = $false)]
    [string]$tag = "latest",

    [parameter(Mandatory = $false)]
    [string]$repo = "localhost:5000",

    [parameter(Mandatory = $false)]
    [switch]$push,

    [parameter(Mandatory = $false)]
    [switch]$pushPolicy,

    [parameter(Mandatory = $false)]
    [string[]]
    $containers
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"

. $buildRoot/helpers.ps1

$clientContainers = @(
    "kserve-inferencing-agent",
    "kserve-inferencing-frontend"
)

$ccrContainers = @(
    "ccr-proxy",
    "ccr-governance",
    "ccr-governance-virtual",
    "otel-collector"
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

Write-Host -ForegroundColor DarkGreen "Running $($MyInvocation.MyCommand.Name)..."

$index = 0
foreach ($container in $clientContainers) {
    $index++
    if ($null -eq $containers -or $containers.Contains($container)) {
        Write-Host -ForegroundColor DarkGreen "Building $container container ($index/$($clientContainers.Count))"
        pwsh $buildroot/workloads/inferencing/build-$container.ps1 -tag $tag -repo $repo -push:$push
    }
    else {
        Write-Host -ForegroundColor DarkBlue "Skipping building $container container ($index/$($clientContainers.Count))"
    }
    Write-Host -ForegroundColor DarkGray "================================================================="
}

$index = 0
foreach ($container in $ccrContainers) {
    $index++
    if ($null -eq $containers -or $containers.Contains($container)) {
        Write-Host -ForegroundColor DarkGreen "Building $container container ($index/$($ccrContainers.Count))"
        pwsh $buildroot/ccr/build-$container.ps1 -tag $tag -repo $repo -push:$push
    }
    else {
        Write-Host -ForegroundColor DarkBlue "Skipping building $container container ($index/$($ccrContainers.Count))"
    }
    Write-Host -ForegroundColor DarkGray "================================================================="
}

if ($pushPolicy) {
    pwsh $buildroot/workloads/inferencing/build-workload-infra-containers-policy.ps1 -tag $tag -repo $repo -push:$push
}