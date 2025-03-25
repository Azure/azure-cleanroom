# Load all scripts
Get-ChildItem $PSScriptRoot/*.ps1 | ForEach { . $_.FullName }

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
