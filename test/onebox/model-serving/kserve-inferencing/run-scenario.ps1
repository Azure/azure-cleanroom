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

    [Parameter(Mandatory)]
    [string]
    $ownerName,

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
    $models = "default",

    [string]
    $flexNodeVmSize = "",

    [string]
    $location = "centralindia",

    [switch]
    $provisionFlexNodeUsingBakedImage,

    [switch]
    $noDelete
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

pwsh $PSScriptRoot/setup-kfserving-examples-storage.ps1 -outDir $outDir -models $models

$publisherSaResult = Get-Content "$outDir/sa-resources.generated.json" | ConvertFrom-Json
pwsh $PSScriptRoot/setup-kfserving-examples-mi.ps1 `
    -resourceGroup $publisherResourceGroup `
    -storageAccountName $publisherSaResult.sa.name `
    -resourceGroupTags $resourceGroupTags `
    -location $location `
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

# TEST ONLY: This is a single tenant scenario masquerading as a multi-tenant scenario.
# We will assert that the actual tenant where the resources exist is the same for all the involved parties.
$ownerTenantId = az account show --query "tenantId" --output tsv
if ($identity.tenantId -ne $ownerTenantId) {
    throw "Publisher's access identity tenant Id $($identity.tenantId) does not match owner's tenant Id $ownerTenantId."
}

$proposalId = (az cleanroom governance member set-tenant-id `
        --identifier $ownerName `
        --tenant-id $ownerTenantId `
        --query "proposalId" `
        --output tsv `
        --governance-client $ownerClient)
az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $ownerClient

# Set the tenant level OIDC value. This is also used later to setup federation to the publisher's and
# consumer's resources.
pwsh $PSScriptRoot/setup-oidc-issuer-for-user.ps1 `
    -oidcContainerName $publisherResourceGroup `
    -outDir "$outDir/$publisherResourceGroup" `
    -governanceClient $ownerClient

$issuerUrl = Get-Content "$outDir/$publisherResourceGroup/issuer-url.txt"

# Store the same issuer under the publisher user.
az cleanroom governance oidc-issuer set-issuer-url `
    --governance-client $publisherProjectName `
    --url $issuerUrl

az cleanroom collaboration context set `
    --collaboration-name $publisherProjectName

az cleanroom collaboration identity add az-federated `
    --identity-name publisher-identity `
    --client-id $identity.clientId `
    --tenant-id $identity.tenantId `
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

# Approvers list (publisher) reused for the inferencing model documents.
, @(
    @{
        "id"   = "$publisherUserId"
        "type" = "user"
    }
) | ConvertTo-Json -Depth 100 | Out-File $outDir/publisher-inferencing-model-approvers.json

# Helper: create + propose + accept a user document as the publisher.
function Publish-PublisherUserDocument {
    param(
        [Parameter(Mandatory)][string]$DocId,
        [Parameter(Mandatory)][string]$DataPath
    )

    $docContent = Get-Content -Raw $DataPath
    az cleanroom governance user-document create `
        --data $docContent `
        --id $DocId `
        --approvers $outDir/publisher-inferencing-model-approvers.json `
        --contract-id $contractId `
        --governance-client $publisherProjectName

    $version = (az cleanroom governance user-document show `
            --id $DocId `
            --governance-client $publisherProjectName `
            --query "version" `
            --output tsv)
    $proposalId = (az cleanroom governance user-document propose `
            --version $version `
            --id $DocId `
            --governance-client $publisherProjectName `
            --query "proposalId" `
            --output tsv)

    az cleanroom governance user-document vote `
        --id $DocId `
        --proposal-id $proposalId `
        --action accept `
        --governance-client $publisherProjectName | Out-Null
}

# Runtime image+digest is operational state pinned by the agent/frontend
# release version (the frontend resolves it from a bundled digest table
# at deploy time). The model document carries only the runtime name.

# Create the inferencing model governance document using the typed schema.
$inferencingModelDocumentId = "inferencing-model-$runId"
@"
{
  "name": "$inferencingModelDocumentId",
  "application": {
    "applicationType": "KServe-Inferencing",
    "modelDir": "$publisherInputSseDatasetName/models/sklearn/1.0/model",
    "modelDatasets": [
      {
        "specification": "$publisherInputSseDatasetName"
      }
    ],
    "runtime": {
      "name": "kserve-sklearnserver"
    }
  }
}
"@ > $outDir/inferencingModelConfig.json

Write-Output "Publishing inferencing model document '$inferencingModelDocumentId'..."
Publish-PublisherUserDocument `
    -DocId $inferencingModelDocumentId `
    -DataPath "$outDir/inferencingModelConfig.json"

# Setup OIDC issuer and managed identity access to storage in publisher tenant.
$subject = $contractId + "-" + $publisherUserId
pwsh $PSScriptRoot/setup-access.ps1 `
    -managedIdentityResourceGroup $publisherMiResult.mi.resourceGroup `
    -managedIdentityName $publisherMiResult.mi.name `
    -storageAccountName $publisherResult.sa.name `
    -storageAccountResourceGroup $publisherResult.sa.resourceGroup `
    -governanceClient $publisherProjectName `
    -subject $subject `
    -issuerUrl $issuerUrl `
    -outDir $outDir

Write-Output "Enabling flex node on the cluster..."
$enableFlexNodeArgs = @(
    "-outDir", $clClusterOutDir,
    "-policySigningCertPath", "$outDir/policy-signing-cert.pem"
)

if ($flexNodeVmSize -ne "") {
    $enableFlexNodeArgs += @("-flexNodeVmSize", $flexNodeVmSize)
}

# GPU VMs have limited availability — use a single flex node to test GPU workloads.
if ($flexNodeVmSize -like "Standard_NC*") {
    $enableFlexNodeArgs += @("-flexNodeCount", 1)
}
else {
    $enableFlexNodeArgs += @("-flexNodeCount", 2)
}
# Enable MPS GPU sharing when tinyllama-gpu is selected — it deploys
# minReplicas: 2, requiring at least 2 nvidia.com/gpu resources which
# MPS provides via replica advertisement. Other GPU models (gemma4-gpu,
# phi4-gpu) use minReplicas: 1 and don't need MPS.
$enabledModels = $models -split ","
if (($enabledModels -contains "tinyllama-gpu") -and $flexNodeVmSize -like "Standard_NC*") {
    $enableFlexNodeArgs += @("-gpuSharingMode", "mps", "-gpuMpsReplicas", 2)
}

# TODO (HPrabh): This forces the api-server-proxy to not check pod policies.
# The kserve pods currently do not support policies that can be enforced by the api-server-proxy.
# Remove this once we have proper policies in place and the kserve pods are annotated.
$enableFlexNodeArgs += @(
    "-insecure"
)

if (!$provisionFlexNodeUsingBakedImage) {
    $enableFlexNodeArgs += @("-provisionUsingSSH")
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

@"
{
    "cgsClient": "$publisherProjectName",
    "inferencingAgentEndpoint": "$inferencingEndpoint"
}
"@ > $outDir/deployModelConfig.json

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

$enabledModels = $models -split ","
$runDefault = $enabledModels -contains "default"
$runTinyLlamaCpu = $runDefault -or $enabledModels -contains "tinyllama"
$runTinyLlamaGpu = $enabledModels -contains "tinyllama-gpu"
$runGemma4 = $enabledModels -contains "gemma4-gpu"
$runPhi4 = $enabledModels -contains "phi4-gpu"

# Helper: create, propose, and accept a model governance document.
function New-ModelGovernanceDocument(
    $displayName,
    $docId,
    $modelDir,
    $runtimeName,
    $configOutputFile) {
    @"
{
  "name": "$docId",
  "application": {
    "applicationType": "KServe-Inferencing",
    "modelDir": "$modelDir",
    "modelDatasets": [
      {
        "specification": "$publisherInputSseDatasetName"
      }
    ],
    "runtime": {
      "name": "$runtimeName"
    }
  }
}
"@ > $outDir/${docId}.json

    Write-Output "Publishing user document '$docId' for $displayName model..."
    Publish-PublisherUserDocument `
        -DocId $docId `
        -DataPath "$outDir/${docId}.json"

    @"
{
    "contractId": "$contractId",
    "modelDocumentId": "$docId"
}
"@ > $configOutputFile
}

if ($runTinyLlamaCpu) {
    New-ModelGovernanceDocument `
        -displayName "TinyLlama-1.1B-Chat (CPU)" `
        -docId "inferencing-tinyllama-cpu-model-$runId" `
        -modelDir "$publisherInputSseDatasetName/models/tinyllama-chat-gguf/model.gguf" `
        -runtimeName "llamacpp-server" `
        -configOutputFile "$outDir/TinyLlamaCpuModelConfig.json"
}

if ($runTinyLlamaGpu) {
    New-ModelGovernanceDocument `
        -displayName "TinyLlama-1.1B-Chat (GPU)" `
        -docId "inferencing-tinyllama-gpu-model-$runId" `
        -modelDir "$publisherInputSseDatasetName/models/tinyllama-chat-gguf/model.gguf" `
        -runtimeName "llamacpp-server-cuda" `
        -configOutputFile "$outDir/TinyLlamaGpuModelConfig.json"
}

if ($runGemma4) {
    New-ModelGovernanceDocument `
        -displayName "Gemma 4 31B-IT" `
        -docId "inferencing-gemma4-model-$runId" `
        -modelDir "$publisherInputSseDatasetName/models/gemma4-31b-it" `
        -runtimeName "vllm-openai" `
        -configOutputFile "$outDir/Gemma4ModelConfig.json"
}

if ($runPhi4) {
    New-ModelGovernanceDocument `
        -displayName "Phi-4 14B" `
        -docId "inferencing-phi4-model-$runId" `
        -modelDir "$publisherInputSseDatasetName/models/phi-4-14b" `
        -runtimeName "vllm-openai" `
        -configOutputFile "$outDir/Phi4ModelConfig.json"
}

Write-Output "Deploying inferencing model..."
$deployModelArgs = @(
    "--out-dir", $outDir,
    "--deployment-config-dir", $deploymentConfigDir,
    "--models", $models
)
if ($noDelete) {
    $deployModelArgs += "--no-delete"
}
python3 -u $PSScriptRoot/deploy-models.py @deployModelArgs
