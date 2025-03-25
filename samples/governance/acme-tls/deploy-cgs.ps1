[CmdletBinding()]
param
(
   [string]
   [Parameter(Mandatory)]
   $ccfEndpoint,

   [string]
   [Parameter(Mandatory)]
   $signingCert,

   [string]
   [Parameter(Mandatory)]
   $signingKey,

   [switch]
   $NoBuild,

   [switch]
   $NoTest
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$build = "$root/build"

. $root/build/helpers.ps1
Import-Module $PSScriptRoot/../scripts/cgs.psm1 -Force -DisableNameChecking

pwsh $PSScriptRoot/remove-cgs.ps1

$sandbox_common = "$PSScriptRoot/sandbox_common"
mkdir -p $sandbox_common

Write-Output "Building cgs js app"
pwsh $build/build-governance-ccf-app.ps1 --output $sandbox_common/dist

if (!$NoBuild) {
   pwsh $build/cgs/build-cgs-client.ps1
   pwsh $build/ccr/build-ccr-governance.ps1
}

$port = "7090"
$env:ccfEndpoint = $ccfEndpoint
docker compose -f $PSScriptRoot/docker-compose.yml up -d --remove-orphans

# wait for cgsclient endpoint to be up
while ((curl -s  -o /dev/null -w "%{http_code}" http://localhost:$port/ready) -ne "200") {
   Write-Host "Waiting for cgs-client endpoint to be up"
   Start-Sleep -Seconds 5
}

# Setup cgs-client instance on port $port with member0 cert/key information so that we can invoke CCF
# APIs via it.
curl -sS -X POST localhost:$port/configure `
   -F SigningCertPemFile=@$sandbox_common/member0_cert.pem `
   -F SigningKeyPemFile=@$sandbox_common/member0_privk.pem `
   -F "CcfEndpoint=$ccfEndpoint"

Write-Host "Activating member."
Activate-Current-Member -port $port

Write-Output "Submitting set_constitution proposal"
$ccfConstitutionDir = "$root/src/ccf/ccf-provider-common/constitution"
$cgsConstitutionDir = "$root/src/governance/ccf-app/js/constitution"
$content = ""
Get-ChildItem $ccfConstitutionDir -Filter *.js | Foreach-Object { $content += Get-Content $_.FullName -Raw }
Get-ChildItem $cgsConstitutionDir -Filter *.js | Foreach-Object { $content += Get-Content $_.FullName -Raw }
$content = $content | ConvertTo-Json
$proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port/proposals/create -d `
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
curl -sS -X GET localhost:$port/proposals/$proposalId | jq

Write-Output "Accepting the set_constitution proposal"
curl -sS -X POST localhost:$port/proposals/$proposalId/ballots/vote_accept | jq

# Submit a set_js_runtime_options to enable exception logging
Write-Output "Submitting set_js_runtime_options proposal"
$proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port/proposals/create -d `
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
curl -sS -X POST localhost:$port/proposals/$proposalId/ballots/vote_accept | jq

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
      localhost:$port/proposals/create `
      --data-binary @$sandbox_common/set_js_app_proposal.json | jq -r '.proposalId')

Write-Output "Accepting the set_js_app proposal"
curl -sS -X POST localhost:$port/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Submitting open network proposal"
curl "$ccfEndpoint/node/network" -k --silent | jq -r .service_certificate > "${sandbox_common}/service_cert.pem"
$certContent = $(awk 'NF {sub(/\r/, ""); printf "%s\\n",$0;}' ${sandbox_common}/service_cert.pem)

$proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port/proposals/create -d `
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
curl -sS -X POST localhost:$port/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Waiting a bit to avoid FrontendNotOpen error"
sleep 3

if (!$NoTest) {
   $contractId = (New-Guid).ToString().Substring(0, 8)
   pwsh $PSScriptRoot/../initiate-set-contract-flow.ps1 -port $port -contractId $contractId -issuerUrl "${ccfEndpoint}/app/oidc"
}

Write-Output "Deployment successful. cgs-client container is listening on $port."