[CmdletBinding()]
param
(
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$sandbox_common = "$PSScriptRoot/sandbox_common"
$ccf = $(Get-Content $sandbox_common/ccf.json | ConvertFrom-Json)
$infraType = $ccf.infraType
$networkName = $ccf.name
$securityPolicyFile = "$sandbox_common/updatedNetworkRego.rego"

# Add new hostData value in the network before scaling up.
pwsh $PSScriptRoot/setup-ccf-upgrade.ps1 -numChangesToMake 2

$nodeCount = az cleanroom ccf network show `
    --name $networkName `
    --provider-config $sandbox_common/providerConfig.json `
    --query "nodeCount" `
    --output tsv
if ([int]$nodeCount -eq 0) {
    throw "Unexpected nodeCount value $nodeCount. It should be > 0."
}

$scaleUpBy = 2
$newNodeCount = [int]$nodeCount + $scaleUpBy
$updatedHostData = cat $securityPolicyFile | sha256sum | cut -d ' ' -f 1
$securityPolicyBase64 = cat $securityPolicyFile | base64 -w 0

$existingHostDataMatches = 0
foreach ($item in $reports.reports) {
    if ($item.hostData -eq $updatedHostData) {
        $existingHostDataMatches++
    }
}
Write-Output "Scaling up the cluster from $nodeCount to $newNodeCount with nodes using hostData $updatedHostData."
az cleanroom ccf network update `
    --name $networkName `
    --node-count $newNodeCount `
    --node-log-level Debug `
    --security-policy-creation-option user-supplied `
    --security-policy $securityPolicyBase64 `
    --infra-type $infraType `
    --provider-config $sandbox_common/providerConfig.json
$network = (az cleanroom ccf network show `
        --name $networkName `
        --infra-type $infraType `
        --provider-config $sandbox_common/providerConfig.json) | ConvertFrom-Json
if ($network.nodeCount -ne $newNodeCount) {
    throw "Expecting $newNodeCount but $($network.nodeCount) is reported."
}

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

$newMatches = $updatedHostDataMatches - $existingHostDataMatches
if ($newMatches -ne $scaleUpBy) {
    Write-Output ($reports | ConvertTo-Json)
    throw "Expecting to find $scaleUpBy new nodes with hostData $updatedHostData but found $newMatches."
}
else {
    Write-Output "Found $scaleUpBy new nodes with hostData $updatedHostData."
}