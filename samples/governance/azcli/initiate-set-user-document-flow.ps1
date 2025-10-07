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

function verifyUserDocumentState([string] $expectedState) {
    $document = (az cleanroom governance user-document show --id $documentId --governance-client $projectName)
    $document | jq
    $state = "$document" | jq -r ".state"
    if ($state -cne $expectedState) {
        Write-Error "document is in state: $state, expected state: $expectedState"
        exit 1
    }
}

Write-Output "Adding user document"
$data = '{"hello": "world"}'
az cleanroom governance user-document create --data $data --id $documentId --contract-id $contractId --governance-client $projectName
verifyUserDocumentState("Draft")

Write-Output "Submitting user document proposal"
$version = (az cleanroom governance user-document show --id $documentId --governance-client $projectName --query "version" --output tsv)
$proposalId = (az cleanroom governance user-document propose --version $version --id $documentId --governance-client $projectName --query "proposalId" --output tsv)
verifyUserDocumentState("Proposed")

Write-Output "Accepting the user document proposal"
az cleanroom governance user-document vote --id $documentId --proposal-id $proposalId --action accept --governance-client $projectName | jq
verifyUserDocumentState("Accepted")

$options = @("execution", "telemetry")
foreach ($option in $options) {
    az cleanroom governance user-document runtime-option get --document-id $documentId --option $option --governance-client $projectName | jq

    az cleanroom governance user-document runtime-option set --document-id $documentId --option $option --action disable --governance-client $projectName | jq

    az cleanroom governance user-document runtime-option get --document-id $documentId --option $option --governance-client $projectName | jq

    az cleanroom governance user-document runtime-option set --document-id $documentId --option $option --action enable --governance-client $projectName | jq

    az cleanroom governance user-document runtime-option get --document-id $documentId --option $option --governance-client $projectName | jq
}
