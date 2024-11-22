[CmdletBinding()]
param
(
    [switch]
    $NoBuild,

    [switch]
    $NoTest,

    [string]
    $initialMemberName = "member0",

    [string]
    $ccfProjectName = "governance-ccf",

    [string]
    $projectName = "9290-cli",

    [string]
    $outDir = "",

    [ValidateSet('mcr', 'local', 'acr')]
    [string]$registry = "local",

    [string]
    $ccfEndpoint = ""
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$build = "$root/build"

. $root/build/helpers.ps1

if ($registry -eq "local") {
    if (!$NoBuild) {
        pwsh $build/build-azcliext-cleanroom.ps1
        CheckLastExitCode
        pwsh $build/build-ccf.ps1
        CheckLastExitCode
        pwsh $build/cgs/build-cgs-client.ps1
        CheckLastExitCode
        pwsh $build/cgs/build-cgs-ui.ps1
        CheckLastExitCode
    }
}
else {
    if (!$NoBuild) {
        pwsh $build/build-ccf.ps1

        # Install clean room extension that corresponds to the MCR images.
        $version = (az extension show --name cleanroom --query "version" --output tsv 2>$null)
        if ($version -ne "0.0.1") {
            Write-Host "Installing az cleanroom cli"
            az extension remove --name cleanroom 2>$null
            az extension add `
                --allow-preview true `
                --source https://cleanroomazcli.blob.core.windows.net/azcli/cleanroom-0.0.7-py2.py3-none-any.whl -y
        }
        else {
            Write-Host "az cleanroom cli version: $version already installed."
        }
    }
}

if ($ccfEndpoint -eq "") {
    pwsh $PSScriptRoot/remove-cgs.ps1 -ccfProjectName $ccfProjectName -projectName $projectName
}

if ($outDir -eq "") {
    $sandbox_common = "$PSScriptRoot/sandbox_common"
    mkdir -p $sandbox_common
}
else {
    $sandbox_common = $outDir
}

# Create registry container unless it already exists.
$reg_name = "ccf-registry"
$reg_port = "5000"
$registryImage = "registry:2.7"
if ($env:GITHUB_ACTIONS -eq "true") {
    $registryImage = "cleanroombuild.azurecr.io/registry:2.7"
}

& {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
    $PSNativeCommandUseErrorActionPreference = $false
    $registryState = docker inspect -f '{{.State.Running}}' "${reg_name}" 2>$null
    if ($registryState -ne "true") {
        docker run -d --restart=always -p "127.0.0.1:${reg_port}:5000" --name "${reg_name}" $registryImage
    }
}

if ($ccfEndpoint -eq "") {
    & {
        # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
        # Creating the initial member identity certificate to add into the consortium.
        $PSNativeCommandUseErrorActionPreference = $false
        az cleanroom governance member keygenerator-sh | `
            bash -s -- --name $initialMemberName --gen-enc-key --out $sandbox_common
    }

    $env:ccfImageTag = "js-virtual"
    $env:initialMemberName = $initialMemberName
    $env:cgs_sandbox_common = $sandbox_common
    docker compose -f $PSScriptRoot/docker-compose.yml -p $ccfProjectName up -d --remove-orphans

    $ccfEndpoint = ""
    if ($env:GITHUB_ACTIONS -ne "true") {
        $ccfEndpoint = "https://host.docker.internal:9081"
    }
    else {
        # 172.17.0.1: https://stackoverflow.com/questions/48546124/what-is-the-linux-equivalent-of-host-docker-internal
        $ccfEndpoint = "https://172.17.0.1:9081"
    }
}

# The node is not up yet and the service certificate will not be created until it returns 200.
& {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
    $PSNativeCommandUseErrorActionPreference = $false
    while ((curl -k -s  -o '' -w "'%{http_code}'" $ccfEndpoint/node/network) -ne "'200'") {
        Write-Host "Waiting for ccf endpoint to be up"
        Start-Sleep -Seconds 3
    }
}
# Get the service cert so that this script can take governance actions.
$response = (curl "$ccfEndpoint/node/network" -k --silent | ConvertFrom-Json)
# Trimming an extra new-line character added to the cert.
$serviceCertStr = $response.service_certificate.TrimEnd("`n")
$serviceCertStr | Out-File "$sandbox_common/service_cert.pem"

# Push cgs-client/ui/constitution/jsapp images if local registry is to be used for CGS.
$localTag = ""
if ($registry -eq "local") {
    $localTag = "100.$(Get-Date -UFormat %s)"
    $localTag | Out-File $sandbox_common/local-registry-tag.txt
    $server = "localhost:$reg_port"
    $orasImage = "ghcr.io/oras-project/oras:v1.2.0"

    # Push the images.
    docker tag cgs-client:latest $server/cgs-client:$localTag
    docker push $server/cgs-client:$localTag
    docker tag cgs-ui:latest $server/cgs-ui:$localTag
    docker push $server/cgs-ui:$localTag

    $client_digest = docker inspect --format='{{index .RepoDigests 0}}' $server/cgs-client:$localTag
    $client_digest = $client_digest.Substring($client_digest.Length - 71, 71)
    $client_digest_no_prefix = $client_digest.Substring(7, $client_digest.Length - 7)
    $ui_digest = docker inspect --format='{{index .RepoDigests 0}}' $server/cgs-ui:$localTag
    $ui_digest = $ui_digest.Substring($ui_digest.Length - 71, 71)
    $ui_digest_no_prefix = $ui_digest.Substring(7, $ui_digest.Length - 7)

    @"
cgs-client:
  version: "$localTag"
  image: $server/cgs-client@$client_digest
"@ | Out-File $sandbox_common/cgs-client-version.yaml

    # Push the version document for the current image and also tag the same as latest.
    docker run --rm --network host -v ${sandbox_common}/cgs-client-version.yaml:/workspace/version.yaml `
        $orasImage push `
        $server/versions/cgs-client:"$client_digest_no_prefix,latest" `
        ./version.yaml

    @"
cgs-ui:
  version: "$localTag"
  image: $server/cgs-ui@$ui_digest
"@ | Out-File $sandbox_common/cgs-ui-version.yaml

    # Push the version document for the current image and also tag the same as latest.
    docker run --rm --network host -v ${sandbox_common}/cgs-ui-version.yaml:/workspace/version.yaml `
        $orasImage push `
        $server/versions/cgs-ui:"$ui_digest_no_prefix,latest" `
        ./version.yaml

    $env:AZCLI_CGS_CLIENT_IMAGE = "$server/cgs-client:$localTag"
    $env:AZCLI_CGS_UI_IMAGE = "$server/cgs-ui:$localTag"

    pwsh $build/cgs/build-cgs-ccf-artefacts.ps1 -repo $server -tag $localTag -push -outDir $sandbox_common

    $constitution_image_digest = docker run --rm --network host `
        $orasImage resolve $server/cgs-constitution:$localTag
    $bundle_image_digest = docker run --rm --network host `
        $orasImage resolve $server/cgs-js-app:$localTag

    $env:AZCLI_CGS_CONSTITUTION_IMAGE = "$server/cgs-constitution@$constitution_image_digest"
    $env:AZCLI_CGS_JSAPP_IMAGE = "$server/cgs-js-app@$bundle_image_digest"

    $env:AZCLI_CLEANROOM_VERSIONS_REGISTRY = $server
}

# Setup cgs-client instance on port $port with member cert/key information so that we can invoke CCF
# APIs via it.
az cleanroom governance client deploy `
    --ccf-endpoint $ccfEndpoint `
    --signing-key $sandbox_common/${initialMemberName}_privk.pem `
    --signing-cert $sandbox_common/${initialMemberName}_cert.pem `
    --service-cert $sandbox_common/service_cert.pem `
    --name $projectName

$port = az cleanroom governance client show-deployment `
    --name $projectName `
    --query 'ports."cgs-client"' `
    --output tsv

# wait for cgsclient endpoint to be up.
& {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
    $PSNativeCommandUseErrorActionPreference = $false
    while ((curl -s  -o '' -w "'%{http_code}'" http://localhost:$port/swagger/index.html) -ne "'200'") {
        Write-Host "Waiting for cgs-client endpoint to be up"
        Start-Sleep -Seconds 3
    }
}

Write-Output "Activating $initialMemberName..."
az cleanroom governance member activate --governance-client $projectName
CheckLastExitCode

timeout 20 bash -c `
    "until az cleanroom governance member show --governance-client $projectName | jq -r '.[].status' | grep Active > /dev/null; do echo Waiting for member to be in Active state...; sleep 5; done"
az cleanroom governance member show --governance-client $projectName | jq
CheckLastExitCode
Write-Output "Member status is now Active"

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

Write-Output "Accepting the open network proposal as $initialMemberName"
curl -sS -X POST localhost:$port/proposals/$proposalId/ballots/vote_accept | jq
CheckLastExitCode

Write-Output "Waiting a bit to avoid FrontendNotOpen error"
sleep 3

az cleanroom governance service deploy --governance-client $projectName

$memberId = (az cleanroom governance client show --name $projectName --query "memberId" --output tsv)
Write-Output "Submitting set_member_data proposal for $initialMemberName"
$proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port/proposals/create -d `
        @"
{
  "actions": [{
     "name": "set_member_data",
     "args": {
       "member_id": "$memberId",
       "member_data": {
         "identifier": "$initialMemberName"
       }
     }
   }]
}
"@ | jq -r '.proposalId')

Write-Output "Accepting the set_member_data proposal"
curl -sS -X POST localhost:$port/proposals/$proposalId/ballots/vote_accept | jq
CheckLastExitCode

if (!$NoTest) {
    pwsh $PSScriptRoot/initiate-set-contract-flow.ps1 -projectName $projectName -issuerUrl "$ccfEndpoint/app/oidc"
    pwsh $PSScriptRoot/initiate-service-upgrade-flow.ps1 -projectName $projectName -version $localTag
    pwsh $PSScriptRoot/initiate-client-upgrade-flow.ps1 -projectName $projectName -version $localTag
}

Write-Output "Deployment successful."
