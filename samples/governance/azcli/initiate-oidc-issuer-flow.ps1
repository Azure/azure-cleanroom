[CmdletBinding()]
param
(
    [string]
    [Parameter(Mandatory)]
    $issuerUrl,

    [string] $projectName = "governance-sample-azcli"
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true
$root = git rev-parse --show-toplevel

. $root/build/helpers.ps1

# az cli service deploy takes care of enabling the OIDC issuer and generating the issuer key so not doing that here.
Write-Output "Submitting set oidc issuer URL proposal with value $issuerUrl"
$proposalId = (az cleanroom governance oidc-issuer propose-set-issuer-url --url $issuerUrl --governance-client $projectName --query "proposalId" --output tsv)

Write-Output "Accepting the proposal $proposalId"
az cleanroom governance proposal vote --proposal-id $proposalId --action accept --governance-client $projectName | jq

Write-Output "OIDC issuer configured. OIDC discovery document:"
$response = $(curl -sS -X GET $issuerUrl/.well-known/openid-configuration -k)
$response | jq

$jwks_uri = $response | jq -r '.jwks_uri'

Write-Output "JWKS document:"
curl -sS -X GET $jwks_uri -k | jq