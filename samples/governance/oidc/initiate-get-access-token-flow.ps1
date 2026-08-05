[CmdletBinding()]
param
(
  [string]
  $endpoint = "https://cgssvc.duckdns.org:8080",
  [string]
  $clientId = "df06c73e-f31d-4573-9f08-6225548b4ca1",
  [string]
  $tenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47",
  [string]
  $scope = "https://vault.azure.net/.default",
  [string]
  $sub = "123456",
  [string]
  $aud = "api://AzureADTokenExchange",
  [string]
  $contractId = ""
)

# Pre-req: Below steps assume CGS has been deployed at the endpoint specified via $endpoint. Update the $endpoint as appropriate for you.
$issuerUrl = "$endpoint/app/oidc"
$port = "7090" # cgs-client container
$govPort = "7990" # ccr-governance sidecar container

if ($contractId -eq "") {
  # Setup a contract, clean room policy and the OIDC issuer in CGS.
  $contractId = $(New-Guid).ToString().Substring(0, 8)
  pwsh $PSScriptRoot/../initiate-set-contract-flow.ps1 -port $port -contractId $contractId -issuerUrl $issuerUrl
}

# clientId, tenandId, and sub parameters assume that a user assigned managed identity was configured with federated credentials per https://learn.microsoft.com/en-us/entra/workload-id/workload-identity-federation-create-trust-user-assigned-managed-identity?pivots=identity-wif-mi-methods-azp#other and the issuer URL in Azure is same as $issuerUrl above and sub value matches the contractId.
# Request a client assertion (JWT token) from the IdP endpoint running in CGS and then exchange it with an access token from Azure.
$clientAssertion = $(pwsh $PSScriptRoot/../oidc/get-idp-token.ps1 -sub $sub -aud $aud -tenantId $tenantId -contractId $contractId -govPort $govPort)
pwsh ${PSScriptRoot}/../oidc/get-access-token.ps1 -scope $scope -clientId $clientId -tenantId $tenantId -clientAssertion $clientAssertion