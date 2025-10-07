[CmdletBinding()]
param
(
    [string]
    $contractId,

    [string] $projectName
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true
$root = git rev-parse --show-toplevel

. $root/build/helpers.ps1

# -allowAll below maps to the attestation report placed under samples/governance/insecure-virtual/attestation.
Write-Output "Submitting clean room policy proposal under contract $contractId"
$proposalId = (az cleanroom governance deployment policy propose --contract-id $contractId --allow-all --governance-client $projectName --query "proposalId" --output tsv)

Write-Output "Accepting the policy proposal $proposalId"
az cleanroom governance proposal vote --proposal-id $proposalId --action accept --governance-client $projectName | jq

Write-Output "Clean room policy:"
az cleanroom governance deployment policy show --contract-id $contractId --governance-client $projectName | jq