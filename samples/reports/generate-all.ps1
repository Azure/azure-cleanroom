[CmdletBinding()]
param
(
    [string]
    $resourceGroup = "gsinhadev"
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

pwsh $PSScriptRoot/generate-caci-reports.ps1 -resourceGroup $resourceGroup -forceDeploy
pwsh $PSScriptRoot/generate-caci-reports.ps1 -resourceGroup $resourceGroup -forceDeploy -useCcePolicy2

pwsh $PSScriptRoot/generate-cvm-reports.ps1