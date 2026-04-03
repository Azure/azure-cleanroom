[CmdletBinding()]
param
(
    [string]
    $contractId
)

$root = git rev-parse --show-toplevel
$port_member0 = "8290"
$port_member1 = "8291"
$port_member2 = "8292"

. $root/build/helpers.ps1

Import-Module $root/samples/governance/scripts/cgs.psm1 -Force -DisableNameChecking

# -allowAll below maps to the attestation report placed under samples/reports/insecure-virtual/attestation.
Write-Output "Submitting clean room policy proposal under contract $contractId as member0"
$proposalId = (Propose-CleanRoom-Policy -contractId $contractId -allowAll -port $port_member0 | jq -r '.proposalId')

Write-Output "Accepting the policy proposal as member0"
Vote-Proposal -proposalId $proposalId -vote accept -port $port_member0 | jq

Write-Output "Accepting the policy proposal as member1"
Vote-Proposal -proposalId $proposalId -vote accept -port $port_member1 | jq

Write-Output "Accepting the policy proposal as member2"
Vote-Proposal -proposalId $proposalId -vote accept -port $port_member2 | jq

Write-Output "Clean room policy:"
$p = (Get-CleanRoom-Policy -contractId $contractId -port $port_member0)
$p | jq