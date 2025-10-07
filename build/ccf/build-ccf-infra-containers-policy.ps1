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
. $PSScriptRoot/../helpers.ps1

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"

$ccfContainers = @(
    "ccf-network",
    "ccf-recovery-service",
    "ccf-consortium-manager"
)

$index = 0
foreach ($container in $ccfContainers) {
    $index++
    if ($null -eq $containers -or $containers.Contains($container)) {
        Write-Host -ForegroundColor DarkGreen "Building $container security policy ($index/$($ccfContainers.Count))"
        pwsh $buildroot/build-$container-security-policy.ps1 -tag $tag -repo $repo -push:$push
    }
}