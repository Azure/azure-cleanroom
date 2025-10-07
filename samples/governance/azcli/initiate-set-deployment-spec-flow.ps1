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

Write-Output "Submitting deployment spec proposal under contract $contractId"
@"
{
    "armTemplate": "something"
}
"@ > template.json.tmp

$proposalId = (az cleanroom governance deployment template propose --contract-id $contractId --template-file ./template.json.tmp --governance-client $projectName --query "proposalId" --output tsv)

Write-Output "Accepting the deployment spec proposal $proposalId"
az cleanroom governance proposal vote --proposal-id $proposalId --action accept --governance-client $projectName | jq

Write-Output "Deployment spec:"
az cleanroom governance deployment template show --contract-id $contractId --governance-client $projectName | jq
