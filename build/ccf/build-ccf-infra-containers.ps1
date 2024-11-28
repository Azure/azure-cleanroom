param(
    [parameter(Mandatory = $false)]
    [string]$tag = "latest",

    [parameter(Mandatory = $false)]
    [string]$repo = "docker.io",

    [parameter(Mandatory = $false)]
    [switch]$push,

    [parameter(Mandatory = $false)]
    [switch]$pushPolicy
)
. $PSScriptRoot/../helpers.ps1

$clientContainers = @(
    "ccf-provider-client",
    "ccf-recovery-agent",
    "ccf-recovery-service",
    "ccf-nginx",
    "ccf-runjs-app-virtual",
    "ccf-runjs-app-snp"
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

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"
$index = 0
foreach ($container in $ccrContainers) {
    $index++
    Write-Host -ForegroundColor DarkGreen "Building $container container ($index/$($ccrContainers.Count))"
    pwsh $buildroot/ccr/build-$container.ps1 -tag $tag -repo $repo -push:$push
    CheckLastExitCode
    Write-Host -ForegroundColor DarkGray "================================================================="
}

$index = 0
foreach ($container in $clientContainers) {
    $index++
    Write-Host -ForegroundColor DarkGreen "Building $container container ($index/$($clientContainers.Count))"
    pwsh $buildroot/ccf/build-$container.ps1 -tag $tag -repo $repo -push:$push
    CheckLastExitCode
    Write-Host -ForegroundColor DarkGray "================================================================="
}

if ($pushPolicy) {
    pwsh $buildroot/ccf/build-ccf-infra-containers-policy.ps1 -tag $tag -repo $repo -push:$push
}

$index = 0
foreach ($container in $govClientContainers) {
    $index++
    Write-Host -ForegroundColor DarkGreen "Building $container container ($index/$($govClientContainers.Count))"
    pwsh $buildroot/cgs/build-$container.ps1 -tag $tag -repo $repo -push:$push
    CheckLastExitCode
    Write-Host -ForegroundColor DarkGray "================================================================="
}

pwsh $buildRoot/cgs/build-cgs-ccf-artefacts.ps1 -tag $tag -repo $repo -push:$push