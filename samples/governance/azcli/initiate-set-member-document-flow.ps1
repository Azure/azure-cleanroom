[CmdletBinding()]
param
(
    [string]
    [Parameter(Mandatory)]
    $contractId,

    [string] $documentId = "5678",

    [string] $projectName = "governance-sample-azcli"
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true
$root = git rev-parse --show-toplevel

. $root/build/helpers.ps1

function verifyDocumentState([string] $expectedState) {
    $document = (az cleanroom governance member-document show --id $documentId --governance-client $projectName)
    $document | jq
    $state = "$document" | jq -r ".state"
    if ($state -cne $expectedState) {
        Write-Error "document is in state: $state, expected state: $expectedState"
        exit 1
    }
}

Write-Output "Adding member document"
$data = '{"hello": "world"}'
az cleanroom governance member-document create --data $data --id $documentId --contract-id $contractId --governance-client $projectName
verifyDocumentState("Draft")

Write-Output "Submitting member document proposal"
$version = (az cleanroom governance member-document show --id $documentId --governance-client $projectName --query "version" --output tsv)
$proposalId = (az cleanroom governance member-document propose --version $version --id $documentId --governance-client $projectName --query "proposalId" --output tsv)
verifyDocumentState("Proposed")

Write-Output "Accepting the member document proposal"
az cleanroom governance member-document vote --id $documentId --proposal-id $proposalId --action accept --governance-client $projectName | jq
verifyDocumentState("Accepted")