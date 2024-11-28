[CmdletBinding()]
param
(
    [string]
    [Parameter(Mandatory)]
    [ValidateSet('virtual', 'virtualaci', 'caci')]
    $infraType,

    [string]
    $networkName = "",

    [string]
    $initialMemberName = "member0",

    [string]
    $initialMemberProjectName = "member0-governance",

    [int]
    $nodeCount = 1,

    [string]
    [ValidateSet("Trace", "Debug", "Info", "Fail", "Fatal")]
    $nodeLogLevel = "Debug",

    [string]
    [ValidateSet("cached", "cached-debug", "allow-all")]
    $securityPolicyCreationOption = "allow-all",

    [switch]
    $NoBuild,

    [switch]
    $NoTest,

    [string]
    $resourceGroup = "",

    [string]
    $location = "westeurope",

    [string]
    [ValidateSet("default", "azurefiles", "dockerhostfs", "localfs")]
    $nodeStorageType = "default",

    [switch]$mcr,

    [string]$registryUrl = "",

    [string]$tag = "latest",

    [switch]
    $fastJoin,

    [switch]
    $startNodeSleep,

    [switch]
    $joinNodeSleep,

    [switch]
    $confidentialRecovery,

    [switch]
    $oneStepConfigureConfidentialRecovery,

    [switch]
    $noDelete
)

function Get-UniqueString ([string]$id, $length = 13) {
    $hashArray = (new-object System.Security.Cryptography.SHA512Managed).ComputeHash($id.ToCharArray())
    -join ($hashArray[1..$length] | ForEach-Object { [char]($_ % 26 + [byte][char]'a') })
}

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$build = "$root/build"

. $root/build/helpers.ps1
Import-Module $root/samples/common/infra-scripts/azure-helpers.psm1 -Force -DisableNameChecking

$sandbox_common = "$PSScriptRoot/sandbox_common"

if (!$noDelete) {
    rm -rf $sandbox_common
}
mkdir -p $sandbox_common

if (!$NoBuild) {
    pwsh $build/build-azcliext-cleanroom.ps1
}

if ($infraType -eq "caci" -and $registryUrl -eq "") {
    Write-Host -ForegroundColor Red "-registryUrl must be specified for caci. " `
        "To build and push containers to an acr do:`n" `
        "az acr login -n <youracrname>`n" `
        "./build/ccf/build-ccf-infra-containers.ps1 -repo <youracrname>.azurecr.io -tag 1212 -push`n" `
        "./samples/ccf/ccf-provider/azcli/deploy-ccf.ps1 -infraType caci -registryUrl <youracrname>.azurecr.io -tag 1212 ...`n"
    exit 1
}

if ($nodeStorageType -eq "default") {
    if ($infraType -eq "caci") {
        $nodeStorageType = "azurefiles"
    }
    elseif ($infraType -eq "virtual") {
        $nodeStorageType = "dockerhostfs"
    }
    elseif ($infraType -eq "virtualaci") {
        $nodeStorageType = "localfs"
    }
    else {
        throw "infraType: $infraType not handled for nodeStorageType: $nodeStorageType value. Update script."
    }
}

if ($oneStepConfigureConfidentialRecovery -and !$confidentialRecovery) {
    throw "-oneStepConfigureConfidentialRecovery can only be specified along with -confidentialRecovery"
}

$ccfProviderProjectName = "ccf-provider"
$cgsProjectName = "ccf-provider-governance"
$operatorName = "ccf-operator"

if (!$mcr) {
    if ($registryUrl -eq "") {
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

        $localTag = "100.$(Get-Date -UFormat %s)"
        $localTag | Out-File $sandbox_common/local-registry-tag.txt
        $server = "localhost:$reg_port"

        if (!$NoBuild) {
            pwsh $build/ccf/build-ccf-infra-containers.ps1 -repo $server -tag latest
        }

        docker tag $server/ccr-proxy:latest $server/ccr-proxy:$localTag
        docker push $server/ccr-proxy:$localTag
        docker tag $server/ccf/ccf-nginx:latest $server/ccf/ccf-nginx:$localTag
        docker push $server/ccf/ccf-nginx:$localTag
        docker tag $server/ccf/ccf-provider-client:latest $server/ccf/ccf-provider-client:$localTag
        docker push $server/ccf/ccf-provider-client:$localTag
        docker tag $server/ccf/app/run-js/virtual:latest $server/ccf/app/run-js/virtual:$localTag
        docker push $server/ccf/app/run-js/virtual:$localTag
        docker tag $server/ccf/app/run-js/snp:latest $server/ccf/app/run-js/snp:$localTag
        docker push $server/ccf/app/run-js/snp:$localTag
        docker tag $server/ccf/ccf-recovery-agent:latest $server/ccf/ccf-recovery-agent:$localTag
        docker push $server/ccf/ccf-recovery-agent:$localTag
        docker tag $server/ccf/ccf-recovery-service:latest $server/ccf/ccf-recovery-service:$localTag
        docker push $server/ccf/ccf-recovery-service:$localTag
    }
    else {
        $server = $registryUrl
        $localTag = $tag
    }
}
$subscriptionId = ""
$CCF_RESOURCE_GROUP_LOCATION = ""
$CCF_RESOURCE_GROUP = ""
$STORAGE_ACCOUNT_NAME = ""
$storageAccountId = ""
$subscriptionId = az account show --query "id" -o tsv
$resourceGroupTags = ""
if ($resourceGroup -ne "") {
    $CCF_RESOURCE_GROUP = $resourceGroup
}
else {
    if ($env:GITHUB_ACTIONS -eq "true") {
        $CCF_RESOURCE_GROUP = "ccf-network-${env:JOB_ID}-${env:RUN_ID}"
        $resourceGroupTags = "github_actions=ccf-network-${env:JOB_ID}-${env:RUN_ID}"
    }
    else {
        $CCF_RESOURCE_GROUP = "ccf-ob-${env:USER}"
    }
}

$CCF_RESOURCE_GROUP_LOCATION = $location

# Create an RG either for ACI intances and/or the storage account for blobfuse/azure file share.
if ($nodeStorageType -eq "azurefiles" -or $infraType -ne "virtual" -or $confidentialRecovery) {
    $subscriptionId = az account show --query "id" -o tsv
    Write-Output "Creating resource group $CCF_RESOURCE_GROUP in $CCF_RESOURCE_GROUP_LOCATION"
    az group create `
        --location $CCF_RESOURCE_GROUP_LOCATION `
        --name $CCF_RESOURCE_GROUP `
        --tags $resourceGroupTags 1>$null

    if ($nodeStorageType -eq "azurefiles") {
        $uniqueString = Get-UniqueString("${CCF_RESOURCE_GROUP}")
        $STORAGE_ACCOUNT_NAME = "${uniqueString}sa"
        $objectId = GetLoggedInEntityObjectId
        $storageAccountId = Create-Storage-Resources `
            -resourceGroup $CCF_RESOURCE_GROUP `
            -storageAccountName @($STORAGE_ACCOUNT_NAME) `
            -objectId $objectId `
            -enableHns `
            -allowSharedKeyAccess # Azure Files works via API key with ACI.
        $storageAccountId = $(az storage account show `
                -n $STORAGE_ACCOUNT_NAME `
                -g $CCF_RESOURCE_GROUP `
                --query "id" `
                --output tsv)
    }
}

if ($env:GITHUB_ACTIONS -eq "true") {
    if ($networkName -eq "") {
        $uniqueString = Get-UniqueString("ccf-network-${env:JOB_ID}-${env:RUN_ID}")
        $networkName = "ccf-${uniqueString}"
    }
}
else {
    if ($networkName -eq "") {
        if ($infraType -eq "virtual") {
            $networkName = "testnet-virtual"
        }
        else {
            $uniqueString = Get-UniqueString("${CCF_RESOURCE_GROUP}")
            $networkName = "ccf-${uniqueString}"
        }
    }
}

$recoveryServiceName = $networkName

if (!$mcr) {
    $env:AZCLI_CCF_PROVIDER_CLIENT_IMAGE = "$server/ccf/ccf-provider-client:$localTag"
    $env:AZCLI_CCF_PROVIDER_PROXY_IMAGE = "$server/ccr-proxy:$localTag"
    $env:AZCLI_CCF_PROVIDER_ATTESTATION_IMAGE = "$server/ccr-attestation:$localTag"
    $env:AZCLI_CCF_PROVIDER_SKR_IMAGE = "$server/skr:$localTag"
    $env:AZCLI_CCF_PROVIDER_NGINX_IMAGE = "$server/ccf/ccf-nginx:$localTag"
    $env:AZCLI_CCF_PROVIDER_RUN_JS_APP_VIRTUAL_IMAGE = "$server/ccf/app/run-js/virtual:$localTag"
    $env:AZCLI_CCF_PROVIDER_RUN_JS_APP_SNP_IMAGE = "$server/ccf/app/run-js/snp:$localTag"
    $env:AZCLI_CCF_PROVIDER_RECOVERY_AGENT_IMAGE = "$server/ccf/ccf-recovery-agent:$localTag"
    $env:AZCLI_CCF_PROVIDER_RECOVERY_SERVICE_IMAGE = "$server/ccf/ccf-recovery-service:$localTag"
    $env:AZCLI_CCF_PROVIDER_CONTAINER_REGISTRY_URL = "$server"
    $env:AZCLI_CCF_PROVIDER_NETWORK_SECURITY_POLICY_DOCUMENT_URL = "$server/policies/ccf/ccf-network-security-policy:$localTag"
    $env:AZCLI_CCF_PROVIDER_RECOVERY_SERVICE_SECURITY_POLICY_DOCUMENT_URL = "$server/policies/ccf/ccf-recovery-service-security-policy:$localTag"
}
else {
    # Unset these so that default azurecr.io paths baked in the AZCLI_CCF_PROVIDER_CLIENT_IMAGE get used.
    $env:AZCLI_CCF_PROVIDER_CLIENT_IMAGE = ""
    $env:AZCLI_CCF_PROVIDER_PROXY_IMAGE = ""
    $env:AZCLI_CCF_PROVIDER_ATTESTATION_IMAGE = ""
    $env:AZCLI_CCF_PROVIDER_SKR_IMAGE = ""
    $env:AZCLI_CCF_PROVIDER_NGINX_IMAGE = ""
    $env:AZCLI_CCF_PROVIDER_RUN_JS_APP_VIRTUAL_IMAGE = ""
    $env:AZCLI_CCF_PROVIDER_RUN_JS_APP_SNP_IMAGE = ""
    $env:AZCLI_CCF_PROVIDER_RECOVERY_AGENT_IMAGE = ""
    $env:AZCLI_CCF_PROVIDER_RECOVERY_SERVICE_IMAGE = ""
    $env:AZCLI_CCF_PROVIDER_CONTAINER_REGISTRY_URL = ""
    $env:AZCLI_CCF_PROVIDER_NETWORK_SECURITY_POLICY_DOCUMENT_URL = ""
    $env:AZCLI_CCF_PROVIDER_RECOVERY_SERVICE_SECURITY_POLICY_DOCUMENT_URL = ""
}
az cleanroom ccf provider deploy --name $ccfProviderProjectName

$providerConfig = @{}
if ($infraType -eq "virtualaci" -or $infraType -eq "caci") {
    $providerConfig.location = $CCF_RESOURCE_GROUP_LOCATION
    $providerConfig.subscriptionId = $subscriptionId
    $providerConfig.resourceGroupName = $CCF_RESOURCE_GROUP
}

if ($nodeStorageType -eq "azureFiles") {
    if ($PSBoundParameters.ContainsKey('fastJoin')) {
        $providerConfig.fastJoin = $fastJoin ? "true" : "false"
    }
    $providerConfig.azureFiles = @{}
    $providerConfig.azureFiles.storageAccountId = $storageAccountId
}

if ($startNodeSleep) {
    $providerConfig.startNodeSleep = "true"
}

if ($joinNodeSleep) {
    $providerConfig.joinNodeSleep = "true"
}

$providerConfig | ConvertTo-Json -Depth 100 > $sandbox_common/providerConfig.json

if (!$noDelete) {
    Write-Output "Deleting any existing $infraType network $networkName."
    az cleanroom ccf network delete `
        --name $networkName `
        --infra-type $infraType `
        --provider-config $sandbox_common/providerConfig.json

    if ($infraType -ne "virtualaci") {
        Write-Output "Deleting any existing $infraType recovery service $networkName."
        az cleanroom ccf recovery-service delete `
            --name $recoveryServiceName `
            --infra-type $infraType `
            --provider-config $sandbox_common/providerConfig.json
    }

    # Creating the operator identity certificate to add into the consortium.
    if ($confidentialRecovery) {
        # Don't generate encryption key for the operator as CCF recovery service will be used.
        az cleanroom governance member keygenerator-sh | `
            bash -s -- --name $operatorName --out $sandbox_common 1>$null 2>$null
    }
    else {
        # Create encryption key for the operator to act as the recovery member.
        $encryptionPublicKey = "$sandbox_common/${operatorName}_enc_pubk.pem"

        az cleanroom governance member keygenerator-sh | `
            bash -s -- --name $operatorName --gen-enc-key --out $sandbox_common 1>$null 2>$null
    }

    # Creating the initial member identity certificate to add into the consortium.
    az cleanroom governance member keygenerator-sh | `
        bash -s -- --name $initialMemberName --out $sandbox_common 1>$null 2>$null
}

@"
[{
    "certificate": "$sandbox_common/${operatorName}_cert.pem",
    "encryptionPublicKey": "$encryptionPublicKey",
    "memberData": {
        "identifier": "$operatorName", 
        "is_operator": true
    }
},
{
    "certificate": "$sandbox_common/${initialMemberName}_cert.pem",
    "memberData": {
        "identifier": "$initialMemberName"
    }
}]
"@ > $sandbox_common/members.json

Write-Output "Creating a $nodeCount node $infraType network $networkName with storage type $nodeStorageType. Resource group being used: $CCF_RESOURCE_GROUP."
az cleanroom ccf network create `
    --name $networkName `
    --node-count $nodeCount `
    --node-log-level $nodeLogLevel `
    --security-policy-creation-option $securityPolicyCreationOption `
    --infra-type $infraType `
    --members @$sandbox_common/members.json `
    --provider-config $sandbox_common/providerConfig.json

$response = az cleanroom ccf network show `
    --name $networkName `
    --infra-type $infraType `
    --provider-config $sandbox_common/providerConfig.json
$response | Out-File $sandbox_common/ccf.json

$ccfEndpoint = ($response | ConvertFrom-Json).endpoint
Write-Output $ccfEndpoint
$response = (curl "$ccfEndpoint/node/network" -k --silent | ConvertFrom-Json)
# Trimming an extra new-line character added to the cert.
$serviceCertStr = $response.service_certificate.TrimEnd("`n")
$serviceCertStr | Out-File "$sandbox_common/service_cert.pem"

# Deploy the governance client for the operator to take any gov actions.
az cleanroom governance client deploy `
    --ccf-endpoint $ccfEndpoint `
    --signing-key $sandbox_common/${operatorName}_privk.pem `
    --signing-cert $sandbox_common/${operatorName}_cert.pem `
    --service-cert $sandbox_common/service_cert.pem `
    --name $cgsProjectName

# Activate the operator membership by default in the cluster that just got created.
az cleanroom governance member activate --governance-client $cgsProjectName

# Configure the ccf provider client for the operator to take any operator actions like opening
# the network.
az cleanroom ccf provider configure `
    --signing-key $sandbox_common/${operatorName}_privk.pem `
    --signing-cert $sandbox_common/${operatorName}_cert.pem `
    --name $ccfProviderProjectName

if ($infraType -eq "caci") {
    Write-Output "Querying the hostData value for the nodes of the CCF network..."
    $securityPolicy = (az cleanroom ccf network security-policy generate `
            --security-policy-creation-option $securityPolicyCreationOption `
            --infra-type $infraType | ConvertFrom-Json)
    $expectedHostData = $securityPolicy.snp.hostData.PSObject.Properties.Name
    $currentPolicy = (az cleanroom ccf network join-policy show `
            --name $networkName `
            --infra-type $infraType `
            --provider-config $sandbox_common/providerConfig.json | ConvertFrom-Json)
    $currentHostData = $currentPolicy.snp.hostData.PSObject.Properties.Name
    if ($currentHostData -ne $expectedHostData) {
        throw "Expecting node to have started with $expectedHostData hostData but network join policy has $currentHostData."
    }

    $reports = az cleanroom ccf network show-report `
        --name $networkName `
        --infra-type $infraType `
        --provider-config $sandbox_common/providerConfig.json | ConvertFrom-Json
    $nodeHostDataMatches = 0
    foreach ($item in $reports.reports) {
        if ($item.hostData -eq $expectedHostData) {
            $nodeHostDataMatches++
        }

        if ($item.verified -ne $true) {
            throw "Expecting report.verified to be true. Report verification failed."
        }
    }

    if ($nodeHostDataMatches -ne $nodeCount) {
        Write-Output ($reports | ConvertTo-Json)
        throw "Expecting to find $nodeCount  nodes with hostData $expectedHostData but found $nodeHostDataMatches."
    }

    Write-Output "Node running with hostData $expectedHostData."
}

if ($confidentialRecovery) {
    pwsh $root/samples/ccf/ccf-provider/azcli/recovery/prepare-resources.ps1 `
        -resourceGroup $CCF_RESOURCE_GROUP `
        -infraType $infraType `
        -outDir $sandbox_common
    $recovery = $(Get-Content $sandbox_common/recoveryResources.json | ConvertFrom-Json)

    # Generated the initial security policy for the network that gets configured on the recovery service.
    az cleanroom ccf network security-policy generate-join-policy `
        --security-policy-creation-option $securityPolicyCreationOption `
        --infra-type $infraType `
    | Out-File $sandbox_common/networkJoinPolicy.json

    Write-Output "Deploying CCF recovery service and adding confidential recoverer."
    az cleanroom ccf recovery-service create `
        --name $recoveryServiceName `
        --infra-type $infraType `
        --key-vault $recovery.kvId `
        --maa-endpoint $recovery.maaEndpoint `
        --identity $recovery.miId `
        --ccf-network-join-policy $sandbox_common/networkJoinPolicy.json `
        --security-policy-creation-option $securityPolicyCreationOption `
        --provider-config $sandbox_common/providerConfig.json

    # Create config files used by configure/recover scripts.
    $recSvc = (az cleanroom ccf recovery-service show `
            --name $recoveryServiceName `
            --infra-type $infraType `
            --provider-config $sandbox_common/providerConfig.json | ConvertFrom-Json)
    $agentConfig = @{}
    $agentConfig.recoveryService = @{}
    $agentConfig.recoveryService.endpoint = $recSvc.endpoint
    $agentConfig.recoveryService.serviceCert = $recSvc.serviceCert
    $agentConfig | ConvertTo-Json -Depth 100 > $sandbox_common/recoveryAgentConfig.json

    $rsvcConfig = @{}
    $rsvcConfig.recoveryService = @{}
    $rsvcConfig.recoveryService.endpoint = $recSvc.endpoint
    $rsvcConfig.recoveryService.serviceCert = $recSvc.serviceCert
    $rsvcConfig | ConvertTo-Json -Depth 100 > $sandbox_common/recoveryServiceConfig.json

    # Trimming an extra new-line character added to the cert.
    $serviceCertStr = $recSvc.serviceCert.TrimEnd("`n")
    $serviceCertStr | Out-File $sandbox_common/recovery_service_cert.pem

    if ($infraType -eq "caci") {
        Write-Output "Querying the hostData value for the CCF recovery service..."
        $securityPolicy = (az cleanroom ccf recovery-service security-policy generate `
                --security-policy-creation-option $securityPolicyCreationOption `
                --ccf-network-join-policy $sandbox_common/networkJoinPolicy.json `
                --infra-type $infraType | ConvertFrom-Json)
        $expectedHostData = $securityPolicy.snp.hostData.PSObject.Properties.Name
        $reportResponse = (az cleanroom ccf recovery-service api show-report `
                --service-config $sandbox_common/recoveryServiceConfig.json | ConvertFrom-Json)
        $currentHostData = $reportResponse.hostData
        if ($currentHostData -ne $expectedHostData) {
            throw "Expecting node to have started with $expectedHostData hostData but node is reporting hostData value as $currentHostData."
        }

        if ($reportResponse.verified -ne $true) {
            throw "Expecting report.verified for $recoveryServiceName to be true. Report verification failed."
        }

        Write-Output "Recovery service is running with hostData $currentHostData."
    }

    $recoveryMemberName = $recovery.confidentialRecovererMemberName
    if ($oneStepConfigureConfidentialRecovery) {
        Write-Output "Configuring confidential recovery in one-step."
        az cleanroom ccf network configure-confidential-recovery `
            --name $networkName `
            --recovery-service-name $recoveryServiceName `
            --recovery-member-name $recoveryMemberName `
            --infra-type $infraType `
            --provider-config $sandbox_common/providerConfig.json
    }
    else {
        $reportResponse = (az cleanroom ccf recovery-service api show-report `
                --service-config $sandbox_common/recoveryServiceConfig.json | ConvertFrom-Json)
        if ($reportResponse.serviceCert -ne $recSvc.serviceCert) {
            Write-Output "/report serviceCert: ${reportResponse.serviceCert}"
            Write-Output "provider serviceCert: ${recSvc.serviceCert}"
            throw "Mismatch in serviceCert output between the provider and /report endpoint."
        }

        az cleanroom ccf network recovery-agent generate-member `
            --network-name $networkName `
            --member-name $recoveryMemberName `
            --infra-type $infraType `
            --agent-config $sandbox_common/recoveryAgentConfig.json `
            --provider-config $sandbox_common/providerConfig.json

        Write-Output "Adding confidential recovery member $recoveryMemberName into the consortium."
        $crm = (az cleanroom ccf recovery-service api member show `
                --member-name $recoveryMemberName `
                --service-config $sandbox_common/recoveryServiceConfig.json | ConvertFrom-Json)

        if ($infraType -eq "caci") {
            if ($reportResponse.hostData -ne $crm.recoveryService.hostData) {
                Write-Output $crm | ConvertTo-Json
                throw "Expecting hostData in recovery member to be $($reportResponse.hostData)  but value is $($crm.recoveryService.hostData)."
            }
        }

        $crm.encryptionPublicKey | Out-File $sandbox_common/${recoveryMemberName}_enc_pubk.pem
        $crm.signingCert | Out-File $sandbox_common/${recoveryMemberName}_cert.pem
        $crmData = @{}
        $crmData.identifier = $recoveryMemberName
        $crmData.is_recovery_operator = $true
        $crmData.recovery_service = $crm.recoveryService | ConvertTo-Json -Depth 100 | ConvertFrom-Json
        $crmData | ConvertTo-Json -Depth 100 | Out-File $sandbox_common/${recoveryMemberName}_member_data.json

        $proposal = (az cleanroom governance member add `
                --certificate $sandbox_common/${recoveryMemberName}_cert.pem `
                --encryption-public-key $sandbox_common/${recoveryMemberName}_enc_pubk.pem `
                --member-data $sandbox_common/${recoveryMemberName}_member_data.json `
                --governance-client $cgsProjectName | ConvertFrom-Json)
        if ($proposal.proposalState -ne "Accepted") {
            # Assuming deploy-cgs.ps1 script was executed, it adds 1 active member so attempt to accept the proposal via that.
            Write-Output "set_member proposal state is '$($proposal.proposalState)'. Launching governance client and accepting as $initialMemberName."
            az cleanroom governance client deploy `
                --ccf-endpoint $ccfEndpoint `
                --signing-key $sandbox_common/${initialMemberName}_privk.pem `
                --signing-cert $sandbox_common/${initialMemberName}_cert.pem `
                --service-cert $sandbox_common/service_cert.pem `
                --name $initialMemberProjectName
            $proposal = (az cleanroom governance proposal vote `
                    --proposal-id $proposal.proposalId `
                    --action accept `
                    --governance-client $initialMemberProjectName | ConvertFrom-Json)
            if ($proposal.proposalState -ne "Accepted") {
                # As long as there are no active members the default constitution is setup to auto-accept all proposals
                # so one is not expecting the proposal to remain in open state.
                throw "Expecting $($proposal.proposalId) to be in Accepted state but state is $($proposal.proposalState)."
            }
        }

        Write-Host "Requesting CCF recovery service to activate its membership in the network."
        az cleanroom ccf network recovery-agent activate-member `
            --network-name $networkName `
            --member-name $recoveryMemberName `
            --infra-type $infraType `
            --agent-config $sandbox_common/recoveryAgentConfig.json `
            --provider-config $sandbox_common/providerConfig.json

        az cleanroom ccf network set-recovery-threshold `
            --name $networkName `
            --infra-type $infraType `
            --recovery-threshold 1 `
            --provider-config $sandbox_common/providerConfig.json
    }

    pwsh $PSScriptRoot/verify-recovery-operators.ps1 `
        -recoveryServiceName $recoveryServiceName `
        -recoveryMemberName $recoveryMemberName

}

# Open the network as the operator.
az cleanroom ccf network transition-to-open `
    --name $networkName `
    --infra-type $infraType `
    --provider-config $sandbox_common/providerConfig.json

if (!$NoTest) {
    az cleanroom ccf network show `
        --name $networkName `
        --infra-type $infraType `
        --provider-config $sandbox_common/providerConfig.json

    Write-Output "Triggering a snapshot before scaling up."
    az cleanroom ccf network trigger-snapshot `
        --name $networkName `
        --infra-type $infraType `
        --provider-config $sandbox_common/providerConfig.json

    $scaleUpBy = 2
    $newNodeCount = $nodeCount + $scaleUpBy
    Write-Output "Scaling up the cluster from $nodeCount to $newNodeCount."
    az cleanroom ccf network update `
        --name $networkName `
        --node-count $newNodeCount `
        --node-log-level $nodeLogLevel `
        --security-policy-creation-option $securityPolicyCreationOption `
        --infra-type $infraType `
        --provider-config $sandbox_common/providerConfig.json
    $network = (az cleanroom ccf network show `
            --name $networkName `
            --infra-type $infraType `
            --provider-config $sandbox_common/providerConfig.json) | ConvertFrom-Json
    if ($network.nodeCount -ne $newNodeCount) {
        throw "Expecting $newNodeCount but $($network.nodeCount) is reported."
    }

    if ($nodeStorageType -ne "localfs") {
        $expectedFromSnapshot = $scaleUpBy
        Write-Output "Checking that $expectedFromSnapshot node(s) started from a snapshot."
        $startedFromSnapshot = 0
        foreach ($node in $network.nodes) {
            $nodeState = curl -s -k https://$node/node/state | ConvertFrom-Json
            $startup_seqno = $nodeState.startup_seqno
            if ($startup_seqno -gt 0) {
                Write-Output "Node $node started from startup_seqno $startup_seqno."
                $startedFromSnapshot++
            }
        }

        if ($startedFromSnapshot -ne $expectedFromSnapshot) {
            throw "Expecting $expectedFromSnapshot node(s) to have started from a snapshot but only found $startedFromSnapshot."
        }
    }

    Write-Output "Scaling down the cluster from $newNodeCount to $nodeCount."
    az cleanroom ccf network update `
        --name $networkName `
        --node-count $nodeCount `
        --node-log-level $nodeLogLevel `
        --security-policy-creation-option $securityPolicyCreationOption `
        --infra-type $infraType `
        --provider-config $sandbox_common/providerConfig.json
    $network = (az cleanroom ccf network show `
            --name $networkName `
            --infra-type $infraType `
            --provider-config $sandbox_common/providerConfig.json) | ConvertFrom-Json
    if ($network.nodeCount -ne $nodeCount) {
        throw "Expecting $nodeCount but $network.nodeCount is reported."
    }
}

Write-Output "Network health:"
az cleanroom ccf network show-health `
    --name $networkName `
    --infra-type $infraType `
    --provider-config $sandbox_common/providerConfig.json

if ($confidentialRecovery) {
    Write-Output "Recovery agent report:"
    az cleanroom ccf network recovery-agent show-report `
        --name $networkName `
        --infra-type $infraType `
        --provider-config $sandbox_common/providerConfig.json
}

Write-Output "CCF network deployed:"
az cleanroom ccf network show `
    --name $networkName `
    --infra-type $infraType `
    --provider-config $sandbox_common/providerConfig.json
