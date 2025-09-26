[CmdletBinding()]
param
(
    [switch]
    $NoBuild,

    [switch]
    $NoTest,

    [string]
    $initialMemberName = "member0"
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$build = "$root/build"
$port_member0 = "9002"

$ccfEndpointHeaderKey = "x-ms-ccf-endpoint"
$serviceCertHeaderKey = "x-ms-service-cert"

. $root/build/helpers.ps1

pwsh $PSScriptRoot/remove-cgs.ps1

$sandbox_common = "$PSScriptRoot/sandbox_common"
mkdir -p $sandbox_common

# Creating the initial member identity certificate to add into the consortium.
bash $root/samples/governance/keygenerator.sh --name $initialMemberName --gen-enc-key -o $sandbox_common

Write-Output "Building governance ccf app"
pwsh $build/build-governance-ccf-app.ps1 --output $sandbox_common/dist

if (!$NoBuild) {
    pwsh $build/ccf/build-ccf-runjs-app-sandbox.ps1
    pwsh $build/cgs/build-cgs-client.ps1
    pwsh $build/cgs/build-cgs-ui.ps1
}

$env:ccfImageTag = "latest"
$env:initialMemberName = $initialMemberName
docker compose -f $PSScriptRoot/docker-compose.yml up -d --remove-orphans

$ccfEndpoint = ""
if ($env:CODESPACES -ne "true" -and $env:GITHUB_ACTIONS -ne "true") {
    $ccfEndpoint = "https://host.docker.internal:9001"
}
else {
    # 172.17.0.1: https://stackoverflow.com/questions/48546124/what-is-the-linux-equivalent-of-host-docker-internal
    $ccfEndpoint = "https://172.17.0.1:9001"
}

# The node is not up yet and the service certificate will not be created until it returns 200.
& {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
    $PSNativeCommandUseErrorActionPreference = $false
    while ((curl -k -s  -o /dev/null -w "%{http_code}" $ccfEndpoint/node/network) -ne "200") {
        Write-Host "Waiting for ccf endpoint to be up"
        Start-Sleep -Seconds 3
    }
}

# Get the service cert so that this script can take governance actions.
$response = (curl "$ccfEndpoint/node/network" -k --silent | ConvertFrom-Json)
# Trimming an extra new-line character added to the cert.
$serviceCertStr = $response.service_certificate.TrimEnd("`n")
$serviceCertStr | Out-File "$sandbox_common/service_cert.pem"

$serviceCert = cat "$sandbox_common/service_cert.pem" | base64 -w 0

# wait for cgs-client endpoint to be up
& {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
    $PSNativeCommandUseErrorActionPreference = $false
    while ((curl -s  -o /dev/null -w "%{http_code}" http://localhost:$port_member0/ready) -ne "200") {
        Write-Host "Waiting for cgs-client endpoint to be up"
        Start-Sleep -Seconds 5
    }
}

# Setup cgs-client instance on port $port_member0 with member0 cert/key information so that we can invoke CCF
# APIs via it.
curl -sS -X POST localhost:$port_member0/configure `
    -F SigningCertPemFile=@$sandbox_common/member0_cert.pem `
    -F SigningKeyPemFile=@$sandbox_common/member0_privk.pem

Write-Output "Member status is Accepted. Activating member0..."
curl -sS -X POST localhost:$port_member0/members/statedigests/ack `
    --header "$($ccfEndpointHeaderKey): $ccfEndpoint" `
    --header "$($serviceCertHeaderKey): $serviceCert"

timeout 20 bash -c `
    "until curl -sS -X GET localhost:$port_member0/members --header '$($ccfEndpointHeaderKey): $ccfEndpoint' --header '$($serviceCertHeaderKey): $serviceCert' | jq -r '.value[].status' | grep Active > /dev/null; do echo Waiting for member to be in Active state...; sleep 5; done"
curl -sS -X GET localhost:$port_member0/members `
    --header "$($ccfEndpointHeaderKey): $ccfEndpoint" `
    --header "$($serviceCertHeaderKey): $serviceCert" | jq
Write-Output "Member status is now Active"

$memberId = (curl -s localhost:$port_member0/show `
        --header "$($ccfEndpointHeaderKey): $ccfEndpoint" `
        --header "$($serviceCertHeaderKey): $serviceCert" | jq -r '.memberId')
Write-Output "Submitting set_member_data proposal for member0"
$proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port_member0/proposals/create `
        --header "$($ccfEndpointHeaderKey): $ccfEndpoint" `
        --header "$($serviceCertHeaderKey): $serviceCert" -d `
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
curl -sS -X POST localhost:$port_member0/proposals/$proposalId/ballots/vote_accept `
    --header "$($ccfEndpointHeaderKey): $ccfEndpoint" `
    --header "$($serviceCertHeaderKey): $serviceCert" | jq

Write-Output "Submitting set_constitution proposal"
$ccfConstitutionDir = "$root/src/ccf/ccf-provider-common/constitution"
$cgsConstitutionDir = "$root/src/governance/ccf-app/js/constitution"
$content = ""
Get-ChildItem $ccfConstitutionDir -Filter *.js | Foreach-Object { $content += Get-Content $_.FullName -Raw }
Get-ChildItem $cgsConstitutionDir -Filter *.js | Foreach-Object { $content += Get-Content $_.FullName -Raw }
$content = $content | ConvertTo-Json
$proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port_member0/proposals/create `
        --header "$($ccfEndpointHeaderKey): $ccfEndpoint" `
        --header "$($serviceCertHeaderKey): $serviceCert" -d `
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
curl -sS -X GET localhost:$port_member0/proposals/$proposalId `
    --header "$($ccfEndpointHeaderKey): $ccfEndpoint" `
    --header "$($serviceCertHeaderKey): $serviceCert" | jq

# Submit a set_js_runtime_options to enable exception logging
Write-Output "Submitting set_js_runtime_options proposal"
$proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port_member0/proposals/create `
        --header "$($ccfEndpointHeaderKey): $ccfEndpoint" `
        --header "$($serviceCertHeaderKey): $serviceCert" -d `
        @"
{
  "actions": [{
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
curl -sS -X POST localhost:$port_member0/proposals/$proposalId/ballots/vote_accept `
    --header "$($ccfEndpointHeaderKey): $ccfEndpoint" `
    --header "$($serviceCertHeaderKey): $serviceCert" | jq

Write-Output "Submitting set_js_app proposal"
@"
{
  "actions": [{
     "name": "set_js_app",
     "args": {
        "bundle": $(Get-Content $sandbox_common/dist/bundle.json)
     }
   }]
}
"@ > $sandbox_common/set_js_app_proposal.json
$proposalId = (curl -sS -X POST -H "content-type: application/json" `
        localhost:$port_member0/proposals/create `
        --header "$($ccfEndpointHeaderKey): $ccfEndpoint" `
        --header "$($serviceCertHeaderKey): $serviceCert"`
        --data-binary @$sandbox_common/set_js_app_proposal.json | jq -r '.proposalId')

Write-Output "Accepting the set_js_app proposal as member0"
curl -sS -X POST localhost:$port_member0/proposals/$proposalId/ballots/vote_accept `
    --header "$($ccfEndpointHeaderKey): $ccfEndpoint" `
    --header "$($serviceCertHeaderKey): $serviceCert" | jq

Write-Output "Confirming that /jsapp/bundle endpoint value matches the proposed app bundle"
$canonical_bundle = curl -sS -X GET localhost:$port_member0/jsapp/bundle `
    --header "$($ccfEndpointHeaderKey): $ccfEndpoint" `
    --header "$($serviceCertHeaderKey): $serviceCert" | jq -S -c
$canonical_proposed_bundle = Get-Content $sandbox_common/dist/bundle.json | jq -S -c

if ($canonical_bundle -ne $canonical_proposed_bundle) {
    $canonical_bundle | Out-File $sandbox_common/canonical_bundle.json
    $canonical_proposed_bundle | Out-File $sandbox_common/canonical_proposed_bundle.json
    throw "Mismatch in proposed and reported JS app bundle. Compare $sandbox_common/canonical_bundle.json and $sandbox_common/canonical_proposed_bundle.json files to figure out the issue."
}

Write-Output "Submitting open network proposal"
$certContent = (Get-Content $sandbox_common/service_cert.pem -Raw).ReplaceLineEndings("\n")
$proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port_member0/proposals/create `
        --header "$($ccfEndpointHeaderKey): $ccfEndpoint" `
        --header "$($serviceCertHeaderKey): $serviceCert" -d `
        @"
{
  "actions": [{
     "name": "transition_service_to_open",
     "args": {
        "next_service_identity": "$certContent"
     }
   }]
}
"@ | jq -r '.proposalId')

Write-Output "Accepting the open network proposal as member0"
curl -sS -X POST localhost:$port_member0/proposals/$proposalId/ballots/vote_accept `
    --header "$($ccfEndpointHeaderKey): $ccfEndpoint" `
    --header "$($serviceCertHeaderKey): $serviceCert" | jq

Write-Output "Waiting a bit to avoid FrontendNotOpen error"
sleep 3

Write-Output "Deployment successful. cgs-client containers are listening on $port_member0."