param(
    [switch]
    $NoBuild,

    [parameter(Mandatory = $false)]
    [string]$port = "8321"
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel

if (!$NoBuild) {
    pwsh $root/build/ccr/build-local-idp.ps1
}

$containerName = "local-idp"
docker rm -f $containerName 2>$null
docker run -d `
    --name $containerName `
    -p ${port}:8399 `
    local-idp