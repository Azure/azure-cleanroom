[CmdletBinding()]
param
(
  [switch]
  $NoBuild,

  [string]
  $initialMemberName = "member0"
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$build = "$root/build"
$port_member0 = "8490"
$port_member1 = "8491"
$port_member2 = "8492"

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
if ($env:GITHUB_ACTIONS -ne "true") {
  $ccfEndpoint = "https://host.docker.internal:8480"
}
else {
  # 172.17.0.1: https://stackoverflow.com/questions/48546124/what-is-the-linux-equivalent-of-host-docker-internal
  $ccfEndpoint = "https://172.17.0.1:8480"
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
  -F 'CcfEndpoint=https://roletest-ccf:8080'

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
  "actions": [{
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
  -F 'CcfEndpoint=https://roletest-ccf:8080'

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
  "actions": [{
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
  -F 'CcfEndpoint=https://roletest-ccf:8080'

curl -sS -X GET localhost:$port_member1/members | jq
Write-Output "Member2 status is Accepted. Activating member2..."
curl -sS -X POST localhost:$port_member2/members/statedigests/ack

# Submit a set_js_runtime_options to enable exception logging
Write-Output "Submitting set_js_runtime_options proposal"
$proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port_member0/proposals/create -d `
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
curl -sS -X POST localhost:$port_member0/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Accepting the set_js_runtime_options proposal as member1"
curl -sS -X POST localhost:$port_member1/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Accepting the set_js_runtime_options proposal as member2"
curl -sS -X POST localhost:$port_member2/proposals/$proposalId/ballots/vote_accept | jq

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
    --data-binary @$sandbox_common/set_js_app_proposal.json | jq -r '.proposalId')

Write-Output "Accepting the set_js_app proposal as member0"
curl -sS -X POST localhost:$port_member0/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Accepting the set_js_app proposal as member1"
curl -sS -X POST localhost:$port_member1/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Accepting the set_js_app proposal as member2"
curl -sS -X POST localhost:$port_member2/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Submitting open network proposal"
$certContent = (Get-Content $sandbox_common/service_cert.pem -Raw).ReplaceLineEndings("\n")
$proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port_member0/proposals/create -d `
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
curl -sS -X POST localhost:$port_member0/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Accepting the open network proposal as member1"
curl -sS -X POST localhost:$port_member1/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Accepting the open network proposal as member2"
curl -sS -X POST localhost:$port_member2/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Waiting a bit to avoid FrontendNotOpen error"
sleep 3

Write-Output "Deployment successful. cgs-client containers are listening on $port_member0, $port_member1 & $port_member2."