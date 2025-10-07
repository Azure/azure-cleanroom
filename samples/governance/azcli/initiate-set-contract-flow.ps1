[CmdletBinding()]
param
(
    [string] $contractId = "4321",

    [Parameter(Mandatory)]
    [string] $issuerUrl,

    [string] $projectName = "governance-sample-azcli"
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true
$root = git rev-parse --show-toplevel

. $root/build/helpers.ps1

function verifyContractState([string] $expectedState) {
    $contract = (az cleanroom governance contract show --id $contractId --governance-client $projectName)
    $contract | jq
    $state = "$contract" | jq -r ".state"
    if ($state -cne $expectedState) {
        Write-Error "contract is in state: $state, expected state: $expectedState"
        exit 1
    }
}

Write-Output "Adding contract"
$data = '{"hello": "world"}'
az cleanroom governance contract create --data $data --id $contractId --governance-client $projectName
verifyContractState("Draft")

Write-Output "Submitting contract proposal"
$version = (az cleanroom governance contract show --id $contractId --governance-client $projectName --query "version" --output tsv)
$proposalId = (az cleanroom governance contract propose --version $version --id $contractId --governance-client $projectName --query "proposalId" --output tsv)
verifyContractState("Proposed")

Write-Output "Accepting the contract proposal"
az cleanroom governance contract vote --id $contractId --proposal-id $proposalId --action accept --governance-client $projectName | jq
verifyContractState("Accepted")

pwsh $PSScriptRoot/initiate-set-member-document-flow.ps1 -contractId $contractId -projectName $projectName
pwsh $PSScriptRoot/initiate-set-deployment-spec-flow.ps1 -contractId $contractId -projectName $projectName
pwsh $PSScriptRoot/initiate-set-cleanroom-policy-flow.ps1 -contractId $contractId -projectName $projectName
pwsh $PSScriptRoot/initiate-set-user-document-flow.ps1 -contractId $contractId -projectName $projectName
pwsh $PSScriptRoot/initiate-oidc-issuer-flow.ps1 -issuerUrl $issuerUrl -projectName $projectName