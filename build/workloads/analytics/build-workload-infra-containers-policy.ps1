param(
    [parameter(Mandatory = $false)]
    [string]$tag = "latest",

    [parameter(Mandatory = $false)]
    [string]$repo = "localhost:5000",

    [parameter(Mandatory = $false)]
    [string[]]
    $containers,

    [parameter(Mandatory = $false)]
    [switch]$push
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"

. $buildRoot/helpers.ps1

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
        pwsh $buildroot/workloads/analytics/build-$container-security-policy.ps1 -tag $tag -repo $repo -push:$push
    }
}