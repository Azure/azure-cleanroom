[CmdletBinding()]
param
(
  [Parameter(Mandatory)]
  [string]$cgsProjectName,

  [string]$idpPort = "8399",

  [string]$repo = "localhost:5000",

  [string]$tag = "latest",
    
  [Parameter(Mandatory)]
  [string]$outDir

)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel

Write-Output "Setting up local IDP endpoint."
pwsh $root/src/tools/local-idp/deploy-local-idp.ps1 -port $idpPort -repo $repo -tag $tag
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

if ($env:GITHUB_ACTIONS -eq "true") {
  # Using JOB_ID as hostname in GitHub Actions environment so that a shared CCF instance can 
  # have multiple local IDP instances registered with it with unique issuer names.
  $issuerEndpoint = "http://${env:JOB_ID}.com"
}
else {
  $issuerEndpoint = "http://local-idp.com"
}

# Setting up IDP for user authentication using JWT.
curl --fail-with-body -s -X POST "$idpEndpoint/setissuerurl" -d `
  @"
{
  "url": "${issuerEndpoint}/oidc"
}
"@ -H "content-type: application/json" | jq

Write-Output "Submitting set_jwt_issuer proposal for local IDP with issuer endpoint ${issuerEndpoint}/oidc"
$proposalId = (az cleanroom governance proposal create --content `
    @"
{
  "actions": [ {
    "name": "set_jwt_issuer",
    "args": {
      "issuer": "${issuerEndpoint}/oidc",
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
Write-Output "set_jwt_public_signing_keys proposal with kid: $kid"
$proposalId = (az cleanroom governance proposal create --content `
    @"
{
  "actions": [ {
    "name": "set_jwt_public_signing_keys",
    "args": {
      "issuer": "${issuerEndpoint}/oidc",
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