[CmdletBinding()]
param
(
  [Parameter(Mandatory)]
  [string]$cgsProjectName,

  [string]$idpPort = "8399",

  [Parameter(Mandatory)]
  [string]$outDir

)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel

Write-Output "Setting up local IDP endpoint."
pwsh $root/src/tools/local-idp/deploy-local-idp.ps1 -port $idpPort
$idpEndpoint = "http://localhost:$idpPort"
$timeout = New-TimeSpan -Minutes 1
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
& {
  # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
  $PSNativeCommandUseErrorActionPreference = $false
  while ((curl -o /dev/null -w "%{http_code}" -s ${idpEndpoint}/ready) -ne "200") {
    Write-Host "Waiting for local IDP endpoint to be ready at ${idpEndpoint}/ready"
    Start-Sleep -Seconds 3
    if ($stopwatch.elapsed -gt $timeout) {
      # Re-run the command once to log its output.
      curl -k -s ${idpEndpoint}/ready
      throw "Hit timeout waiting for local IDP to be ready."
    }
  }
}

if ($env:CODESPACES -ne "true" -and $env:GITHUB_ACTIONS -ne "true") {
  $localIdpEndpoint = "http://host.docker.internal:$idpPort"
}
else {
  # 172.17.0.1: https://stackoverflow.com/questions/48546124/what-is-the-linux-equivalent-of-host-docker-internal
  $localIdpEndpoint = "http://172.17.0.1:$idpPort"
}

# Setting up IDP for user authentication using JWT.
curl --fail-with-body -s -X POST "$idpEndpoint/setissuerurl" -d `
  @"
{
  "url": "${localIdpEndpoint}/oidc"
}
"@ -H "content-type: application/json" | jq

Write-Output "Submitting set_jwt_issuer proposal for local IDP"
$proposalId = (az cleanroom governance proposal create --content `
    @"
{
  "actions": [ {
    "name": "set_jwt_issuer",
    "args": {
      "issuer": "${localIdpEndpoint}/oidc",
      "auto_refresh": false
    }
  }]
}
"@ --governance-client $cgsProjectName | jq -r '.proposalId')

az cleanroom governance proposal vote `
  --proposal-id $proposalId `
  --action accept `
  --governance-client $cgsProjectName

Write-Output "Submitting set_jwt_public_signing_keys proposal for local IDP"
$idpSigningKey = (curl --fail-with-body -s -X POST "$idpEndpoint/generatesigningkey" --silent | ConvertFrom-Json)
$idpSigningKey.pem.TrimEnd("`n") | Out-File "$outDir/local_idp_cert.pem"
$kid = $idpSigningKey.kid
$x5c = $idpSigningKey.x5c
$proposalId = (az cleanroom governance proposal create --content `
    @"
{
  "actions": [ {
    "name": "set_jwt_public_signing_keys",
    "args": {
      "issuer": "${localIdpEndpoint}/oidc",
      "jwks": {
        "keys": [
          {
            "kty": "RSA",
            "kid": "${kid}",
            "x5c": ["${x5c}"]
          }
        ]
      }
    }
  }]
}
"@ --governance-client $cgsProjectName | jq -r '.proposalId')

az cleanroom governance proposal vote `
  --proposal-id $proposalId `
  --action accept `
  --governance-client $cgsProjectName
Write-Output "Local IDP endpoint configured in CCF."