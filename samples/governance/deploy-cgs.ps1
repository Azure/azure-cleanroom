[CmdletBinding()]
param
(
    [switch]
    $NoBuild,

    [switch]
    $NoTest
)

$root = git rev-parse --show-toplevel
$build = "$root/build"
$port = "9290"
$uiport = "6290"
$initialMemberName = "member0"

. $root/build/helpers.ps1

pwsh $PSScriptRoot/remove-cgs.ps1

$sandbox_common = "$PSScriptRoot/sandbox_common"
mkdir -p $sandbox_common

# Creating the initial member identity certificate to add into the consortium.
bash $root/samples/governance/keygenerator.sh --name $initialMemberName --gen-enc-key -o $sandbox_common

Write-Output "Building governance ccf app"
pwsh $build/build-governance-ccf-app.ps1 --output $sandbox_common/dist

if (!$NoBuild) {
    pwsh $build/build-ccf.ps1
    CheckLastExitCode
    pwsh $build/cgs/build-cgs-client.ps1
    CheckLastExitCode
    pwsh $build/cgs/build-cgs-ui.ps1
    CheckLastExitCode
    pwsh $build/ccr/build-ccr-governance.ps1
    CheckLastExitCode
}

$env:ccfImageTag = "js-virtual"
$env:initialMemberName = $initialMemberName
docker compose -f $PSScriptRoot/docker-compose.yml up -d --remove-orphans

$ccfEndpoint = ""
if ($env:GITHUB_ACTIONS -ne "true") {
    $ccfEndpoint = "https://host.docker.internal:9080"
}
else {
    # 172.17.0.1: https://stackoverflow.com/questions/48546124/what-is-the-linux-equivalent-of-host-docker-internal
    $ccfEndpoint = "https://172.17.0.1:9080"
}

# The node is not up yet and the service certificate will not be created until it returns 200.
while ((curl -k -s  -o '' -w "'%{http_code}'" $ccfEndpoint/node/network) -ne "'200'") {
    Write-Host "Waiting for ccf endpoint to be up"
    Start-Sleep -Seconds 3
}

# Get the service cert so that this script can take governance actions.
$response = (curl "$ccfEndpoint/node/network" -k --silent | ConvertFrom-Json)
# Trimming an extra new-line character added to the cert.
$serviceCertStr = $response.service_certificate.TrimEnd("`n")
$serviceCertStr | Out-File "$sandbox_common/service_cert.pem"

# wait for cgsclient endpoint to be up
while ((curl -s  -o '' -w "'%{http_code}'" http://localhost:$port/swagger/index.html) -ne "'200'") {
    Write-Host "Waiting for cgs-client endpoint to be up"
    Start-Sleep -Seconds 5
}

# Get the service cert so that this script can take governance actions.
$response = (curl "$ccfEndpoint/node/network" -k --silent | ConvertFrom-Json)
# Trimming an extra new-line character added to the cert.
$serviceCertStr = $response.service_certificate.TrimEnd("`n")
$serviceCertStr | Out-File "$sandbox_common/service_cert.pem"

# Setup cgs-client instance on port $port with member0 cert/key information so that we can invoke CCF
# APIs via it.
curl -sS -X POST localhost:$port/configure `
    -F SigningCertPemFile=@$sandbox_common/member0_cert.pem `
    -F SigningKeyPemFile=@$sandbox_common/member0_privk.pem `
    -F ServiceCertPemFile=@$sandbox_common/service_cert.pem `
    -F 'CcfEndpoint=https://ccf:8080'

# Wait for endpoints to be up by checking that an Accepted status member is reported.
timeout 20 bash -c `
    "until curl -sS -X GET localhost:$port/members | jq -r '.[].status' | grep Accepted > /dev/null; do echo Waiting for member to be in Accepted state...; sleep 5; done"
curl -sS -X GET localhost:$port/members | jq
CheckLastExitCode

Write-Output "Member status is Accepted. Activating member0..."
curl -sS -X POST localhost:$port/members/statedigests/ack
CheckLastExitCode

timeout 20 bash -c `
    "until curl -sS -X GET localhost:$port/members | jq -r '.[].status' | grep Active > /dev/null; do echo Waiting for member to be in Active state...; sleep 5; done"
curl -sS -X GET localhost:$port/members | jq
CheckLastExitCode
Write-Output "Member status is now Active"

$memberId = (curl -s localhost:$port/show | jq -r '.memberId')
Write-Output "Submitting set_member_data proposal for member0"
$proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port/proposals/create -d `
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
curl -sS -X POST localhost:$port/proposals/$proposalId/ballots/vote_accept | jq
CheckLastExitCode

Write-Output "Submitting set_constitution proposal"
$ccfConstitutionDir = "$root/src/ccf/ccf-provider-common/constitution"
$cgsConstitutionDir = "$root/src/governance/ccf-app/constitution"
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
CheckLastExitCode

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

Write-Output "Accepting the set_js_app proposal as member0"
curl -sS -X POST localhost:$port/proposals/$proposalId/ballots/vote_accept | jq
CheckLastExitCode

Write-Output "Confirming that /jsapp/bundle endpoint value matches the proposed app bundle"
$canonical_bundle = curl -sS -X GET localhost:$port/jsapp/bundle | jq -S -c
$canonical_proposed_bundle = Get-Content $sandbox_common/dist/bundle.json | jq -S -c

if ($canonical_bundle -ne $canonical_proposed_bundle) {
    $canonical_bundle | Out-File $sandbox_common/canonical_bundle.json
    $canonical_proposed_bundle | Out-File $sandbox_common/canonical_proposed_bundle.json
    throw "Mismatch in proposed and reported JS app bundle. Compare $sandbox_common/canonical_bundle.json and $sandbox_common/canonical_proposed_bundle.json files to figure out the issue."
}

Write-Output "Submitting open network proposal"
$certContent = (Get-Content $sandbox_common/service_cert.pem -Raw).ReplaceLineEndings("\n")
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
CheckLastExitCode

Write-Output "Waiting a bit to avoid FrontendNotOpen error"
sleep 3

if (!$NoTest) {
    pwsh $PSScriptRoot/initiate-set-contract-flow.ps1 -port $port
}

Write-Output "Deployment successful. cgs-client container is listening on $port. Open CGS UI at http://localhost:$uiport."