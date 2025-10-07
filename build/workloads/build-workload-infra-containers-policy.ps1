param(
    [parameter(Mandatory = $false)]
    [string]$tag = "latest",

    [parameter(Mandatory = $false)]
    [string]$repo = "docker.io",

    [parameter(Mandatory = $false)]
    [string[]]
    $containers,

    [parameter(Mandatory = $false)]
    [switch]$push
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. $PSScriptRoot/../helpers.ps1

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"

$clientContainers = @(
    "cleanroom-spark-analytics-agent"
    "cleanroom-spark-frontend"
    "cleanroom-spark-analytics-app"
)

$index = 0
foreach ($container in $clientContainers) {
    $index++
    if ($null -eq $containers -or $containers.Contains($container)) {
        Write-Host -ForegroundColor DarkGreen "Building $container security policy ($index/$($clientContainers.Count))"
        pwsh $buildroot/build-$container-security-policy.ps1 -tag $tag -repo $repo -push:$push
    }
}