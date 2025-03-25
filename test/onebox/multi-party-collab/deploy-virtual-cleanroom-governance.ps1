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
    [string]$ccfProjectName,

    [Parameter(Mandatory)]
    [string]$projectName,

    [Parameter(Mandatory)]
    [string]$initialMemberName,

    [Parameter(Mandatory)]
    [string]$outDir

)
$root = git rev-parse --show-toplevel

$outDir = "$outDir/ccf"
mkdir -p $outDir
pwsh $root/samples/governance/azcli/deploy-cgs.ps1 `
    -initialMemberName $initialMemberName `
    -projectName $projectName `
    -ccfProjectName $ccfProjectName `
    -outDir $outDir `
    -NoTest `
    -NoBuild:$NoBuild `
    -registry $registry `
    -repo $repo `
    -tag $tag