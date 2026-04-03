[CmdletBinding()]
param
(
    [string]
    $ccfProjectName,

    [string]
    $projectName
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

docker compose -f $PSScriptRoot/docker-compose.yml -p $ccfProjectName down
az cleanroom governance client remove --name $projectName
rm -rf $PSScriptRoot/sandbox_common