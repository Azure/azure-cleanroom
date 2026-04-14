param(
    [parameter(Mandatory = $true)]
    [string]$tag,

    [parameter(Mandatory = $true)]
    [string]$repo,

    [string]$outDir = "",

    [switch]$push
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"

pwsh $buildroot/ccf/build-ccf-consortium-manager-security-policy-caci.ps1 -tag $tag -repo $repo -outDir $outDir -push:$push
pwsh $buildroot/ccf/build-ccf-consortium-manager-security-policy-vn2.ps1 -tag $tag -repo $repo -outDir $outDir -push:$push