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
    "ccf-provider-client",
    "ccf-recovery-agent",
    "ccf-recovery-service",
    "ccf-runjs-app-virtual",
    "ccf-runjs-app-snp",
    "ccf-runjs-app-sandbox",
    "ccf-consortium-manager"
)

$ccrContainers = @(
    "ccr-proxy",
    "ccr-attestation",
    "skr"
)

$govClientContainers = @(
    "cgs-client",
    "cgs-ui"
)

$ccfArtefacts = @(
    "cgs-ccf-artefacts"
)

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"

Write-Host -ForegroundColor DarkGreen "Running $($MyInvocation.MyCommand.Name)..."

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

$index = 0
foreach ($container in $clientContainers) {
    $index++
    if ($null -eq $containers -or $containers.Contains($container)) {
        Write-Host -ForegroundColor DarkGreen "Building $container container ($index/$($clientContainers.Count))"
        pwsh $buildroot/ccf/build-$container.ps1 -tag $tag -repo $repo -push:$push
    }
    else {
        Write-Host -ForegroundColor DarkBlue "Skipping building $container container ($index/$($clientContainers.Count))"
    }
    Write-Host -ForegroundColor DarkGray "================================================================="
}

if ($pushPolicy) {
    pwsh $buildroot/ccf/build-ccf-infra-containers-policy.ps1 -tag $tag -repo $repo -push:$push
}

$index = 0
foreach ($container in $govClientContainers) {
    $index++
    if ($null -eq $containers -or $containers.Contains($container)) {
        Write-Host -ForegroundColor DarkGreen "Building $container container ($index/$($govClientContainers.Count))"
        pwsh $buildroot/cgs/build-$container.ps1 -tag $tag -repo $repo -push:$push
    }
    else {
        Write-Host -ForegroundColor DarkBlue "Skipping building $container container ($index/$($govClientContainers.Count))"
    }
    Write-Host -ForegroundColor DarkGray "================================================================="
}

$index = 0
foreach ($artefact in $ccfArtefacts) {
    $index++
    if ($null -eq $containers -or $containers.Contains($artefact)) {
        Write-Host -ForegroundColor DarkGreen "Building $artefact ($index/$($ccfArtefacts.Count))"
        pwsh $buildRoot/cgs/build-$artefact.ps1 -tag $tag -repo $repo -push:$push
    }
    else {
        Write-Host -ForegroundColor DarkBlue "Skipping building $artefact ($index/$($ccfArtefacts.Count))"
    }
    Write-Host -ForegroundColor DarkGray "================================================================="
}