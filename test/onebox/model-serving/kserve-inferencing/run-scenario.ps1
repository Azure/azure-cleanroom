[CmdletBinding()]
param
(
    [string]
    $outDir = "$PSScriptRoot/generated",

    [Parameter(Mandatory)]
    [string]
    $ccfEndpoint,

    [Parameter(Mandatory)]
    [string]
    $ownerClient,

    [string]
    $deploymentConfigDir = "$PSScriptRoot/../../workloads/generated",

    [string]
    $datastoreOutdir = "",

    [string]
    $contractId = "kserve-inferencing",

    [ValidateSet('mcr', 'local', 'acr')]
    [string]$registry = "local",

    [string]$repo = "localhost:5000",

    [string]$tag = "latest",

    [switch]
    $withSecurityPolicy,

    [string]
    $models = "all",

    [string]
    $flexNodeVmSize = ""
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
mkdir -p $outDir

$ccfOutDir = "$deploymentConfigDir/ccf"
$clClusterOutDir = "$deploymentConfigDir/cl-cluster"

$resourceGroupTags = ""
if ($env:GITHUB_ACTIONS -eq "true") {
    $publisherResourceGroup = "cl-ob-publisher-kserve-inferencing-${env:JOB_ID}-${env:RUN_ID}"
    $resourceGroupTags = "github_actions=${env:JOB_ID}-${env:RUN_ID}"
}
else {
    $user = $env:CODESPACES -eq "true" ? $env:GITHUB_USER : $env:USER
    $publisherResourceGroup = "cl-ob-publisher-kserve-inferencing-${user}"
}

if ($datastoreOutdir -eq "") {
    $datastoreOutdir = "$outDir/datastores"
}

$serviceCert = $ccfOutDir + "/service_cert.pem"
if (-not (Test-Path -Path $serviceCert)) {
    throw "serviceCert at $serviceCert does not exist."
}

rm -rf "$datastoreOutdir"
mkdir -p "$datastoreOutdir"
$publisherDatastoreConfig = "$datastoreOutdir/model-publisher-datastore-config"

# Remove stale config files from previous runs to avoid confusion.
Remove-Item -Path "$outDir/collaboration-config-*.yaml" -Force -ErrorAction SilentlyContinue
$runId = (New-Guid).ToString().Substring(0, 8)
$env:CLEANROOM_COLLABORATION_CONFIG_FILE = "$outDir/collaboration-config-$runId.yaml"

pwsh $PSScriptRoot/setup-kfserving-examples-storage.ps1 -outDir $outDir

$publisherSaResult = Get-Content "$outDir/sa-resources.generated.json" | ConvertFrom-Json
pwsh $PSScriptRoot/setup-kfserving-examples-mi.ps1 `
    -resourceGroup $publisherResourceGroup `
    -storageAccountName $publisherSaResult.sa.name `
    -resourceGroupTags $resourceGroupTags `
    -outDir $outDir

# Start a local IDP server that can provide token to local users.
$idpPort = "8399"
pwsh $root/test/onebox/multi-party-collab/setup-local-idp.ps1 `
    -outDir $outDir `
    -repo $repo `
    -tag $tag `
    -idpPort $idpPort `
    -cgsProjectName $ownerClient

if ($env:CODESPACES -ne "true" -and $env:GITHUB_ACTIONS -ne "true") {
    $localIdpEndpoint = "http://host.docker.internal:$idpPort"
}
else {
    # 172.17.0.1: https://stackoverflow.com/questions/48546124/what-is-the-linux-equivalent-of-host-docker-internal
    $localIdpEndpoint = "http://172.17.0.1:$idpPort"
}

# Add "publisher" user to the CCF.
$publisherTenantId = [Guid]::NewGuid().ToString()
$publisherUserId = [Guid]::NewGuid().ToString("N")
Write-Output "Adding user $publisherUserId with tenant Id: $publisherTenantId in CCF."
$proposalId = (az cleanroom governance user-identity add `
        --object-id $publisherUserId `
        --identifier publisher `
        --tenant-id $publisherTenantId `
        --account-type microsoft `
        --governance-client $ownerClient `
        --query "proposalId" --output tsv)
az cleanroom governance proposal vote --proposal-id $proposalId --action accept --governance-client $ownerClient

Write-Output "Starting cgs-client for the publisher"
$publisherProjectName = "ob-kserve-inferencing-publisher-user-client"
$envFilePath = "$ccfOutDir/governance-client.env"
# Remove the project so as to avoid any caching of oids.
az cleanroom governance client remove --name $publisherProjectName
az cleanroom governance client deploy `
    --ccf-endpoint $ccfEndpoint `
    --use-local-identity `
    --local-identity-endpoint "$localIdpEndpoint/oauth/token?oid=$publisherUserId&tid=$publisherTenantId" `
    --service-cert $serviceCert `
    --name $publisherProjectName `
    --env-file $envFilePath

Write-Output "Publisher details"
az cleanroom governance user-identity show --identity-id $publisherUserId --governance-client $publisherProjectName

az cleanroom collaboration context add `
    --collaboration-name $publisherProjectName `
    --collaborator-id $publisherUserId `
    --governance-client $publisherProjectName

# Add the model datastore.
# Use the storage account created via setup-kfserving-examples-storage.ps1 and
# MI created via setup-kfserving-examples-mi.ps1.
$publisherResult = Get-Content "$outDir/sa-resources.generated.json" | ConvertFrom-Json
$publisherMiResult = Get-Content "$outDir/mi-resources.generated.json" | ConvertFrom-Json
$sseDatastoreName = "publisher-model-input-sse"
$kfServingExamplesStorageContainerName = "kfserving-examples"
$schemaFields = "date:date,time:string,author:string,mentions:string" # TODO (gsinha): What to do about schema?
$format = "csv"

az cleanroom datastore add `
    --name $sseDatastoreName `
    --config $publisherDatastoreConfig `
    --encryption-mode SSE `
    --backingstore-type Azure_BlobStorage `
    --backingstore-id $publisherResult.sa.id `
    --schema-format $format `
    --schema-fields $schemaFields `
    --container-name $kfServingExamplesStorageContainerName

# Use CCF service certificate discovery to dynamically figure out the CCF network's
# service certificate.
$agent = Get-Content $ccfOutDir/ccf.recovery-agent.json | ConvertFrom-Json
$agentEndpoint = $agent.endpoint
$agentNetworkReport = curl --fail-with-body -k -s -S $agentEndpoint/network/report | ConvertFrom-Json
$reportDataContent = $agentNetworkReport.reportDataPayload | base64 -d | ConvertFrom-Json

# Propose a contract for the cleanroom cluster.
$recoveryMembers = az cleanroom governance member show --governance-client $ownerClient | jq '[.value[] | select(.publicEncryptionKey != null) | .memberId]' -c
@"
{
  "ccrgovEndpoint": "$ccfEndpoint",
  "ccrgovApiPathPrefix": "/app/contracts/$contractId",
  "ccrgovServiceCertDiscovery" : {
    "endpoint": "$agentEndpoint/network/report",
    "snpHostData": "$($agent.snpHostData)",
    "constitutionDigest": "$($reportDataContent.constitutionDigest)",
    "jsappBundleDigest": "$($reportDataContent.jsappBundleDigest)"
  },
  "ccfNetworkRecoveryMembers": $recoveryMembers
}
"@ > $clClusterOutDir/contract.json

$data = Get-Content -Raw $clClusterOutDir/contract.json
Write-Output "Creating contract $contractId..."
az cleanroom governance contract create `
    --data "$data" `
    --id $contractId `
    --governance-client $ownerClient

# Submitting a contract proposal.
$version = (az cleanroom governance contract show `
        --id $contractId `
        --query "version" `
        --output tsv `
        --governance-client $ownerClient)

az cleanroom governance contract propose `
    --version $version `
    --id $contractId `
    --governance-client $ownerClient

$contract = (az cleanroom governance contract show `
        --id $contractId `
        --governance-client $ownerClient | ConvertFrom-Json)

# Accept it.
az cleanroom governance contract vote `
    --id $contractId `
    --proposal-id $contract.proposalId `
    --action accept `
    --governance-client $ownerClient

Write-Output "Enabling CA..."
az cleanroom governance ca propose-enable `
    --contract-id $contractId `
    --governance-client $ownerClient

# Vote on the proposed CA enable.
$proposalId = az cleanroom governance ca show `
    --contract-id $contractId `
    --governance-client $ownerClient `
    --query "proposalIds[0]" `
    --output tsv

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $ownerClient

az cleanroom governance ca generate-key `
    --contract-id $contractId `
    --governance-client $ownerClient

az cleanroom governance ca show `
    --contract-id $contractId `
    --governance-client $ownerClient `
    --query "caCert" `
    --output tsv > $outDir/cleanroomca.crt

# Enable signing and generate signing key.
Write-Output "Enabling signing..."
$signingProposalId = az cleanroom governance signing propose-enable `
    --governance-client $ownerClient `
    --query "proposalId" `
    --output tsv

az cleanroom governance proposal vote `
    --proposal-id $signingProposalId `
    --action accept `
    --governance-client $ownerClient

Write-Output "Generating signing key..."
az cleanroom governance signing generate-signing-key `
    --governance-client $ownerClient

Write-Output "Downloading signing public key..."
az cleanroom governance signing show `
    --governance-client $ownerClient `
    --query "publicKeyPem" `
    --output tsv > $outDir/policy-signing-cert.pem

mkdir -p $outDir/deployments
$repoConfig = Get-Content $clClusterOutDir/repoConfig.json | ConvertFrom-Json
$clusterProviderProjectName = $repoConfig.clusterProviderProjectName

if ($withSecurityPolicy) {
    $option = "cached-debug"
}
else {
    $option = "allow-all"
}

$clCluster = Get-Content $clClusterOutDir/cl-cluster.json | ConvertFrom-Json

Write-Output "Generating deployment template/policy with $option creation option for kserve inferencing workload..."
az cleanroom cluster kserve-inferencing-workload deployment generate `
    --contract-id $contractId `
    --governance-client $ownerClient `
    --output-dir $outDir/deployments `
    --security-policy-creation-option $option `
    --infra-type $clCluster.infraType `
    --provider-client $clusterProviderProjectName `
    --provider-config $clClusterOutDir/providerConfig.json

Write-Output "Setting deployment template..."
az cleanroom governance deployment template propose `
    --contract-id $contractId `
    --template-file $outDir/deployments/kserve-inferencing-workload.deployment-template.json `
    --governance-client $ownerClient

# Vote on the proposed deployment template.
$proposalId = az cleanroom governance deployment template show `
    --contract-id $contractId `
    --governance-client $ownerClient `
    --query "proposalIds[0]" `
    --output tsv

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $ownerClient

Write-Output "Setting clean room policy..."
az cleanroom governance deployment policy propose `
    --policy-file $outDir/deployments/kserve-inferencing-workload.governance-policy.json `
    --contract-id $contractId `
    --governance-client $ownerClient

# Vote on the proposed cce policy.
$proposalId = az cleanroom governance deployment policy show `
    --contract-id $contractId `
    --governance-client $ownerClient `
    --query "proposalIds[0]" `
    --output tsv

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $ownerClient

# Section: Publisher publishes the model datasets.
$identity = $(az resource show --ids $publisherMiResult.mi.id --query "properties") | ConvertFrom-Json

# Create identity entry in the configuration.
pwsh $PSScriptRoot/setup-oidc-issuer-for-user.ps1 `
    -oidcContainerName $publisherResourceGroup `
    -outDir "$outDir/$publisherResourceGroup" `
    -governanceClient $publisherProjectName

$publisherIssuerUrl = Get-Content "$outDir/$publisherResourceGroup/issuer-url.txt"

az cleanroom collaboration context set `
    --collaboration-name $publisherProjectName

az cleanroom collaboration identity add az-federated `
    --identity-name publisher-identity `
    --client-id $identity.clientId `
    --tenant-id $identity.tenantId `
    --token-issuer-url $publisherIssuerUrl `
    --backing-identity cleanroom_cgs_oidc

$publisherInputSseDatasetName = "publisher-input-sse-$runId"
az cleanroom collaboration dataset publish `
    --contract-id $contractId `
    --dataset-name $publisherInputSseDatasetName `
    --datastore-name $sseDatastoreName `
    --identity-name publisher-identity `
    --policy-access-mode read `
    --policy-allowed-fields "date,author,mentions" `
    --datastore-config-file $publisherDatastoreConfig

# Create an ad-hoc inferencing document till we figure out the document schema for inferencing models.
$inferencingModelDocumentId = "inferencing-model-$runId"
@"
{
  "name": "$inferencingModelDocumentId",
  "application": {
    "applicationType": "KServe-Inferencing",
    "modelDir": "$publisherInputSseDatasetName/models/sklearn/1.0/model",
    "inputDataset": [
      {
        "specification": "$publisherInputSseDatasetName"
      }
    ]
  }
}
"@ > $outDir/inferencingModelConfig.json

# , @({...}) generates an array of objects in json for the approvers list for the user document.
, @(
    @{
        "id"   = "$publisherUserId"
        "type" = "user"
    }
) | ConvertTo-Json -Depth 100 | Out-File $outDir/publisher-inferencing-model-approvers.json
$modelConfigDocument = Get-Content -Raw $outDir/inferencingModelConfig.json

Write-Output "Adding user document for infrencing model with approvers as $(Get-Content -Raw $outDir/publisher-inferencing-model-approvers.json)..."
az cleanroom governance user-document create `
    --data $modelConfigDocument `
    --id $inferencingModelDocumentId `
    --approvers $outDir/publisher-inferencing-model-approvers.json `
    --contract-id $contractId `
    --governance-client $publisherProjectName

Write-Output "Submitting user document proposal for inferencing model"
$version = (az cleanroom governance user-document show `
        --id $inferencingModelDocumentId `
        --governance-client $publisherProjectName `
        --query "version" `
        --output tsv)
$proposalId = (az cleanroom governance user-document propose `
        --version $version `
        --id $inferencingModelDocumentId `
        --governance-client $publisherProjectName `
        --query "proposalId" `
        --output tsv)

Write-Output "Accepting the user document proposal for inferencing model as publisher"
az cleanroom governance user-document vote `
    --id $inferencingModelDocumentId `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $publisherProjectName

@"
{
    "cgsClient": "$publisherProjectName"
}
"@ > $outDir/deployModelConfig.json

# Setup OIDC issuer and managed identity access to storage in publisher tenant.
$subject = $contractId + "-" + $publisherUserId
pwsh $PSScriptRoot/setup-access.ps1 `
    -managedIdentityResourceGroup $publisherMiResult.mi.resourceGroup `
    -managedIdentityName $publisherMiResult.mi.name `
    -storageAccountName $publisherResult.sa.name `
    -storageAccountResourceGroup $publisherResult.sa.resourceGroup `
    -governanceClient $publisherProjectName `
    -subject $subject `
    -issuerUrl $publisherIssuerUrl `
    -outDir $outDir

Write-Output "Enabling flex node on the cluster..."
$enableFlexNodeArgs = @(
    "-outDir", $clClusterOutDir,
    "-policySigningCertPath", "$outDir/policy-signing-cert.pem"
)

if ($flexNodeVmSize -ne "") {
    $enableFlexNodeArgs += @("-flexNodeVmSize", $flexNodeVmSize)
}

pwsh $root/samples/workloads/azcli/enable-flex-node.ps1 @enableFlexNodeArgs

# Deploy the inferencing agent using the CGS /deploymentspec endpoint as the inferencing config endpoint.
@"
{
    "url": "${ccfEndpoint}/app/contracts/$contractId/deploymentspec",
    "caCert": "$((Get-Content $serviceCert -Raw).ReplaceLineEndings("\n"))"
}
"@ > $outDir/kserve-inferencing-workload-config-endpoint.json

pwsh $root/samples/workloads/azcli/enable-kserve-inferencing-workload.ps1 `
    -outDir $clClusterOutDir `
    -securityPolicyCreationOption $option `
    -configEndpointFile $outDir/kserve-inferencing-workload-config-endpoint.json

# Fetch latest info about the cluster updated by the above command.
Write-Output "Fetching deployment information..."
$clCluster = Get-Content $clClusterOutDir/cl-cluster.json | ConvertFrom-Json
$inferencingEndpoint = $clCluster.inferencingWorkloadProfile.kserveProfile.endpoint
Write-Output "Fetched inferencing endpoint: $inferencingEndpoint"

#
# Instead of accessing the service via the public endpoint, we will use kubectl proxy to access it via localhost.
# This is needed as the public IP address for AKS load balancer is not accessible from machines that are not on corpnet.
# https://kubernetes.io/docs/tasks/access-application-cluster/access-cluster-services/#manually-constructing-apiserver-proxy-urls
# For Kind cluster infra also this technique works fine to access the service as it would be having a clusterIP
# and thus not reachable from outside the cluster.
#
$inferencingEndpoint = "http://localhost:8282/api/v1/namespaces/kserve-inferencing-agent/services/https:kserve-inferencing-agent:443/proxy"

Write-Output "Using inferencing endpoint: $inferencingEndpoint"
$deploymentInformation = @{
    url = $inferencingEndpoint
} | ConvertTo-Json

Write-Output "Saving inferencing endpoint deployment information..."
az cleanroom governance deployment information propose `
    --deployment-information $deploymentInformation `
    --contract-id $contractId `
    --governance-client $ownerClient

# Vote on the proposed deployment information.
$proposalId = az cleanroom governance deployment information show `
    --contract-id $contractId `
    --governance-client $ownerClient `
    --query "proposalIds[0]" `
    --output tsv

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $ownerClient

@"
{
    "contractId": "$contractId",
    "modelDocumentId": "$inferencingModelDocumentId"
}
"@ > $outDir/ModelConfig.json

# Create governance document for GPT-2 LLM model (llama.cpp server).
$gpt2ModelDocumentId = "inferencing-gpt2-model-$runId"
@"
{
  "name": "$gpt2ModelDocumentId",
  "application": {
    "applicationType": "KServe-Inferencing",
    "modelDir": "$publisherInputSseDatasetName/models/gpt2-gguf/model.gguf",
    "inputDataset": [
      {
        "specification": "$publisherInputSseDatasetName"
      }
    ]
  }
}
"@ > $outDir/gpt2ModelConfig.json

$gpt2ModelConfigDocument = Get-Content -Raw $outDir/gpt2ModelConfig.json

Write-Output "Adding user document for GPT-2 model..."
az cleanroom governance user-document create `
    --data $gpt2ModelConfigDocument `
    --id $gpt2ModelDocumentId `
    --approvers $outDir/publisher-inferencing-model-approvers.json `
    --contract-id $contractId `
    --governance-client $publisherProjectName

Write-Output "Submitting user document proposal for GPT-2 model"
$gpt2Version = (az cleanroom governance user-document show `
        --id $gpt2ModelDocumentId `
        --governance-client $publisherProjectName `
        --query "version" `
        --output tsv)
$gpt2ProposalId = (az cleanroom governance user-document propose `
        --version $gpt2Version `
        --id $gpt2ModelDocumentId `
        --governance-client $publisherProjectName `
        --query "proposalId" `
        --output tsv)

Write-Output "Accepting the user document proposal for GPT-2 model as publisher"
az cleanroom governance user-document vote `
    --id $gpt2ModelDocumentId `
    --proposal-id $gpt2ProposalId `
    --action accept `
    --governance-client $publisherProjectName

@"
{
    "contractId": "$contractId",
    "modelDocumentId": "$gpt2ModelDocumentId"
}
"@ > $outDir/Gpt2ModelConfig.json

Write-Output "Deploying inferencing model..."
python3 -u $PSScriptRoot/deploy-models.py `
    --out-dir $outDir `
    --deployment-config-dir $deploymentConfigDir `
    --models $models