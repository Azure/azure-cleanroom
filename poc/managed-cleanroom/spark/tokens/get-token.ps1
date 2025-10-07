[CmdletBinding()]
param
(
  [string]
  $scope = "user.read openid profile",
  [string]
  $clientId = "c99753b6-3f16-4251-bf2e-f4f9bf6a6e9f",
  [string]
  $tenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47"
)

# https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-device-code#device-authorization-request
$endpoint = "https://login.microsoftonline.com/${tenantId}/oauth2/v2.0/devicecode";
$scope = [uri]::EscapeDataString($scope);
$data = "scope=${scope}&client_id=${clientId}";
$response = (curl -sS -X POST -H "content-type: application/x-www-form-urlencoded" -d $data ${endpoint} | ConvertFrom-Json)
Write-Output $response.message

$endpoint = "https://login.microsoftonline.com/${tenantId}/oauth2/v2.0/token"
$deviceCode = $response.device_code
$data = "grant_type=urn:ietf:params:oauth:grant-type:device_code&client_id=${clientId}&device_code=${deviceCode}"
$keepPooling = $true
while ($keepPooling) {
  $response = (curl -sS -X POST -H "content-type: application/x-www-form-urlencoded" -d $data ${endpoint} | ConvertFrom-Json)
  $response | ConvertTo-Json | jq
  if ($response.error -eq "authorization_pending") {
    Start-Sleep -Seconds 5
  }
  elseif ($response.error -eq "expired_token") {
    Write-Output "Token expired, please try again"
    $keepPooling = $false
  }
  elseif ($response.error -eq "slow_down") {
    Start-Sleep -Seconds 5
  }
  elseif ($response.error -eq "interaction_required") {
    Write-Output "Please go to the following URL and enter the code: $($response.verification_uri)?user_code=$($response.user_code)"
    Start-Sleep -Seconds 5
  }
  elseif ($response.error -eq "invalid_grant") {
    Write-Output "Invalid grant, please try again"
    $keepPooling = $false
  }
}

# az login --scope api://c99753b6-3f16-4251-bf2e-f4f9bf6a6e9f/api --allow-no-subscriptions --use-device-code
# az account get-access-token --scope api://c99753b6-3f16-4251-bf2e-f4f9bf6a6e9f/api --query accessToken -o tsv