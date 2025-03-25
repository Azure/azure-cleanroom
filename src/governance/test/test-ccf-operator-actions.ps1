[CmdletBinding()]
param
(
    [switch]
    $NoBuild,

    [switch]
    $WithWorkaround
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$build = "$root/build"

$port_member0 = "8290"
$port_member1 = "8291"
$port_member2 = "8292"
$port_ccf_operator = "8299"
$sandbox_common = "$PSScriptRoot/sandbox_common"

. $root/build/helpers.ps1

if (!$NoBuild) {
    pwsh $build/build-test_ccf_operator_actions.ps1
}

# Adding the ccf operator member into the consortium.
if (!(Test-Path -Path $sandbox_common/ccf_operator_cert.pem)) {
    bash $root/samples/governance/keygenerator.sh --name ccf_operator -o $sandbox_common
}

Write-Output "Submitting set_member proposal for ccf_operator"
$certContent = (Get-Content $sandbox_common/ccf_operator_cert.pem -Raw).ReplaceLineEndings("\n")
$proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port_member0/proposals/create -d `
        @"
{
  "actions": [{
     "name": "set_member",
     "args": {
       "cert": "$certContent",
       "member_data": {
        "isOperator": true
       }
     }
   }]
}
"@ | jq -r '.proposalId')

Write-Output "Accepting the set_member proposal as member0"
curl -sS -X POST localhost:$port_member0/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Accepting the set_member proposal as member1"
curl -sS -X POST localhost:$port_member1/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Accepting the set_member proposal as member2"
curl -sS -X POST localhost:$port_member2/proposals/$proposalId/ballots/vote_accept | jq

# Setup cgs-client on port $port_ccfOpeator with ccf_operator cert/key information so that we can invoke CCF APIs via it.
curl -sS -X POST localhost:$port_ccf_operator/configure `
    -F SigningCertPemFile=@$sandbox_common/ccf_operator_cert.pem `
    -F SigningKeyPemFile=@$sandbox_common/ccf_operator_privk.pem `
    -F ServiceCertPemFile=@$sandbox_common/service_cert.pem `
    -F 'CcfEndpoint=https://test-ccf:8080'

Write-Output "ccf_operator status is Accepted. Activating ccf_operator..."
curl -sS -X POST localhost:$port_ccf_operator/members/statedigests/ack
curl -sS -X GET localhost:$port_ccf_operator/members | jq

$testcontainer = "test_ccf_operator_actions"
$successMessage = "Total tests:10, Passed:10, Failed:0, Test coverage:100.00%"

if ($WithWorkaround) {
    Write-Output "Workaround: Replacing -cacert argument with -k in test_operator_actions.sh."
    sed -i 's/--cacert $certificate_dir\/service_cert.pem/-k/g' $root/src/ccf/tests/test_operator_actions.sh
}

# Run the test and look for the expected success message in the container logs.
docker rm --force $testcontainer 1>$null 2>$null
docker run --name $testcontainer `
    --network host `
    -t `
    -v ${root}:/app `
    -v ${sandbox_common}:/app/sandbox_common `
    -w /app/src/ccf/tests `
    $testcontainer -c `
    "/app/src/ccf/tests/test_operator_actions.sh --address https://localhost:8080 --signing-cert /app/sandbox_common/ccf_operator_cert.pem --signing-key /app/sandbox_common/ccf_operator_privk.pem"

docker logs $testcontainer | grep $successMessage 1>$null
if ($LASTEXITCODE -gt 0) {
    throw "ccf test operator actions failed!"
}