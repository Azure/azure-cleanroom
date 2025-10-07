param(
    [parameter(Mandatory = $false)]
    [string]$tag = "latest",

    [parameter(Mandatory = $false)]
    [string]$repo = "docker.io",

    [parameter(Mandatory = $false)]
    [switch]$push,

    [parameter(Mandatory = $false)]
    [string]$digestFile = ""
)
. $PSScriptRoot/helpers.ps1

$ccrContainers = @(
    "blobfuse-launcher",
    "s3fs-launcher",
    "ccr-attestation",
    "ccr-governance",
    "ccr-init",
    "ccr-secrets",
    "ccr-proxy",
    "ccr-proxy-ext-processor",
    "code-launcher",
    "identity",
    "otel-collector",
    "skr"
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"
$digests = @()
$index = 0
foreach ($container in $ccrContainers) {
    Write-Host -ForegroundColor DarkGreen "Building $container container ($index/$($ccrContainers.Count))"
    pwsh $buildRoot/ccr/build-$container.ps1 -tag $tag -repo $repo -push:$push
    if ($digestFile -ne "") {
        $digest = docker inspect --format='{{index .RepoDigests 0}}' $repo/${container}:$tag
        $digest = $digest.Substring($digest.Length - 71, 71)
        $digests += @"
- image: $container
  digest: $digest
"@
    }

    Write-Host -ForegroundColor DarkGray "================================================================="
}

if ($digestFile -ne "") {
    Write-Output $digests | Out-File $digestFile
}
