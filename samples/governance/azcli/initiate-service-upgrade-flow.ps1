[CmdletBinding()]
param
(
    [string] $projectName = "governance-sample-azcli",

    [string] $version = "1.0.8",

    [string] $registry = "localhost:5000"
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel

. $root/build/helpers.ps1

$versions = (az cleanroom governance service version --governance-client $projectName) | ConvertFrom-Json
if ($versions.constitution.version -cne $version) {
    $x = $versions.constitution.version
    Write-Error "constitution version: $x, expected version: $version"
    exit 1
}

if ($versions.jsapp.version -cne $version) {
    $x = $versions.jsapp.version
    Write-Error "jsapp version: $x, expected version: $version"
    exit 1
}

$upgrades = (az cleanroom governance service get-upgrades --governance-client $projectName | ConvertFrom-Json)
if ($upgrades.upgrades.Count -ne 0) {
    Write-Error "Not expecting any updates but seeing: ${upgrades.upgrades}"
    exit 1
}

az cleanroom governance service upgrade-constitution `
    --constitution-url "${registry}/cgs-constitution:$version" `
    --governance-client $projectName

# Get the constitution proposal and vote on it.
$proposalId = az cleanroom governance service upgrade status `
    --governance-client $projectName `
    --query "proposals[?(actionName=='set_constitution')].proposalId" `
    --output "tsv"

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $projectName

az cleanroom governance service upgrade-js-app `
    --js-app-url "${registry}/cgs-js-app:$version" `
    --governance-client $projectName

# Get the jsapp proposal and vote on it.
$proposalId = az cleanroom governance service upgrade status `
    --governance-client $projectName `
    --query "proposals[?(actionName=='set_js_app')].proposalId" `
    --output "tsv"

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $projectName