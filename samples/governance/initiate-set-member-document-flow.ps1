[CmdletBinding()]
param
(
    [string]
    [Parameter(Mandatory)]
    $contractId,

    [string] $documentId = "1234",

    [string] $port = "9290"
)

$root = git rev-parse --show-toplevel

. $root/build/helpers.ps1

Import-Module $PSScriptRoot/scripts/cgs.psm1 -Force -DisableNameChecking

function verifyDocumentState([string] $expectedState) {
    $document = (Get-MemberDocument -id $documentId -port $port)
    $document | jq
    $state = "$document" | jq -r ".state"
    if ($state -cne $expectedState) {
        Write-Error "document is in state: $state, expected state: $expectedState"
        exit 1
    }
}

Write-Output "Adding document"
$data = '{"hello": "world"}'
Create-MemberDocument -data $data -id $documentId -contractId $contractId -port $port
verifyDocumentState("Draft")

Write-Output "Submitting document proposal"
$version = (Get-MemberDocument -id $documentId -port $port | jq -r ".version")
$proposalId = (Propose-MemberDocument -version $version -id $documentId -port $port | jq -r '.proposalId')
verifyDocumentState("Proposed")

Write-Output "Accepting the document proposal"
Vote-MemberDocument -id $documentId -proposalId $proposalId -vote accept -port $port | jq
verifyDocumentState("Accepted")