[CmdletBinding()]
param
(
    [string]
    $contractId,

    [string] $port = "9290"
)

$root = git rev-parse --show-toplevel

. $root/build/helpers.ps1

Import-Module $PSScriptRoot/scripts/cgs.psm1 -Force -DisableNameChecking

# -allowAll below maps to the attestation report placed under samples/reports/insecure-virtual.
Write-Output "Submitting clean room policy proposal under contract $contractId"
$proposalId = (Propose-CleanRoom-Policy -contractId $contractId -allowAll -port $port | jq -r '.proposalId')

Write-Output "Accepting the policy proposal"
Vote-Proposal -proposalId $proposalId -vote accept -port $port | jq

Write-Output "Clean room policy:"
$p = (Get-CleanRoom-Policy -contractId $contractId -port $port)
$p | jq