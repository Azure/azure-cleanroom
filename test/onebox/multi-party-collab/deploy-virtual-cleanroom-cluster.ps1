[CmdletBinding()]
param
(
    [switch]
    $NoBuild,

    [ValidateSet('mcr', 'local', 'acr')]
    [string]$registry = "local",

    [string]$repo = "",

    [string]$tag = "latest",

    [Parameter(Mandatory)]
    [string]$clusterProviderProjectName,

    [Parameter(Mandatory)]
    [string]$clusterName,

    [Parameter(Mandatory)]
    [string]$outDir

)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel

$outDir = "$outDir/cl-cluster"
mkdir -p $outDir
pwsh $root/samples/spark/azcli/deploy-cluster.ps1 `
    -infraType virtual `
    -donotEnableAnalytics `
    -registry $registry `
    -repo $repo `
    -tag $tag `
    -outDir $outDir `
    -clusterProviderProjectName $clusterProviderProjectName `
    -clusterName $clusterName `
    -NoBuild:$NoBuild