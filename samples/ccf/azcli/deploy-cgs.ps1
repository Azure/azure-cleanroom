[CmdletBinding()]
param
(
    [switch]
    $NoBuild,

    [switch]
    $NoTest,

    [switch]
    $triggerSnapshotOnCompletion,

    [ValidateSet('mcr', 'local', 'acr')]
    [string]$registry = "local",

    [string]$repo = "localhost:5000",

    [string]$tag = ""
)
#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$sandbox_common = "$PSScriptRoot/sandbox_common"
$ccf = $(Get-Content $sandbox_common/ccf.json | ConvertFrom-Json)
$ccfEndpoint = $ccf.endpoint
Write-Output "Using CCF endpoint: $ccfEndpoint"
pwsh $root/samples/governance/azcli/deploy-cgs.ps1 `
    -ccfEndpoint $ccfEndpoint `
    -outDir $sandbox_common `
    -NoBuild:$NoBuild `
    -NoTest:$NoTest `
    -projectName "member0-governance" `
    -initialMemberName "member0" `
    -registry $registry `
    -repo $repo `
    -tag $tag

if ($triggerSnapshotOnCompletion) {
    Write-Output "Triggering a snapshot post CGS deployment."
    az cleanroom ccf network trigger-snapshot `
        --name $ccf.name `
        --infra-type $ccf.infraType `
        --provider-config $sandbox_common/providerConfig.json
}