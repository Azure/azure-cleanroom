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
    $ccfProjectName = "samples-governance-ccf",

    [string]
    $projectName = "samples-governance-cli",

    [string]
    $outDir = "",

    [ValidateSet('mcr', 'local', 'acr')]
    [string]$registry = "local",

    [string]$repo = "localhost:5000",

    [string]$tag = "latest",

    [string]
    $ccfEndpoint = "",

    [string]
    $memberKeysAkvVaultName = ""
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$build = "$root/build"

. $root/build/helpers.ps1

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

$orasImage = "ghcr.io/oras-project/oras:v1.2.0"

$env:ccfImage = ""
$env:recoveryAgentImage = ""
if ($registry -eq "local") {
    $env:ccfImage = "ccf/app/run-js/sandbox:latest"
    $env:recoveryAgentImage = "ccf/ccf-recovery-agent:latest"
    if (!$NoBuild) {
        pwsh $build/build-azcliext-cleanroom.ps1

        if ($ccfEndpoint -eq "") {
            pwsh $build/ccf/build-ccf-runjs-app-sandbox.ps1
            pwsh $build/ccf/build-ccf-recovery-agent.ps1
        }

        pwsh $build/cgs/build-cgs-client.ps1
        pwsh $build/cgs/build-cgs-ui.ps1
    }
}
elseif ($registry -eq "acr") {
    if ($ccfEndpoint -eq "") {
        $env:ccfImage = "$repo/ccf/app/run-js/sandbox:$tag"
        $env:recoveryAgentImage = "$repo/ccf/ccf-recovery-agent:$tag"
    }

    $whlPath = "$repo/cli/cleanroom-whl:$tag"
    Write-Host "Downloading and installing az cleanroom cli from ${whlPath}"
    if ($env:GITHUB_ACTIONS -eq "true") {
        oras pull $whlPath --output $sandbox_common
    }
    else {
        $orasImage = "ghcr.io/oras-project/oras:v1.2.0"
        docker run --rm --network host -v ${sandbox_common}:/workspace -w /workspace `
            $orasImage pull $whlPath
    }

    & {
        # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
        $PSNativeCommandUseErrorActionPreference = $false
        az extension remove --name cleanroom 2>$null
    }
    az extension add `
        --allow-preview true `
        --source ${sandbox_common}/cleanroom-*-py2.py3-none-any.whl -y
}
else {
    if (!$NoBuild) {
        if ($ccfEndpoint -eq "") {
            pwsh $build/ccf/build-ccf-runjs-app-sandbox.ps1
            pwsh $build/ccf/build-ccf-recovery-agent.ps1
        }

        # Install clean room extension that corresponds to the MCR images.
        $version = (az extension show --name cleanroom --query "version" --output tsv 2>$null)
        if ($version -ne "0.0.1") {
            oras pull mcr.microsoft.com/azurecleanroom/cli/cleanroom-whl:6.0.0

            Write-Host "Installing az cleanroom cli"
            az extension remove --name cleanroom 2>$null
            az extension add `
                --allow-preview true `
                --source ./cleanroom-6.0.0-py2.py3-none-any.whl -y
        }
        else {
            Write-Host "az cleanroom cli version: $version already installed."
        }
    }
}

# Create registry container unless it already exists.
if ($registry -eq "local") {
    $reg_name = "ccr-registry"
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
            docker run -d --restart=always -p "127.0.0.1:${reg_port}:5000" --network bridge --name "${reg_name}" $registryImage
        }
    }
}

if ($ccfEndpoint -eq "") {
    if ($memberKeysAkvVaultName -eq "") {
        & {
            # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
            # Creating the initial member identity certificate to add into the consortium.
            $PSNativeCommandUseErrorActionPreference = $false
            az cleanroom governance member keygenerator-sh | `
                bash -s -- --name $initialMemberName --gen-enc-key --out $sandbox_common
        }
    }
    else {
        # Generate member identity certificate/key.
        Write-Output "Generating identity private key and certificate for participant '$initialMemberName' in key vault..."
        $certName = "${initialMemberName}-identity"
        $policy = az cleanroom governance member get-default-certificate-policy `
            --member-name $initialMemberName
        az keyvault certificate create `
            --name $certName `
            --vault-name $memberKeysAkvVaultName `
            --policy "$policy"
        $cert_pem_file = "$sandbox_common/${initialMemberName}_cert.pem"
        if (Test-Path $cert_pem_file) {
            # az keyvault certificate download does not overwrite hence remove any existing file first.
            Remove-Item $cert_pem_file
        }
        az keyvault certificate download `
            --file $cert_pem_file `
            --name $certName `
            --vault-name $memberKeysAkvVaultName `
            --encoding PEM
        Write-Output "Identity certificate generated at: $cert_pem_file (to be registered in CCF)"
        $certId = az keyvault certificate show `
            --name $certName `
            --vault-name $memberKeysAkvVaultName `
            --query id `
            --output tsv
        $certId | Out-File "$sandbox_common/${initialMemberName}_cert.id"

        # Generate member encryption key.
        Write-Output "Generating RSA encryption key pair for participant '$initialMemberName' in key vault..."
        $encKeyName = "${initialMemberName}-encryption"
        az keyvault key create `
            --name $encKeyName `
            --vault-name $memberKeysAkvVaultName `
            --kty RSA `
            --size 2048 `
            --ops decrypt
        $enc_pubk_pem_file = "$sandbox_common/${initialMemberName}_enc_pubk.pem"
        if (Test-Path $enc_pubk_pem_file) {
            # az keyvault key download does not overwrite hence remove any existing file first.
            Remove-Item $enc_pubk_pem_file
        }
        az keyvault key download `
            --file $enc_pubk_pem_file `
            --name $encKeyName `
            --vault-name $memberKeysAkvVaultName
        Write-Output "Encryption public key generated at: $enc_pubk_pem_file (to be registered in CCF)"
    }

    $env:initialMemberName = $initialMemberName
    $env:cgs_sandbox_common = $sandbox_common
    $env:root_dir = $root
    docker compose -f $PSScriptRoot/docker-compose.yml -p $ccfProjectName up -d --remove-orphans
    $ccfPortMapping = docker compose -f $PSScriptRoot/docker-compose.yml -p $ccfProjectName port ccf 8080
    if ($ccfPortMapping -eq "") {
        throw "Could not determine port mapping for ccf."
    }
    # $ccfPortMapping format is 0.0.0.0:<port>
    $ccfPort = $ccfPortMapping.split(":")[1]

    $agentPortMapping = docker compose -f $PSScriptRoot/docker-compose.yml -p $ccfProjectName port recovery-agent 9080
    if ($agentPortMapping -eq "") {
        throw "Could not determine port mapping for recovery-agent."
    }
    # $agentPortMapping format is 0.0.0.0:<port>
    $agentPort = $agentPortMapping.split(":")[1]

    $ccfEndpoint = ""
    $agentEndpoint = ""
    if ($env:CODESPACES -ne "true" -and $env:GITHUB_ACTIONS -ne "true") {
        $ccfEndpoint = "https://host.docker.internal:$ccfPort"
        $agentEndpoint = "http://host.docker.internal:$agentPort"
    }
    else {
        # 172.17.0.1: https://stackoverflow.com/questions/48546124/what-is-the-linux-equivalent-of-host-docker-internal
        $ccfEndpoint = "https://172.17.0.1:$ccfPort"
        $agentEndpoint = "http://172.17.0.1:$agentPort"
    }

    @"
{
  "endpoint": "$ccfEndpoint"
}
"@ | Out-File $sandbox_common/ccf.json

    # wait for agent endpoint to be up.
    & {
        # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
        $PSNativeCommandUseErrorActionPreference = $false
        $timeout = New-TimeSpan -Minutes 5
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $statusCode = (curl -s -o /dev/null -w "%{http_code}" $agentEndpoint/ready)
        while ($statusCode -ne "200") {
            Write-Host "Waiting for recovery-agent endpoint to be up"
            Start-Sleep -Seconds 3
            if ($stopwatch.elapsed -gt $timeout) {
                throw "Hit timeout waiting for recovery-agent endpoint to be up."
            }
            $statusCode = (curl -s  -o /dev/null -w "%{http_code}" $agentEndpoint/ready)
        }
    }

    @"
{
  "endpoint": "$agentEndpoint",
  "snpHostData": "73973b78d70cc68353426de188db5dfc57e5b766e399935fb73a61127ea26d20"
}
"@ | Out-File $sandbox_common/ccf.recovery-agent.json
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

# Push cgs-client/ui/constitution/jsapp images if local registry is to be used for CGS.
$localTag = ""
if ($registry -eq "local") {
    $localTag = "100.$(Get-Date -UFormat %s)"
    $localTag | Out-File $sandbox_common/local-registry-tag.txt

    # Push the images.
    docker tag cgs-client:latest $repo/cgs-client:$localTag
    docker push $repo/cgs-client:$localTag
    docker tag cgs-ui:latest $repo/cgs-ui:$localTag
    docker push $repo/cgs-ui:$localTag

    $client_digest = docker inspect --format='{{index .RepoDigests 0}}' $repo/cgs-client:$localTag
    $client_digest = $client_digest.Substring($client_digest.Length - 71, 71)
    $client_digest_no_prefix = $client_digest.Substring(7, $client_digest.Length - 7)
    $ui_digest = docker inspect --format='{{index .RepoDigests 0}}' $repo/cgs-ui:$localTag
    $ui_digest = $ui_digest.Substring($ui_digest.Length - 71, 71)
    $ui_digest_no_prefix = $ui_digest.Substring(7, $ui_digest.Length - 7)

    @"
cgs-client:
  version: "$localTag"
  image: $repo/cgs-client@$client_digest
"@ | Out-File $sandbox_common/cgs-client-version.yaml

    # Push the version document for the current image and also tag the same as latest.
    docker run --rm --network host -v ${sandbox_common}/cgs-client-version.yaml:/workspace/version.yaml `
        $orasImage push `
        $repo/versions/cgs-client:"$client_digest_no_prefix,latest" `
        ./version.yaml

    @"
cgs-ui:
  version: "$localTag"
  image: $repo/cgs-ui@$ui_digest
"@ | Out-File $sandbox_common/cgs-ui-version.yaml

    # Push the version document for the current image and also tag the same as latest.
    docker run --rm --network host -v ${sandbox_common}/cgs-ui-version.yaml:/workspace/version.yaml `
        $orasImage push `
        $repo/versions/cgs-ui:"$ui_digest_no_prefix,latest" `
        ./version.yaml

    $env:AZCLI_CGS_CLIENT_IMAGE = "$repo/cgs-client:$localTag"
    $env:AZCLI_CGS_UI_IMAGE = "$repo/cgs-ui:$localTag"

    pwsh $build/cgs/build-cgs-ccf-artefacts.ps1 -repo $repo -tag $localTag -push -outDir $sandbox_common

    $constitution_image_digest = docker run --rm --network host `
        $orasImage resolve $repo/cgs-constitution:$localTag
    $bundle_image_digest = docker run --rm --network host `
        $orasImage resolve $repo/cgs-js-app:$localTag

    $env:AZCLI_CGS_CONSTITUTION_IMAGE = "$repo/cgs-constitution@$constitution_image_digest"
    $env:AZCLI_CGS_JSAPP_IMAGE = "$repo/cgs-js-app@$bundle_image_digest"

    $env:AZCLI_CLEANROOM_VERSIONS_REGISTRY = $repo
}
elseif ($registry -eq "acr") {
    $localTag = $tag

    docker pull $repo/cgs-client:$localTag
    $client_digest = docker inspect --format='{{index .RepoDigests 0}}' $repo/cgs-client:$localTag
    $client_digest = $client_digest.Substring($client_digest.Length - 71, 71)
    $client_digest_no_prefix = $client_digest.Substring(7, $client_digest.Length - 7)

    docker pull $repo/cgs-ui:$localTag
    $ui_digest = docker inspect --format='{{index .RepoDigests 0}}' $repo/cgs-ui:$localTag
    $ui_digest = $ui_digest.Substring($ui_digest.Length - 71, 71)
    $ui_digest_no_prefix = $ui_digest.Substring(7, $ui_digest.Length - 7)

    $env:AZCLI_CGS_CLIENT_IMAGE = "$repo/cgs-client:$localTag"
    $env:AZCLI_CGS_UI_IMAGE = "$repo/cgs-ui:$localTag"

    $constitution_image_digest = docker run --rm --network host `
        $orasImage resolve $repo/cgs-constitution:$localTag
    $bundle_image_digest = docker run --rm --network host `
        $orasImage resolve $repo/cgs-js-app:$localTag

    $env:AZCLI_CGS_CONSTITUTION_IMAGE = "$repo/cgs-constitution@$constitution_image_digest"
    $env:AZCLI_CGS_JSAPP_IMAGE = "$repo/cgs-js-app@$bundle_image_digest"

    $env:AZCLI_CLEANROOM_VERSIONS_REGISTRY = $repo
}

# Setup cgs-client instance on port $port with member cert/key information so that we can invoke CCF
# APIs via it.
if (Test-Path $sandbox_common/${initialMemberName}_cert.id) {
    az cleanroom governance client deploy `
        --ccf-endpoint $ccfEndpoint `
        --signing-cert-id $sandbox_common/${initialMemberName}_cert.id `
        --service-cert $sandbox_common/service_cert.pem `
        --name $projectName
}
else {
    az cleanroom governance client deploy `
        --ccf-endpoint $ccfEndpoint `
        --signing-key $sandbox_common/${initialMemberName}_privk.pem `
        --signing-cert $sandbox_common/${initialMemberName}_cert.pem `
        --service-cert $sandbox_common/service_cert.pem `
        --name $projectName
}

$port = az cleanroom governance client show-deployment `
    --name $projectName `
    --query 'ports."cgs-client"' `
    --output tsv

# wait for cgsclient endpoint to be up.
& {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
    $PSNativeCommandUseErrorActionPreference = $false
    while ((curl -s  -o /dev/null -w "%{http_code}" http://localhost:$port/ready) -ne "200") {
        Write-Host "Waiting for cgs-client endpoint to be up"
        Start-Sleep -Seconds 3
    }
}

Write-Output "Activating $initialMemberName..."
az cleanroom governance member activate --governance-client $projectName

timeout 20 bash -c `
    "until az cleanroom governance member show --governance-client $projectName | jq -r '.value[].status' | grep Active > /dev/null; do echo Waiting for member to be in Active state...; sleep 5; done"
az cleanroom governance member show --governance-client $projectName | jq
Write-Output "Member status is now Active"

# Setting up sts.windows.net as a trusted JWT token issuer.
Write-Output "Submitting set_ca_cert_bundle proposal for trusted root CAs"
$proposalId = (az cleanroom governance proposal create `
        --content "$PSScriptRoot/proposals/set_ca_cert_bundle/trusted_root_cas.json" `
        --governance-client $projectName | jq -r '.proposalId')
Write-Output "Accepting the set_ca_cert_bundle proposal as $initialMemberName"
curl -sS -X POST localhost:$port/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Submitting set_jwt_issuer proposal for sts.windows.net"
$proposalId = (az cleanroom governance proposal create `
        --content "$PSScriptRoot/proposals/set_jwt_issuer/sts.windows.net.json" `
        --governance-client $projectName | jq -r '.proposalId')
Write-Output "Accepting the set_issuer proposal as $initialMemberName"
curl -sS -X POST localhost:$port/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Submitting set_jwt_issuer proposal for login.microsoftonline.com"
$proposalId = (az cleanroom governance proposal create `
        --content "$PSScriptRoot/proposals/set_jwt_issuer/login.microsoftonline.com.json" `
        --governance-client $projectName | jq -r '.proposalId')
Write-Output "Accepting the set_issuer proposal as $initialMemberName"
curl -sS -X POST localhost:$port/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Submitting open network proposal"
$certContent = (Get-Content $sandbox_common/service_cert.pem -Raw).ReplaceLineEndings("\n")
$proposalId = (az cleanroom governance proposal create --content `
        @"
{
  "actions": [{
     "name": "transition_service_to_open",
     "args": {
        "next_service_identity": "$certContent"
     }
   }]
}
"@ --governance-client $projectName | jq -r '.proposalId')

Write-Output "Accepting the open network proposal as $initialMemberName"
curl -sS -X POST localhost:$port/proposals/$proposalId/ballots/vote_accept | jq

Write-Output "Waiting a bit to avoid FrontendNotOpen error"
sleep 3

az cleanroom governance service deploy --governance-client $projectName

Write-Output "Governance service deployed. Version info:"
az cleanroom governance service version --governance-client $projectName

$memberId = (az cleanroom governance client show --name $projectName --query "memberId" --output tsv)
Write-Output "Submitting set_member_data proposal for $initialMemberName"
$proposalId = (az cleanroom governance proposal create --content `
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
"@ --governance-client $projectName | jq -r '.proposalId')

Write-Output "Accepting the set_member_data proposal"
curl -sS -X POST localhost:$port/proposals/$proposalId/ballots/vote_accept | jq

if (!$NoTest) {
    pwsh $PSScriptRoot/initiate-set-contract-flow.ps1 -projectName $projectName -issuerUrl "$ccfEndpoint/app/oidc"
    pwsh $PSScriptRoot/initiate-add-azure-user-identity-flow.ps1 -projectName $projectName -ccfEndpoint $ccfEndpoint -outDir $sandbox_common
    pwsh $PSScriptRoot/initiate-invite-azure-user-identity-flow.ps1 -projectName $projectName -ccfEndpoint $ccfEndpoint -outDir $sandbox_common
    pwsh $PSScriptRoot/initiate-service-upgrade-flow.ps1 -projectName $projectName -version $localTag -repo $repo
    pwsh $PSScriptRoot/initiate-client-upgrade-flow.ps1 -projectName $projectName -version $localTag
}

Write-Output "Deployment successful."
