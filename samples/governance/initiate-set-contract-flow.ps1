[CmdletBinding()]
param
(
    [string] $contractId = "1234",

    [string] $issuerUrl = "https://localhost:9080/app/oidc",

    [string] $port = "9290"
)

$root = git rev-parse --show-toplevel

. $root/build/helpers.ps1

Import-Module $PSScriptRoot/scripts/cgs.psm1 -Force -DisableNameChecking

function verifyContractState([string] $expectedState) {
    $contract = (Get-Contract -id $contractId -port $port)
    $contract | jq
    $state = "$contract" | jq -r ".state"
    if ($state -cne $expectedState) {
        Write-Error "contract is in state: $state, expected state: $expectedState"
        exit 1
    }
}

Write-Output "Adding contract"
$data = '{"hello": "world"}'
Create-Contract -data $data -id $contractId -port $port
verifyContractState("Draft")

Write-Output "Submitting contract proposal"
$version = (Get-Contract -id $contractId -port $port | jq -r ".version")
$proposalId = (Propose-Contract -version $version -id $contractId -port $port | jq -r '.proposalId')
verifyContractState("Proposed")

Write-Output "Accepting the contract proposal"
Vote-Contract -id $contractId -proposalId $proposalId -vote accept -port $port | jq
verifyContractState("Accepted")

pwsh $PSScriptRoot/initiate-set-member-document-flow.ps1 -contractId $contractId -port $port
pwsh $PSScriptRoot/initiate-set-deployment-spec-flow.ps1 -contractId $contractId -port $port
pwsh $PSScriptRoot/initiate-set-cleanroom-policy-flow.ps1 -contractId $contractId -port $port
pwsh $PSScriptRoot/initiate-oidc-issuer-flow.ps1 -issuerUrl $issuerUrl -port $port
pwsh $PSScriptRoot/initiate-ca-flow.ps1 -contractId $contractId -port $port