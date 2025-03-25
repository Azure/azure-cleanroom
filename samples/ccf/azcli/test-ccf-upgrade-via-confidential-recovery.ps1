[CmdletBinding()]
param
(
    [string]$repo = "",

    [string]$tag = "latest"
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$sandbox_common = "$PSScriptRoot/sandbox_common"
$ccf = $(Get-Content $sandbox_common/ccf.json | ConvertFrom-Json)
$infraType = $ccf.infraType
$networkName = $ccf.name
$securityPolicyFile = "$sandbox_common/updatedNetworkRego.rego"

# Add new hostData value in the network and the conf. recovery service.
pwsh $PSScriptRoot/setup-ccf-upgrade.ps1 -numChangesToMake 1 -confidentialRecovery -repo $repo -tag $tag

$updatedHostData = cat $securityPolicyFile | sha256sum | cut -d ' ' -f 1
$nodeCount = 1
Write-Output "Triggering confidential network recovery with nodes using hostData $updatedHostData"
pwsh $PSScriptRoot/recover-ccf.ps1 `
    -nodeCount $nodeCount `
    -confidentialRecovery `
    -securityPolicyCreationOption user-supplied `
    -securityPolicy $securityPolicyFile `
    -repo $repo `
    -tag $tag

Write-Output "Join policy on the network after recovery"
az cleanroom ccf network join-policy show `
    --name $networkName `
    --infra-type $infraType `
    --provider-config $sandbox_common/providerConfig.json

$reports = az cleanroom ccf network show-report `
    --name $networkName `
    --infra-type $infraType `
    --provider-config $sandbox_common/providerConfig.json | ConvertFrom-Json
$updatedHostDataMatches = 0
foreach ($item in $reports.reports) {
    if ($item.hostData -eq $updatedHostData) {
        $updatedHostDataMatches++
    }

    if ($item.verified -ne $true) {
        throw "Expecting report.verified to be true. Report verification failed."
    }
}

if ($updatedHostDataMatches -ne $nodeCount) {
    Write-Output ($reports | ConvertTo-Json)
    throw "Expecting to find $nodeCount new nodes with hostData $updatedHostData but found $updatedHostDataMatches."
}
else {
    Write-Output "Found $updatedHostDataMatches new nodes with hostData $updatedHostData."
}