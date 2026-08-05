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
    $containers,

    [parameter(Mandatory = $false)]
    [string]
    $skipContainersFile
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. $PSScriptRoot/../helpers.ps1

$skipContainers = @()
if ($skipContainersFile -and (Test-Path $skipContainersFile)) {
    $skipContainers = @(Get-Content $skipContainersFile)
}

$clientContainers = @(
    "cleanroom-cluster-provider-client"
)

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"

Write-Host -ForegroundColor DarkGreen "Running $($MyInvocation.MyCommand.Name)..."

$index = 0
foreach ($container in $clientContainers) {
    $index++
    if ($null -ne $skipContainers -and $skipContainers.Contains($container)) {
        Write-Host -ForegroundColor DarkYellow "Skipping $container (already built) ($index/$($clientContainers.Count))"
    }
    elseif ($null -eq $containers -or $containers.Contains($container)) {
        Write-Host -ForegroundColor DarkGreen "Building $container container ($index/$($clientContainers.Count))"
        pwsh $buildroot/cleanroom-cluster/build-$container.ps1 -tag $tag -repo $repo -push:$push
    }
    else {
        Write-Host -ForegroundColor DarkBlue "Skipping building $container container ($index/$($clientContainers.Count))"
    }
    Write-Host -ForegroundColor DarkGray "================================================================="
}
