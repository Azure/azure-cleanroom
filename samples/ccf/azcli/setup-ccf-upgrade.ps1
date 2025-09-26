[CmdletBinding()]
param
(
    [switch]
    $confidentialRecovery,

    $numChangesToMake = 1
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$sandbox_common = "$PSScriptRoot/sandbox_common"
$ccf = $(Get-Content $sandbox_common/ccf.json | ConvertFrom-Json)
$ccfEndpoint = $ccf.endpoint
$infraType = $ccf.infraType
$networkName = $ccf.name
$securityPolicyFile = "$sandbox_common/updatedNetworkRego.rego"
$initialMemberProjectName = "member0-governance"
$initialMemberName = "member0"

# The strategy to test CCF node code upgrade is essentially to validate that one is able to recover/join
# nodes into the network with new hostData values. Steps are:
# - Generate rego policy variation that is currently not in use on the network. This is done by 
#   adding new line characters to an existing policy so that its hostData value changes.
# - Raise add-snp-host-data proposal and have it accepted.
# - Scale up or recover the ccf network.
# - Confirm that node(s) with new hostData values have come up.
$debugSecurityPolicy = (az cleanroom ccf network security-policy generate `
        --security-policy-creation-option cached-debug `
        --infra-type $infraType | ConvertFrom-Json)
$debugRego = $debugSecurityPolicy.snp.hostData.PSObject.Properties.Value
$debugRego | Out-File $sandbox_common/debugNetworkRego.rego -NoNewline
$updatedRego = $debugRego
for ($i = 0; $i -lt $numChangesToMake; ++$i) {
    $updatedRego += "`n"
}
$updatedRego | Out-File $securityPolicyFile -NoNewline
$updatedHostData = cat $securityPolicyFile | sha256sum | cut -d ' ' -f 1

Write-Output "Join policy before adding ${updatedHostData}:"
az cleanroom ccf network join-policy show `
    --name $networkName `
    --infra-type $infraType `
    --provider-config $sandbox_common/providerConfig.json

$proposal = (az cleanroom ccf network join-policy add-snp-host-data `
        --name $networkName `
        --infra-type $infraType `
        --host-data $updatedHostData `
        --security-policy $securityPolicyFile `
        --provider-config $sandbox_common/providerConfig.json | ConvertFrom-Json)
if ($proposal.proposalState -ne "Accepted") {
    # Assuming deploy-cgs.ps1 script was executed, it adds 1 active member so attempt to accept the proposal via that.
    Write-Output "add-snp-host-data proposal state is '$($proposal.proposalState)'. Launching governance client and accepting as $initialMemberName."

    $setup = Get-Content $sandbox_common/setup.json | ConvertFrom-Json
    $repo = $setup.repo
    $tag = $setup.tag

    if ($repo -ne "") {
        $env:AZCLI_CGS_CLIENT_IMAGE = "$repo/cgs-client:$tag"
        $env:AZCLI_CGS_UI_IMAGE = "$repo/cgs-ui:$tag"
    }
    else {
        $env:AZCLI_CGS_CLIENT_IMAGE = ""
        $env:AZCLI_CGS_UI_IMAGE = ""
    }
    az cleanroom governance client deploy `
        --ccf-endpoint $ccfEndpoint `
        --signing-key $sandbox_common/${initialMemberName}_privk.pem `
        --signing-cert $sandbox_common/${initialMemberName}_cert.pem `
        --service-cert $sandbox_common/service_cert.pem `
        --name $initialMemberProjectName
    $proposal = (az cleanroom governance proposal vote `
            --proposal-id $proposal.proposalId `
            --action accept `
            --governance-client $initialMemberProjectName | ConvertFrom-Json)
    if ($proposal.proposalState -ne "Accepted") {
        throw "add-snp-host-data failed as proposal is still not accepted. Proposal state '$($proposal.proposalState)': $($proposal.proposalId)"
    }
}

Write-Output "Join policy after adding ${updatedHostData}:"
az cleanroom ccf network join-policy show `
    --name $networkName `
    --infra-type $infraType `
    --provider-config $sandbox_common/providerConfig.json

if ($confidentialRecovery) {
    Write-Output "Join policy in recovery service:"
    az cleanroom ccf recovery-service api network show-join-policy `
        --service-config $sandbox_common/recoveryServiceConfig.json
    
    Write-Output "Updating hostData values in recovery service."
    az cleanroom ccf network recovery-agent set-network-join-policy `
        --network-name $networkName `
        --infra-type $infraType `
        --agent-config $sandbox_common/recoveryAgentConfig.json `
        --provider-config $sandbox_common/providerConfig.json
    
    Write-Output "Join policy in recovery service after adding hostdata:"
    az cleanroom ccf recovery-service api network show-join-policy `
        --service-config $sandbox_common/recoveryServiceConfig.json
}
