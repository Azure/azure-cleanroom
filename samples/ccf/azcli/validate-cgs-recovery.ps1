[CmdletBinding()]
param
(
    [string] $contractId = "4321",

    [string] $documentId = "5678",

    [string]
    $targetNetworkName = ""
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel

. $root/build/helpers.ps1

if ($targetNetworkName -eq "") {
    # Target network is same as the network being recovered as its an
    # in-place recovery.
    $projectName = "ccf-provider-governance"
}
else {
    $projectName = "ccf-provider-governance-$targetNetworkName"
}

function verifyContractState([string] $expectedState) {
    $contract = (az cleanroom governance contract show --id $contractId --governance-client $projectName)
    $contract | jq
    $state = "$contract" | jq -r ".state"
    if ($state -cne $expectedState) {
        Write-Error "contract is in state: $state, expected state: $expectedState"
        exit 1
    }
}

function verifyDocumentState([string] $expectedState) {
    $document = (az cleanroom governance document show --id $documentId --governance-client $projectName)
    $document | jq
    $state = "$document" | jq -r ".state"
    if ($state -cne $expectedState) {
        Write-Error "document is in state: $state, expected state: $expectedState"
        exit 1
    }
}

Write-Output "Checking that contract $contractId exists"
verifyContractState("Accepted")
Write-Output "Checking that document $contractId exists"
verifyDocumentState("Accepted")

Write-Output "Deployment spec:"
az cleanroom governance deployment template show --contract-id $contractId --governance-client $projectName | jq

Write-Output "Clean room policy:"
az cleanroom governance deployment policy show --contract-id $contractId --governance-client $projectName | jq

$ccfEndpoint = az cleanroom governance client show --name $projectName --query ccfEndpoint --output tsv
$issuerUrl = "$ccfEndpoint/app/oidc"

Write-Output "OIDC discovery document:"
$response = $(curl -sS -X GET $issuerUrl/.well-known/openid-configuration -k)
$response | jq

# As we have not proposed a new issuerUrl for the new ccfEndpoint cannot use the value reported out.
#$jwks_uri = $response | jq -r '.jwks_uri'
$jwks_uri = "$issuerUrl/keys"
Write-Output "JWKS document:"
curl -sS -X GET $jwks_uri -k | jq