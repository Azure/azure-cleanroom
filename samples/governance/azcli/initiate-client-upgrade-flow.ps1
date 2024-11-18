[CmdletBinding()]
param
(
    [string] $projectName = "governance-sample-azcli",

    [string] $version = "1.0.8"
)

$root = git rev-parse --show-toplevel

. $root/build/helpers.ps1

$versions = (az cleanroom governance client version --name $projectName) | ConvertFrom-Json
if ($versions."cgs-client".version -cne $version) {
    $x = $versions."cgs-client".version
    Write-Error "client version: $x, expected version: $version"
    exit 1
}

$upgrades = (az cleanroom governance client get-upgrades --name $projectName | ConvertFrom-Json)
if ($upgrades.upgrades.Count -ne 0) {
    Write-Error "Not expecting any updates but seeing: ${upgrades.upgrades}"
    exit 1
}

