param(
    [parameter(Mandatory = $false)]
    [string]$tag = "latest",

    [parameter(Mandatory = $false)]
    [string]$repo = "docker.io",

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

. $PSScriptRoot/../helpers.ps1

$clientContainers = @(
    "cleanroom-cluster-provider-client"
)

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"

Write-Host -ForegroundColor DarkGreen "Running $($MyInvocation.MyCommand.Name)..."

$index = 0
foreach ($container in $clientContainers) {
    $index++
    if ($null -eq $containers -or $containers.Contains($container)) {
        Write-Host -ForegroundColor DarkGreen "Building $container container ($index/$($clientContainers.Count))"
        pwsh $buildroot/cleanroom-cluster/build-$container.ps1 -tag $tag -repo $repo -push:$push
    }
    else {
        Write-Host -ForegroundColor DarkBlue "Skipping building $container container ($index/$($clientContainers.Count))"
    }
    Write-Host -ForegroundColor DarkGray "================================================================="
}
