[CmdletBinding()]
param
(
    [switch]
    $NoBuild,
    [Parameter(Mandatory)]
    [string]$resourceGroup,

    [Parameter(Mandatory)]
    [string]$location,

    [Parameter(Mandatory)]
    [string]$clusterName,

    [Parameter(Mandatory)]
    [string]$repo,

    [Parameter(Mandatory)]
    [string]$tag,

    [Parameter(Mandatory)]
    [string]$outDir,
    
    [Parameter(Mandatory)]
    [string]$clusterProviderProjectName,

    [ValidateSet('mcr', 'local', 'acr')]
    [string]$registry = "local"
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$outDir = "$outDir/cl-cluster"
mkdir -p $outDir

function Get-UniqueString ([string]$id, $length = 13) {
    $hashArray = (new-object System.Security.Cryptography.SHA512Managed).ComputeHash($id.ToCharArray())
    -join ($hashArray[1..$length] | ForEach-Object { [char]($_ % 26 + [byte][char]'a') })
}

$ISV_RESOURCE_GROUP = $resourceGroup
$CLUSTER_NAME = $clusterName

if ($env:GITHUB_ACTIONS -ne "true") {
    if (!$NoBuild -and $registry -eq "local") {
        # Install az cli before deploying ccf so that we can invoke az cleanroom ccf.
        # For Github Actions flow its built and installed as part of the workflow.
        pwsh $root/build/build-azcliext-cleanroom.ps1
    }
    else {
        & {
            # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
            $PSNativeCommandUseErrorActionPreference = $false
            az cleanroom -h 2>$null 1>$null
            if ($LASTEXITCODE -gt 0) {
                Write-Host -ForegroundColor Red "az cli cleanroom extension not found. Install and try again."
                throw "az cli cleanroom extension not found. Install and try again."
            }
        }
    }
}

pwsh $PSScriptRoot/cleanroom-cluster-up.ps1 `
    -resourceGroup $ISV_RESOURCE_GROUP `
    -clusterName $CLUSTER_NAME `
    -location $location `
    -repo $repo `
    -tag $tag `
    -outDir $outDir `
    -clusterProviderProjectName $clusterProviderProjectName
