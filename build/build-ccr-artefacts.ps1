param(
    [parameter(Mandatory = $false)]
    [string]$tag = "latest",

    [parameter(Mandatory = $false)]
    [string]$repo = "docker.io",

    [parameter(Mandatory = $false)]
    [switch]$push,

    [string] $outputPath = ""
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. $PSScriptRoot/helpers.ps1

$ccrArtefacts = @(
    "ccr-governance-opa-policy"
)

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"
$index = 0
foreach ($artefact in $ccrArtefacts) {
    Write-Host -ForegroundColor DarkGreen "Building $artefact container ($($index++)/$($ccrArtefacts.Count))"
    pwsh $buildRoot/ccr/build-$artefact.ps1 -tag $tag -repo $repo -push:$push -outputPath $outputPath
    Write-Host -ForegroundColor DarkGray "================================================================="
}