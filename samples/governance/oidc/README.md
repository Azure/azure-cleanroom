## Using OIDC Provider (IdP) running in CGS for getting federated credentials from Azure
```pwsh
# endpoint is where the CGS instance is running and publicly reachable. Change as appropriate for you.
$endpoint="https://cgssvc.duckdns.org:8080"

# The user-assigned managed identity federated credential details. Update as appropriate for you.
$clientId = "df06c73e-f31d-4573-9f08-6225548b4ca1"
$tenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47"
$scope="https://vault.azure.net/.default"
$sub="123456"
$aud="api://AzureADTokenExchange"

# Get an access token from Azure using the ID token from CGS.
./samples/governance/oidc/initiate-get-access-token-flow.ps1 -endpoint $endpoint -clientId $clientId -tenantId $tenantId -scope $scope -sub $sub -aud $aud
```
