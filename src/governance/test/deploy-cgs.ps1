[CmdletBinding()]
param
(
  [switch]
  $NoBuild,

  [switch]
  $NoTest,

  [string]
  $initialMemberName = "member0",

  [ValidateSet('mcr', 'local', 'acr')]
  [string]$registry = "local",

  [string]$repo = "",

  [string]$tag = "latest"
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$build = "$root/build"
$port_member0 = "8290"
$port_member1 = "8291"
$port_member2 = "8292"
$port_local_idp = "8319"

. $root/build/helpers.ps1

pwsh $PSScriptRoot/remove-cgs.ps1

$sandbox_common = "$PSScriptRoot/sandbox_common"
mkdir -p $sandbox_common

# Creating the initial member identity certificate to add into the consortium.
bash $root/samples/governance/keygenerator.sh --name $initialMemberName --gen-enc-key -o $sandbox_common

Write-Output "Building governance ccf app"
pwsh $build/cgs/build-governance-ccf-app.ps1 --output $sandbox_common/dist

if ($registry -eq "acr") {
  Write-Output "Pulling images from registry: $repo with tag: $tag"
  $registryName = ($repo -split '\.')[0]
  az acr login -n $registryName
  $images = @(
    "ccf/app/run-js/sandbox",
    "cvm/cvm-attestation-verifier",
    "cgs-client",
    "cgs-ui",
    "ccr-governance",
    "ccr-governance-virtual",
    "local-idp"
  )
  foreach ($image in $images) {
    $sourceImage = "$repo/$image`:$tag"
    $destImage = "localhost:5000/$image`:latest"
    Write-Output "Pulling $sourceImage and tagging as $destImage"
    docker pull $sourceImage
    docker tag $sourceImage $destImage
  }
} elseif (!$NoBuild) {
  Write-Output "Building containers locally"
  pwsh $build/ccf/build-ccf-runjs-app-sandbox.ps1
  pwsh $build/cvm/build-cvm-attestation-verifier.ps1
  pwsh $build/cgs/build-cgs-client.ps1
  pwsh $build/cgs/build-cgs-ui.ps1
  pwsh $build/ccr/build-ccr-governance.ps1
  pwsh $build/ccr/build-ccr-governance-virtual.ps1
  pwsh $build/ccr/build-local-idp.ps1
}

$env:ccfImageTag = "latest"
$env:initialMemberName = $initialMemberName
$env:credentialsProxyUid = (id -u $env:USER)
$env:credentialsProxyGid = (id -g $env:USER)
docker compose -f $PSScriptRoot/docker-compose.yml up -d --remove-orphans

$ccfEndpoint = ""
$localIdpEndpoint = ""
if ($env:CODESPACES -ne "true" -and $env:GITHUB_ACTIONS -ne "true") {
  $ccfEndpoint = "https://host.docker.internal:8080"
  $localIdpEndpoint = "http://host.docker.internal:$port_local_idp"
}
else {
  # 172.17.0.1: https://stackoverflow.com/questions/48546124/what-is-the-linux-equivalent-of-host-docker-internal
  $ccfEndpoint = "https://172.17.0.1:8080"
  $localIdpEndpoint = "http://172.17.0.1:$port_local_idp"
}

# The node is not up yet and the service certificate will not be created until it returns 200.
& {
  # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
  $PSNativeCommandUseErrorActionPreference = $false
  $timeout = New-TimeSpan -Minutes 5
  $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
  $statusCode = (curl -k -s  -o /dev/null -w "%{http_code}" $ccfEndpoint/node/network)
  while ($statusCode -ne "200") {
    Write-Host "Waiting for ccf endpoint to be up at $ccfEndpoint, status code: $statusCode"
    Start-Sleep -Seconds 3
    if ($stopwatch.elapsed -gt $timeout) {
      throw "Hit timeout waiting for ccf endpoint to be up."
    }
    $statusCode = (curl -k -s  -o /dev/null -w "%{http_code}" $ccfEndpoint/node/network)
  }
}

# Get the service cert so that this script can take governance actions.
$response = (curl "$ccfEndpoint/node/network" -k --silent | ConvertFrom-Json)
# Trimming an extra new-line character added to the cert.
$serviceCertStr = $response.service_certificate.TrimEnd("`n")
$serviceCertStr | Out-File "$sandbox_common/service_cert.pem"

& {
  # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
  $PSNativeCommandUseErrorActionPreference = $false
  # wait for cgs-client endpoint to be up
  while ((curl -s  -o /dev/null -w "%{http_code}" http://localhost:$port_member0/ready) -ne "200") {
    Write-Host "Waiting for cgs-client endpoint to be up"
    Start-Sleep -Seconds 5
  }
}

# Get the service cert so that this script can take governance actions.
$response = (curl "$ccfEndpoint/node/network" -k --silent | ConvertFrom-Json)
# Trimming an extra new-line character added to the cert.
$serviceCertStr = $response.service_certificate.TrimEnd("`n")
$serviceCertStr | Out-File "$sandbox_common/service_cert.pem"

# Setup cgs-client instance on port $port_member0 with member0 cert/key information so that we can invoke CCF
# APIs via it.
curl -sS -X POST localhost:$port_member0/configure `
  -F SigningCertPemFile=@$sandbox_common/member0_cert.pem `
  -F SigningKeyPemFile=@$sandbox_common/member0_privk.pem `
  -F ServiceCertPemFile=@$sandbox_common/service_cert.pem `
  -F 'CcfEndpoint=https://test-ccf:8080'

# Wait for endpoints to be up by checking that an Accepted status member is reported.
timeout 20 bash -c `
  "until curl -sS -X GET localhost:$port_member0/members | jq -r '.value[].status' | grep Accepted > /dev/null; do echo Waiting for member to be in Accepted state...; sleep 5; done"

Write-Output "Member status is Accepted. Activating member0..."
curl -sS -X POST localhost:$port_member0/members/statedigests/ack

timeout 20 bash -c `
  "until curl -sS -X GET localhost:$port_member0/members | jq -r '.value[].status' | grep Active > /dev/null; do echo Waiting for member to be in Active state...; sleep 5; done"
curl -sS -X GET localhost:$port_member0/members | jq
Write-Output "Member status is now Active"

$memberId = (curl -s localhost:$port_member0/show | jq -r '.memberId')
Write-Output "Submitting set_member_data proposal for member0"
$proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port_member0/proposals/create -d `
    @"
{
  "actions": [{
     "name": "set_member_data",
     "args": {
       "member_id": "$memberId",
       "member_data": {
         "identifier": "member0"
       }
     }
   }]
}
"@ | jq -r '.proposalId')

Write-Output "Accepting the set_member_data proposal"
curl -sS -X POST localhost:$port_member0/proposals/$proposalId/ballots/vote_accept | jq

# Setting up IDP for user authentication using JWT.
Write-Output "Setting up local IDP endpoint"
curl --fail-with-body -X POST "http://localhost:$port_local_idp/setissuerurl" -d `
  @"
{
  "url": "${localIdpEndpoint}/oidc"
}
"@ -H "content-type: application/json" | jq

Write-Output "Submitting set_jwt_issuer proposal"
$proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port_member0/proposals/create -d `
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
"@ | jq -r '.proposalId')
curl -sS -X GET localhost:$port_member0/proposals/$proposalId | jq

Write-Output "Submitting set_jwt_public_signing_keys proposal"
$idpSigningKey = (curl --fail-with-body -X POST "http://localhost:$port_local_idp/generatesigningkey" --silent | ConvertFrom-Json)
$idpSigningKey.pem.TrimEnd("`n") | Out-File "$sandbox_common/local_idp_cert.pem"
$kid = $idpSigningKey.kid
$x5c = $idpSigningKey.x5c
Write-Output @"
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
"@ | Out-File "$sandbox_common/set_jwt_public_signing_keys.json"
$proposalId = (curl -sS --fail-with-body -X POST -H "content-type: application/json" `
    localhost:$port_member0/proposals/create -d "@$sandbox_common/set_jwt_public_signing_keys.json" | jq -r '.proposalId')
curl -sS -X GET localhost:$port_member0/proposals/$proposalId | jq

Write-Output "Submitting set_ca_cert_bundle proposal for trusted root CAs"
$proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port_member0/proposals/create -d `
    @"
{
  "actions": [
    {
      "name": "set_ca_cert_bundle",
      "args": {
        "name": "trusted_root_cas",
        "cert_bundle": "-----BEGIN CERTIFICATE-----\nMIIDrzCCApegAwIBAgIQCDvgVpBCRrGhdWrJWZHHSjANBgkqhkiG9w0BAQUFADBhMQswCQYDVQQG\nEwJVUzEVMBMGA1UEChMMRGlnaUNlcnQgSW5jMRkwFwYDVQQLExB3d3cuZGlnaWNlcnQuY29tMSAw\nHgYDVQQDExdEaWdpQ2VydCBHbG9iYWwgUm9vdCBDQTAeFw0wNjExMTAwMDAwMDBaFw0zMTExMTAw\nMDAwMDBaMGExCzAJBgNVBAYTAlVTMRUwEwYDVQQKEwxEaWdpQ2VydCBJbmMxGTAXBgNVBAsTEHd3\ndy5kaWdpY2VydC5jb20xIDAeBgNVBAMTF0RpZ2lDZXJ0IEdsb2JhbCBSb290IENBMIIBIjANBgkq\nhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA4jvhEXLeqKTTo1eqUKKPC3eQyaKl7hLOllsBCSDMAZOn\nTjC3U/dDxGkAV53ijSLdhwZAAIEJzs4bg7/fzTtxRuLWZscFs3YnFo97nh6Vfe63SKMI2tavegw5\nBmV/Sl0fvBf4q77uKNd0f3p4mVmFaG5cIzJLv07A6Fpt43C/dxC//AH2hdmoRBBYMql1GNXRor5H\n4idq9Joz+EkIYIvUX7Q6hL+hqkpMfT7PT19sdl6gSzeRntwi5m3OFBqOasv+zbMUZBfHWymeMr/y\n7vrTC0LUq7dBMtoM1O/4gdW7jVg/tRvoSSiicNoxBN33shbyTApOB6jtSj1etX+jkMOvJwIDAQAB\no2MwYTAOBgNVHQ8BAf8EBAMCAYYwDwYDVR0TAQH/BAUwAwEB/zAdBgNVHQ4EFgQUA95QNVbRTLtm\n8KPiGxvDl7I90VUwHwYDVR0jBBgwFoAUA95QNVbRTLtm8KPiGxvDl7I90VUwDQYJKoZIhvcNAQEF\nBQADggEBAMucN6pIExIK+t1EnE9SsPTfrgT1eXkIoyQY/EsrhMAtudXH/vTBH1jLuG2cenTnmCmr\nEbXjcKChzUyImZOMkXDiqw8cvpOp/2PV5Adg06O/nVsJ8dWO41P0jmP6P6fbtGbfYmbW0W5BjfIt\ntep3Sp+dWOIrWcBAI+0tKIJFPnlUkiaY4IBIqDfv8NZ5YBberOgOzW6sRBc4L0na4UU+Krk2U886\nUAb3LujEV0lsYSEY1QSteDwsOoBrp+uvFRTp2InBuThs4pFsiv9kuXclVzDAGySj4dzp30d8tbQk\nCAUw7C29C79Fv1C5qfPrmAESrciIxpg0X40KPMbp1ZWVbd4=\n-----END CERTIFICATE-----\n-----BEGIN CERTIFICATE-----\nMIIDjjCCAnagAwIBAgIQAzrx5qcRqaC7KGSxHQn65TANBgkqhkiG9w0BAQsFADBhMQswCQYDVQQG\nEwJVUzEVMBMGA1UEChMMRGlnaUNlcnQgSW5jMRkwFwYDVQQLExB3d3cuZGlnaWNlcnQuY29tMSAw\nHgYDVQQDExdEaWdpQ2VydCBHbG9iYWwgUm9vdCBHMjAeFw0xMzA4MDExMjAwMDBaFw0zODAxMTUx\nMjAwMDBaMGExCzAJBgNVBAYTAlVTMRUwEwYDVQQKEwxEaWdpQ2VydCBJbmMxGTAXBgNVBAsTEHd3\ndy5kaWdpY2VydC5jb20xIDAeBgNVBAMTF0RpZ2lDZXJ0IEdsb2JhbCBSb290IEcyMIIBIjANBgkq\nhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAuzfNNNx7a8myaJCtSnX/RrohCgiN9RlUyfuI2/Ou8jqJ\nkTx65qsGGmvPrC3oXgkkRLpimn7Wo6h+4FR1IAWsULecYxpsMNzaHxmx1x7e/dfgy5SDN67sH0NO\n3Xss0r0upS/kqbitOtSZpLYl6ZtrAGCSYP9PIUkY92eQq2EGnI/yuum06ZIya7XzV+hdG82MHauV\nBJVJ8zUtluNJbd134/tJS7SsVQepj5WztCO7TG1F8PapspUwtP1MVYwnSlcUfIKdzXOS0xZKBgyM\nUNGPHgm+F6HmIcr9g+UQvIOlCsRnKPZzFBQ9RnbDhxSJITRNrw9FDKZJobq7nMWxM4MphQIDAQAB\no0IwQDAPBgNVHRMBAf8EBTADAQH/MA4GA1UdDwEB/wQEAwIBhjAdBgNVHQ4EFgQUTiJUIBiV5uNu\n5g/6+rkS7QYXjzkwDQYJKoZIhvcNAQELBQADggEBAGBnKJRvDkhj6zHd6mcY1Yl9PMWLSn/pvtsr\nF9+wX3N3KjITOYFnQoQj8kVnNeyIv/iPsGEMNKSuIEyExtv4NeF22d+mQrvHRAiGfzZ0JFrabA0U\nWTW98kndth/Jsw1HKj2ZL7tcu7XUIOGZX1NGFdtom/DzMNU+MeKNhJ7jitralj41E6Vf8PlwUHBH\nQRFXGU7Aj64GxJUTFy8bJZ918rGOmaFvE7FBcf6IKshPECBV1/MUReXgRPTqh5Uykw7+U0b6LJ3/\niyK5S9kJRaTepLiaWN0bfVKfjllDiIGknibVb63dDcY3fe0Dkhvld1927jyNxF1WW6LZZm6zNTfl\nMrY=\n-----END CERTIFICATE-----\n-----BEGIN CERTIFICATE-----\nMIICPzCCAcWgAwIBAgIQBVVWvPJepDU1w6QP1atFcjAKBggqhkjOPQQDAzBhMQswCQYDVQQGEwJV\nUzEVMBMGA1UEChMMRGlnaUNlcnQgSW5jMRkwFwYDVQQLExB3d3cuZGlnaWNlcnQuY29tMSAwHgYD\nVQQDExdEaWdpQ2VydCBHbG9iYWwgUm9vdCBHMzAeFw0xMzA4MDExMjAwMDBaFw0zODAxMTUxMjAw\nMDBaMGExCzAJBgNVBAYTAlVTMRUwEwYDVQQKEwxEaWdpQ2VydCBJbmMxGTAXBgNVBAsTEHd3dy5k\naWdpY2VydC5jb20xIDAeBgNVBAMTF0RpZ2lDZXJ0IEdsb2JhbCBSb290IEczMHYwEAYHKoZIzj0C\nAQYFK4EEACIDYgAE3afZu4q4C/sLfyHS8L6+c/MzXRq8NOrexpu80JX28MzQC7phW1FGfp4tn+6O\nYwwX7Adw9c+ELkCDnOg/QW07rdOkFFk2eJ0DQ+4QE2xy3q6Ip6FrtUPOZ9wj/wMco+I+o0IwQDAP\nBgNVHRMBAf8EBTADAQH/MA4GA1UdDwEB/wQEAwIBhjAdBgNVHQ4EFgQUs9tIpPmhxdiuNkHMEWNp\nYim8S8YwCgYIKoZIzj0EAwMDaAAwZQIxAK288mw/EkrRLTnDCgmXc/SINoyIJ7vmiI1Qhadj+Z4y\n3maTD/HMsQmP3Wyr+mt/oAIwOWZbwmSNuJ5Q3KjVSaLtx9zRSX8XAbjIho9OjIgrqJqpisXRAL34\nVOKa5Vt8sycX\n-----END CERTIFICATE-----\n-----BEGIN CERTIFICATE-----\nMIIEPjCCAyagAwIBAgIESlOMKDANBgkqhkiG9w0BAQsFADCBvjELMAkGA1UEBhMC\nVVMxFjAUBgNVBAoTDUVudHJ1c3QsIEluYy4xKDAmBgNVBAsTH1NlZSB3d3cuZW50\ncnVzdC5uZXQvbGVnYWwtdGVybXMxOTA3BgNVBAsTMChjKSAyMDA5IEVudHJ1c3Qs\nIEluYy4gLSBmb3IgYXV0aG9yaXplZCB1c2Ugb25seTEyMDAGA1UEAxMpRW50cnVz\ndCBSb290IENlcnRpZmljYXRpb24gQXV0aG9yaXR5IC0gRzIwHhcNMDkwNzA3MTcy\nNTU0WhcNMzAxMjA3MTc1NTU0WjCBvjELMAkGA1UEBhMCVVMxFjAUBgNVBAoTDUVu\ndHJ1c3QsIEluYy4xKDAmBgNVBAsTH1NlZSB3d3cuZW50cnVzdC5uZXQvbGVnYWwt\ndGVybXMxOTA3BgNVBAsTMChjKSAyMDA5IEVudHJ1c3QsIEluYy4gLSBmb3IgYXV0\naG9yaXplZCB1c2Ugb25seTEyMDAGA1UEAxMpRW50cnVzdCBSb290IENlcnRpZmlj\nYXRpb24gQXV0aG9yaXR5IC0gRzIwggEiMA0GCSqGSIb3DQEBAQUAA4IBDwAwggEK\nAoIBAQC6hLZy254Ma+KZ6TABp3bqMriVQRrJ2mFOWHLP/vaCeb9zYQYKpSfYs1/T\nRU4cctZOMvJyig/3gxnQaoCAAEUesMfnmr8SVycco2gvCoe9amsOXmXzHHfV1IWN\ncCG0szLni6LVhjkCsbjSR87kyUnEO6fe+1R9V77w6G7CebI6C1XiUJgWMhNcL3hW\nwcKUs/Ja5CeanyTXxuzQmyWC48zCxEXFjJd6BmsqEZ+pCm5IO2/b1BEZQvePB7/1\nU1+cPvQXLOZprE4yTGJ36rfo5bs0vBmLrpxR57d+tVOxMyLlbc9wPBr64ptntoP0\njaWvYkxN4FisZDQSA/i2jZRjJKRxAgMBAAGjQjBAMA4GA1UdDwEB/wQEAwIBBjAP\nBgNVHRMBAf8EBTADAQH/MB0GA1UdDgQWBBRqciZ60B7vfec7aVHUbI2fkBJmqzAN\nBgkqhkiG9w0BAQsFAAOCAQEAeZ8dlsa2eT8ijYfThwMEYGprmi5ZiXMRrEPR9RP/\njTkrwPK9T3CMqS/qF8QLVJ7UG5aYMzyorWKiAHarWWluBh1+xLlEjZivEtRh2woZ\nRkfz6/djwUAFQKXSt/S1mja/qYh2iARVBCuch38aNzx+LaUa2NSJXsq9rD1s2G2v\n1fN2D807iDginWyTmsQ9v4IbZT+mD12q/OWyFcq1rca8PdCE6OoGcrBNOTJ4vz4R\nnAuknZoh8/CbCzB428Hch0P+vGOaysXCHMnHjf87ElgI5rY97HosTvuDls4MPGmH\nVHOkc8KT/1EQrBVUAdj8BbGJoX90g5pJ19xOe4pIb4tF9g==\n-----END CERTIFICATE-----\n-----BEGIN CERTIFICATE-----\nMIICWTCCAd+gAwIBAgIQZvI9r4fei7FK6gxXMQHC7DAKBggqhkjOPQQDAzBlMQswCQYDVQQGEwJV\nUzEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMTYwNAYDVQQDEy1NaWNyb3NvZnQgRUND\nIFJvb3QgQ2VydGlmaWNhdGUgQXV0aG9yaXR5IDIwMTcwHhcNMTkxMjE4MjMwNjQ1WhcNNDIwNzE4\nMjMxNjA0WjBlMQswCQYDVQQGEwJVUzEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMTYw\nNAYDVQQDEy1NaWNyb3NvZnQgRUNDIFJvb3QgQ2VydGlmaWNhdGUgQXV0aG9yaXR5IDIwMTcwdjAQ\nBgcqhkjOPQIBBgUrgQQAIgNiAATUvD0CQnVBEyPNgASGAlEvaqiBYgtlzPbKnR5vSmZRogPZnZH6\nthaxjG7efM3beaYvzrvOcS/lpaso7GMEZpn4+vKTEAXhgShC48Zo9OYbhGBKia/teQ87zvH2RPUB\neMCjVDBSMA4GA1UdDwEB/wQEAwIBhjAPBgNVHRMBAf8EBTADAQH/MB0GA1UdDgQWBBTIy5lycFIM\n+Oa+sgRXKSrPQhDtNTAQBgkrBgEEAYI3FQEEAwIBADAKBggqhkjOPQQDAwNoADBlAjBY8k3qDPlf\nXu5gKcs68tvWMoQZP3zVL8KxzJOuULsJMsbG7X7JNpQS5GiFBqIb0C8CMQCZ6Ra0DvpWSNSkMBaR\neNtUjGUBiudQZsIxtzm6uBoiB078a1QWIP8rtedMDE2mT3M=\n-----END CERTIFICATE-----\n-----BEGIN CERTIFICATE-----\nMIIFqDCCA5CgAwIBAgIQHtOXCV/YtLNHcB6qvn9FszANBgkqhkiG9w0BAQwFADBlMQswCQYDVQQG\nEwJVUzEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMTYwNAYDVQQDEy1NaWNyb3NvZnQg\nUlNBIFJvb3QgQ2VydGlmaWNhdGUgQXV0aG9yaXR5IDIwMTcwHhcNMTkxMjE4MjI1MTIyWhcNNDIw\nNzE4MjMwMDIzWjBlMQswCQYDVQQGEwJVUzEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9u\nMTYwNAYDVQQDEy1NaWNyb3NvZnQgUlNBIFJvb3QgQ2VydGlmaWNhdGUgQXV0aG9yaXR5IDIwMTcw\nggIiMA0GCSqGSIb3DQEBAQUAA4ICDwAwggIKAoICAQDKW76UM4wplZEWCpW9R2LBifOZNt9GkMml\n7Xhqb0eRaPgnZ1AzHaGm++DlQ6OEAlcBXZxIQIJTELy/xztokLaCLeX0ZdDMbRnMlfl7rEqUrQ7e\nS0MdhweSE5CAg2Q1OQT85elss7YfUJQ4ZVBcF0a5toW1HLUX6NZFndiyJrDKxHBKrmCk3bPZ7Pw7\n1VdyvD/IybLeS2v4I2wDwAW9lcfNcztmgGTjGqwu+UcF8ga2m3P1eDNbx6H7JyqhtJqRjJHTOoI+\ndkC0zVJhUXAoP8XFWvLJjEm7FFtNyP9nTUwSlq31/niol4fX/V4ggNyhSyL71Imtus5Hl0dVe49F\nyGcohJUcaDDv70ngNXtk55iwlNpNhTs+VcQor1fznhPbRiefHqJeRIOkpcrVE7NLP8TjwuaGYaRS\nMLl6IE9vDzhTyzMMEyuP1pq9KsgtsRx9S1HKR9FIJ3Jdh+vVReZIZZ2vUpC6W6IYZVcSn2i51BVr\nlMRpIpj0M+Dt+VGOQVDJNE92kKz8OMHY4Xu54+OU4UZpyw4KUGsTuqwPN1q3ErWQgR5WrlcihtnJ\n0tHXUeOrO8ZV/R4O03QK0dqq6mm4lyiPSMQH+FJDOvTKVTUssKZqwJz58oHhEmrARdlns87/I6KJ\nClTUFLkqqNfs+avNJVgyeY+QW5g5xAgGwax/Dj0ApQIDAQABo1QwUjAOBgNVHQ8BAf8EBAMCAYYw\nDwYDVR0TAQH/BAUwAwEB/zAdBgNVHQ4EFgQUCctZf4aycI8awznjwNnpv7tNsiMwEAYJKwYBBAGC\nNxUBBAMCAQAwDQYJKoZIhvcNAQEMBQADggIBAKyvPl3CEZaJjqPnktaXFbgToqZCLgLNFgVZJ8og\n6Lq46BrsTaiXVq5lQ7GPAJtSzVXNUzltYkyLDVt8LkS/gxCP81OCgMNPOsduET/m4xaRhPtthH80\ndK2Jp86519efhGSSvpWhrQlTM93uCupKUY5vVau6tZRGrox/2KJQJWVggEbbMwSubLWYdFQl3JPk\n+ONVFT24bcMKpBLBaYVu32TxU5nhSnUgnZUP5NbcA/FZGOhHibJXWpS2qdgXKxdJ5XbLwVaZOjex\n/2kskZGT4d9Mozd2TaGf+G0eHdP67Pv0RR0Tbc/3WeUiJ3IrhvNXuzDtJE3cfVa7o7P4NHmJweDy\nAmH3pvwPuxwXC65B2Xy9J6P9LjrRk5Sxcx0ki69bIImtt2dmefU6xqaWM/5TkshGsRGRxpl/j8nW\nZjEgQRCHLQzWwa80mMpkg/sTV9HB8Dx6jKXB/ZUhoHHBk2dxEuqPiAppGWSZI1b7rCoucL5mxAyE\n7+WL85MB+GqQk2dLsmijtWKP6T+MejteD+eMuMZ87zf9dOLITzNy4ZQ5bb0Sr74MTnB8G2+NszKT\nc0QWbej09+CVgI+WXTik9KveCjCHk9hNAHFiRSdLOkKEW39lt2c0Ui2cFmuqqNh7o0JMcccMyj6D\n5KbvtwEwXlGjefVwaaZBRA+GsCyRxj3qrg+E\n-----END CERTIFICATE-----\n"
      }
    }
  ]
}
"@ | jq -r '.proposalId')
curl -sS -X GET localhost:$port_member0/proposals/$proposalId | jq

Write-Output "Submitting set_jwt_issuer proposal for sts.windows.net"
$proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port_member0/proposals/create -d `
    @"
{
  "actions": [
    {
      "name": "set_jwt_issuer",
      "args": {
        "issuer": "https://sts.windows.net/72f988bf-86f1-41af-91ab-2d7cd011db47/",
        "ca_cert_bundle_name": "trusted_root_cas",
        "auto_refresh": true
      }
    }
  ]
}
"@ | jq -r '.proposalId')
curl -sS -X GET localhost:$port_member0/proposals/$proposalId | jq

Write-Output "Submitting set_jwt_issuer proposal for login.microsoftonline.com"
$proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port_member0/proposals/create -d `
    @"
{
  "actions": [
    {
      "name": "set_jwt_issuer",
      "args": {
        "issuer": "https://login.microsoftonline.com/common/v2.0",
        "ca_cert_bundle_name": "trusted_root_cas",
        "auto_refresh": true
      }
    }
  ]
}
"@ | jq -r '.proposalId')
curl -sS -X GET localhost:$port_member0/proposals/$proposalId | jq

Write-Output "Submitting set_constitution proposal"
$ccfConstitutionDir = "$root/src/ccf/ccf-provider-common/constitution"
$cgsConstitutionDir = "$root/src/governance/ccf-app/js/constitution"
$content = ""
Get-ChildItem $ccfConstitutionDir -Filter *.js | Foreach-Object { $content += Get-Content $_.FullName -Raw }
Get-ChildItem $cgsConstitutionDir -Filter *.js | Foreach-Object { $content += Get-Content $_.FullName -Raw }
$content = $content | ConvertTo-Json
$proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port_member0/proposals/create -d `
    @"
{
  "actions": [{
     "name": "set_constitution",
     "args": {
       "constitution": $content
      }
  }]
}
"@ | jq -r '.proposalId')
curl -sS -X GET localhost:$port_member0/proposals/$proposalId | jq

# Adding a second member (member1) into the consortium.
bash $root/samples/governance/keygenerator.sh --name member1 -o $sandbox_common

Write-Output "Submitting set_member proposal"
$certContent = (Get-Content $sandbox_common/member1_cert.pem -Raw).ReplaceLineEndings("\n")
$proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port_member0/proposals/create -d `
    @"
{
  "actions": [ {
    "name": "set_member",
    "args": {
      "cert": "$certContent",
      "member_data": {
        "tenantId": "72f988bf-86f1-41af-91ab-2d7cd011db47",
        "identifier": "member1"
      }
    }
  }]
}
"@ | jq -r '.proposalId')

Write-Output "Accepting the set_member proposal"
curl -sS -X POST localhost:$port_member0/proposals/$proposalId/ballots/vote_accept | jq

# Setup cgs-client on port $port_member1 with member1 cert/key information so that we can invoke CCF APIs via it.
curl -sS -X POST localhost:$port_member1/configure `
  -F SigningCertPemFile=@$sandbox_common/member1_cert.pem `
  -F SigningKeyPemFile=@$sandbox_common/member1_privk.pem `
  -F ServiceCertPemFile=@$sandbox_common/service_cert.pem `
  -F 'CcfEndpoint=https://test-ccf:8080'

curl -sS -X GET localhost:$port_member1/members | jq
Write-Output "Member1 status is Accepted. Activating member1..."
curl -sS -X POST localhost:$port_member1/members/statedigests/ack

# Adding a third member (member2) into the consortium.
bash $root/samples/governance/keygenerator.sh --name member2 -o $sandbox_common

Write-Output "Submitting set_member proposal"
$certContent = (Get-Content $sandbox_common/member2_cert.pem -Raw).ReplaceLineEndings("\n")
$proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port_member0/proposals/create -d `
    @"
{
  "actions": [ {
    "name": "set_member",
    "args": {
      "cert": "$certContent",
      "member_data": {
        "tenantId": "72f988bf-86f1-41af-91ab-2d7cd011db47",
        "identifier": "member2"
      }
    }
  }]
}
"@ | jq -r '.proposalId')

Write-Output "Accepting the set_member proposal as member0"
curl -sS -X POST localhost:$port_member0/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Accepting the set_member proposal as member1"
curl -sS -X POST localhost:$port_member1/proposals/$proposalId/ballots/vote_accept | jq

# Setup cgs-client on port $port_member2 with member2 cert/key information so that we can invoke CCF APIs via it.
curl -sS -X POST localhost:$port_member2/configure `
  -F SigningCertPemFile=@$sandbox_common/member2_cert.pem `
  -F SigningKeyPemFile=@$sandbox_common/member2_privk.pem `
  -F ServiceCertPemFile=@$sandbox_common/service_cert.pem `
  -F 'CcfEndpoint=https://test-ccf:8080'

curl -sS -X GET localhost:$port_member1/members | jq
Write-Output "Member2 status is Accepted. Activating member2..."
curl -sS -X POST localhost:$port_member2/members/statedigests/ack

# Submit a set_js_runtime_options to enable exception logging
Write-Output "Submitting set_js_runtime_options proposal"
$proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port_member0/proposals/create -d `
    @"
{
  "actions": [ {
    "name": "set_js_runtime_options",
    "args": {
      "max_heap_bytes": 104857600,
      "max_stack_bytes": 1048576,
      "max_execution_time_ms": 1000,
      "log_exception_details": true,
      "return_exception_details": true
    }
  }]
}
"@ | jq -r '.proposalId')

Write-Output "Accepting the set_js_runtime_options proposal as member0"
curl -sS -X POST localhost:$port_member0/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Accepting the set_js_runtime_options proposal as member1"
curl -sS -X POST localhost:$port_member1/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Accepting the set_js_runtime_options proposal as member2"
curl -sS -X POST localhost:$port_member2/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Submitting set_js_app proposal"
@"
{
  "actions": [ {
    "name": "set_js_app",
    "args": {
      "bundle": $(Get-Content $sandbox_common/dist/bundle.json)
    }
  }]
}
"@ > $sandbox_common/set_js_app_proposal.json
$proposalId = (curl --fail-with-body -sS -X POST -H "content-type: application/json" `
    localhost:$port_member0/proposals/create `
    --data-binary @$sandbox_common/set_js_app_proposal.json | jq -r '.proposalId')

Write-Output "Accepting the set_js_app proposal as member0"
curl -sS -X POST localhost:$port_member0/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Accepting the set_js_app proposal as member1"
curl -sS -X POST localhost:$port_member1/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Accepting the set_js_app proposal as member2"
curl -sS -X POST localhost:$port_member2/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Confirming that /jsapp/bundle endpoint value matches the proposed app bundle"
$canonical_bundle = curl -sS -X GET localhost:$port_member0/jsapp/bundle | jq -S -c
$canonical_proposed_bundle = Get-Content $sandbox_common/dist/bundle.json | jq -S -c

if ($canonical_bundle -ne $canonical_proposed_bundle) {
  $canonical_bundle | Out-File $sandbox_common/canonical_bundle.json
  $canonical_proposed_bundle | Out-File $sandbox_common/canonical_proposed_bundle.json
  Write-Output "diff output:"
  diff $sandbox_common/canonical_bundle.json $sandbox_common/canonical_proposed_bundle.json
  throw "Mismatch in proposed and reported JS app bundle. Compare $sandbox_common/canonical_bundle.json and $sandbox_common/canonical_proposed_bundle.json files to figure out the issue."
}

Write-Output "Submitting open network proposal"
$certContent = (Get-Content $sandbox_common/service_cert.pem -Raw).ReplaceLineEndings("\n")
$proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port_member0/proposals/create -d `
    @"
{
  "actions": [ {
    "name": "transition_service_to_open",
    "args": {
      "next_service_identity": "$certContent"
    }
  }]
}
"@ | jq -r '.proposalId')

Write-Output "Accepting the open network proposal as member0"
curl -sS -X POST localhost:$port_member0/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Accepting the open network proposal as member1"
curl -sS -X POST localhost:$port_member1/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Accepting the open network proposal as member2"
curl -sS -X POST localhost:$port_member2/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Waiting on node/ready/app to avoid FrontendNotOpen error"
& {
  # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
  $PSNativeCommandUseErrorActionPreference = $false
  while ((curl -s  -o /dev/null -w "%{http_code}" http://localhost:$port_member0/node/ready/app) -ne "204") {
    Write-Host "Waiting for node/ready/app to be up"
    Start-Sleep -Seconds 3
  }
}

if (!$NoTest) {
  pwsh $PSScriptRoot/initiate-set-contract-flow.ps1
}

Write-Output "Deployment successful. cgs-client containers are listening on $port_member0, $port_member1 & $port_member2."