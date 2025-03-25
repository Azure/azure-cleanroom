[CmdletBinding()]
param
(
    $numChangesToMake = 1,

    [int]
    $nodeCount = 1,

    [string]
    [ValidateSet("Trace", "Debug", "Info", "Fail", "Fatal")]
    $nodeLogLevel = "Debug",

    [string]
    [ValidateSet("cached", "cached-debug", "allow-all", "user-supplied")]
    $securityPolicyCreationOption = "allow-all",

    [switch]
    $OneStepRecovery,

    [string]$repo = "",

    [string]$tag = "latest"
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$sandbox_common = "$PSScriptRoot/sandbox_common"
$ccf = $(Get-Content $sandbox_common/ccf.json | ConvertFrom-Json)
$ccfEndpoint = $ccf.endpoint
$infraType = $ccf.infraType
$networkName = $ccf.name
$securityPolicyFile = "$sandbox_common/updatedRecoveryServiceRego.rego"
$recovery = $(Get-Content $sandbox_common/recoveryResources.json | ConvertFrom-Json)
$cgsProjectName = "ccf-provider-governance"
$initialMemberName = "member0"
$initialMemberProjectName = "member0-governance"

# The strategy to test confidential recovery service code upgrade is essentially to validate that 
# one is able to recover/join nodes into the network with new hostData values. Steps are:
# - Generate rego policy variation that is currently not in use. This is done by 
#   adding new line characters to an existing policy so that its hostData value changes.
# - Create a new recovery service instance with new hostData value.
# - Add new recovery member belonging to the new recovery service instance.
# - Recover the ccf network using the new recovery service instance.
az cleanroom ccf network security-policy generate-join-policy-from-network `
    --name $networkName `
    --provider-config $sandbox_common/providerConfig.json | Out-File $sandbox_common/$networkName-networkJoinPolicy.json

$debugSecurityPolicy = (az cleanroom ccf recovery-service security-policy generate `
        --security-policy-creation-option cached-debug `
        --ccf-network-join-policy $sandbox_common/$networkName-networkJoinPolicy.json `
        --infra-type $infraType | ConvertFrom-Json)
$debugRego = $debugSecurityPolicy.snp.hostData.PSObject.Properties.Value
$debugRego | Out-File $sandbox_common/debugRecoveryServiceRego.rego -NoNewline
$updatedRego = $debugRego
for ($i = 0; $i -lt $numChangesToMake; ++$i) {
    $updatedRego += "`n"
}
$updatedRego | Out-File $securityPolicyFile -NoNewline
$updatedHostData = cat $securityPolicyFile | sha256sum | cut -d ' ' -f 1
$securityPolicyBase64 = cat $securityPolicyFile | base64 -w 0

$reportResponse = (az cleanroom ccf recovery-service api show-report `
        --service-config $sandbox_common/recoveryServiceConfig.json | ConvertFrom-Json)
$currentHostData = $reportResponse.hostData
Write-Output "HostData value of existing recovery service: $currentHostData"
Write-Output "HostData value of recovery service that will get created: $updatedHostData"

$serviceName = $networkName + "-upgrade"
Write-Output "Deploying new $serviceName CCF recovery service instance and adding confidential recoverer."
az cleanroom ccf recovery-service create `
    --name $serviceName `
    --infra-type $infraType `
    --key-vault $recovery.kvId `
    --maa-endpoint $recovery.maaEndpoint `
    --identity $recovery.miId `
    --ccf-network-join-policy $sandbox_common/$networkName-networkJoinPolicy.json `
    --security-policy-creation-option user-supplied `
    --security-policy $securityPolicyBase64 `
    --provider-config $sandbox_common/providerConfig.json

$recSvc = (az cleanroom ccf recovery-service show `
        --name $serviceName `
        --infra-type $infraType `
        --provider-config $sandbox_common/providerConfig.json | ConvertFrom-Json)
$rsvcConfig = @{}
$rsvcConfig.recoveryService = @{}
$rsvcConfig.recoveryService.endpoint = $recSvc.endpoint
$rsvcConfig.recoveryService.serviceCert = $recSvc.serviceCert
$rsvcConfig | ConvertTo-Json -Depth 100 > $sandbox_common/updatedRecoveryServiceConfig.json

Write-Output "Querying the hostData value for $serviceName CCF recovery service..."
$reportResponse = (az cleanroom ccf recovery-service api show-report `
        --service-config $sandbox_common/updatedRecoveryServiceConfig.json | ConvertFrom-Json)
$currentHostData = $reportResponse.hostData
if ($currentHostData -ne $updatedHostData) {
    throw "Expecting node to have started with $updatedHostData hostData but node is reporting hostData value as $currentHostData."
}
if ($reportResponse.verified -ne $true) {
    throw "Expecting report.verified for $serviceName to be true. Report verification failed."
}
Write-Output "New recovery service $serviceName is running with hostData $updatedHostData."

$recSvc = (az cleanroom ccf recovery-service show `
        --name $serviceName `
        --infra-type $infraType `
        --provider-config $sandbox_common/providerConfig.json | ConvertFrom-Json)
$agentConfig = @{}
$agentConfig.recoveryService = @{}
$agentConfig.recoveryService.endpoint = $recSvc.endpoint
$agentConfig.recoveryService.serviceCert = $recSvc.serviceCert
$agentConfig | ConvertTo-Json -Depth 100 > $sandbox_common/${serviceName}-RecoveryAgentConfig.json

$recoveryMemberName = $recovery.confidentialRecovererMemberName + "-upgrade"
Write-Output "Generating confidential recovery member $recoveryMemberName in $serviceName."
az cleanroom ccf network recovery-agent generate-member `
    --network-name $networkName `
    --member-name $recoveryMemberName `
    --infra-type $infraType `
    --agent-config $sandbox_common/${serviceName}-RecoveryAgentConfig.json `
    --provider-config $sandbox_common/providerConfig.json

Write-Output "Adding confidential recovery member $recoveryMemberName into the consortium."
$crm = (az cleanroom ccf recovery-service api member show `
        --member-name $recoveryMemberName `
        --service-config $sandbox_common/updatedRecoveryServiceConfig.json | ConvertFrom-Json)
$crm.encryptionPublicKey | Out-File $sandbox_common/${recoveryMemberName}_enc_pubk.pem
$crm.signingCert | Out-File $sandbox_common/${recoveryMemberName}_cert.pem
$crmData = @{}
$crmData.identifier = $recoveryMemberName
$crmData.isRecoveryOperator = $true
$crmData.recoveryService = $crm.recoveryService | ConvertTo-Json -Depth 100 | ConvertFrom-Json
$crmData | ConvertTo-Json -Depth 100 | Out-File $sandbox_common/${recoveryMemberName}_member_data.json

$proposal = (az cleanroom governance member add `
        --certificate $sandbox_common/${recoveryMemberName}_cert.pem `
        --encryption-public-key $sandbox_common/${recoveryMemberName}_enc_pubk.pem `
        --recovery-role owner `
        --member-data $sandbox_common/${recoveryMemberName}_member_data.json `
        --governance-client $cgsProjectName | ConvertFrom-Json)
if ($proposal.proposalState -ne "Accepted") {
    # Assuming deploy-cgs.ps1 script was executed, it adds 1 active member so attempt to accept the proposal via that.
    Write-Output "set_member proposal state is '$($proposal.proposalState)'. Launching governance client and accepting as $initialMemberName."

    if ($repo -ne "") {
        $server = $repo
        $localTag = $tag
        $env:AZCLI_CGS_CLIENT_IMAGE = "$server/cgs-client:$localTag"
        $env:AZCLI_CGS_UI_IMAGE = "$server/cgs-ui:$localTag"
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
        throw "Expecting $($proposal.proposalId) to be in Accepted state but state is $($proposal.proposalState)."
    }
}

pwsh $PSScriptRoot/verify-recovery-operators.ps1 `
    -recoveryServiceName $serviceName `
    -recoveryMemberName $recoveryMemberName `
    -governanceClient $cgsProjectName

Write-Host "Requesting $serviceName CCF recovery service to activate its membership in the network."
az cleanroom ccf network recovery-agent activate-member `
    --network-name $networkName `
    --member-name $recoveryMemberName `
    --infra-type $infraType `
    --agent-config $sandbox_common/${serviceName}-RecoveryAgentConfig.json `
    --provider-config $sandbox_common/providerConfig.json

Write-Output "Recovering $networkName network via $serviceName CCF recovery service."
pwsh $PSScriptRoot/recover-ccf.ps1 `
    -nodeCount $nodeCount `
    -confidentialRecovery `
    -OneStepRecovery:$OneStepRecovery `
    -recoveryServiceName $serviceName `
    -recoveryMemberName $recoveryMemberName `
    -securityPolicyCreationOption $securityPolicyCreationOption `
    -repo $repo `
    -tag $tag
