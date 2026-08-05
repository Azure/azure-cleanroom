[CmdletBinding()]
param
(
    [switch]
    $NoBuild,

    [ValidateSet('mcr', 'local', 'acr')]
    [string]$registry = "local",

    [string]$repo = "localhost:5000",

    [string]$tag = "latest",

    [switch]
    $useCsiDriver,

    [switch]
    $SkipCsiTests,

    [switch]
    $useBlobfuseProxySidecar,

    [string]
    $SecondaryContractId = "",

    [string]
    $ContractId = "collab2",

    [string]
    $OutDir = "$PSScriptRoot/generated-second"
)

# https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

Write-Host "Running second full collab setup with contract '$ContractId' and output dir '$OutDir'."

pwsh $PSScriptRoot/run-collab.ps1 `
    -NoBuild:$NoBuild `
    -registry $registry `
    -repo $repo `
    -tag $tag `
    -ContractId $ContractId `
    -OutDir $OutDir `
    -useCsiDriver:$useCsiDriver `
    -SkipCsiTests:$SkipCsiTests `
    -useBlobfuseProxySidecar:$useBlobfuseProxySidecar `
    -SecondaryContractId $SecondaryContractId
