param(
    [parameter(Mandatory = $false)]
    [string]$tag = "latest",

    [parameter(Mandatory = $false)]
    [string]$repo = "docker.io",

    [parameter(Mandatory = $false)]
    [switch]$push
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. $PSScriptRoot/../helpers.ps1

$clientContainers = @(
    "cgs-client",
    "cgs-ui"
)

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"
foreach ($container in $clientContainers) {
    Write-Host -ForegroundColor DarkGreen "Building $container container"
    pwsh $buildroot/cgs/build-$container.ps1 -tag $tag -repo $repo -push:$push
    Write-Host -ForegroundColor DarkGray "================================================================="
}