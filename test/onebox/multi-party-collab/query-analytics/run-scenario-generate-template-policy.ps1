[CmdletBinding()]
param
(
    [string]
    $outDir = "$PSScriptRoot/generated",

    [Parameter(Mandatory)]
    [string]
    $ccfEndpoint,

    [string]
    $ccfOutDir = "",

    [string]
    $contractId = "collab1",

    [ValidateSet('mcr', 'local', 'acr')]
    [string]$registry = "local",

    [string]$repo = "localhost:5000",

    [string]$tag = "latest",

    [switch]
    $withSecurityPolicy
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# This script assumes a CCF instance was deployed in docker with the initial member that acts as the
# consumer for the multi-party collab sample.
$root = git rev-parse --show-toplevel
$collabSamplePath = "$root/samples/multi-party-collab"
$consumerDataSamplePath = "$root/samples/multi-party-collab/scenarios/analytics/consumer-demo/consumer-input"
$publisherDataSamplePath = "$root/samples/multi-party-collab/scenarios/analytics/publisher-demo/publisher-input"

if ($ccfOutDir -eq "") {
    $ccfOutDir = "$root/test/onebox/multi-party-collab/query-analytics/generated/ccf"
}

$serviceCert = $ccfOutDir + "/service_cert.pem"
if (-not (Test-Path -Path $serviceCert)) {
    throw "serviceCert at $serviceCert does not exist."
}

mkdir -p "$outDir/configurations"
$publisherConfig = "$outDir/configurations/publisher-config"
$consumerConfig = "$outDir/configurations/consumer-config"
$datastoreOutdir = "$outDir/datastores"
mkdir -p "$datastoreOutdir"
$publisherDatastoreConfig = "$datastoreOutdir/analytics-publisher-datastore-config"
$consumerDatastoreConfig = "$datastoreOutdir/analytics-consumer-datastore-config"

mkdir -p "$datastoreOutdir/keys"
$publisherKeyStore = "$datastoreOutdir/keys/analytics-publisher-datastore-config-publisher-keys"
$consumerKeyStore = "$datastoreOutdir/keys/analytics-publisher-datastore-config-consumer-keys"

$resourceGroupTags = ""
if ($env:GITHUB_ACTIONS -eq "true") {
    $publisherResourceGroup = "cl-ob-publisher-${env:JOB_ID}-${env:RUN_ID}"
    $consumerResourceGroup = "cl-ob-consumer-${env:JOB_ID}-${env:RUN_ID}"
    $resourceGroupTags = "github_actions=multi-party-collab-${env:JOB_ID}-${env:RUN_ID}"
}
else {
    $publisherResourceGroup = "cl-ob-publisher-${env:USER}"
    $consumerResourceGroup = "cl-ob-consumer-${env:USER}"
}

# Set tenant Id as a part of the consumer's member data.
# This is required to enable OIDC provider in the later steps.
$consumerTenantId = az account show --query "tenantId" --output tsv
$proposalId = (az cleanroom governance member set-tenant-id `
        --identifier consumer `
        --tenant-id $consumerTenantId `
        --query "proposalId" `
        --output tsv `
        --governance-client "ob-consumer-client")

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client "ob-consumer-client"

# Publisher identity creation.
if (-not (Test-Path -Path "$ccfOutDir/publisher_cert.pem")) {
    az cleanroom governance member keygenerator-sh | bash -s -- --name "publisher" --gen-enc-key --out "$ccfOutDir"
}

# Invite publisher to the consortium.
$publisherTenantId = az account show --query "tenantId" --output tsv

# "consumer" member makes a proposal for adding the new member "publisher".
$proposalId = (az cleanroom governance member add `
        --certificate $ccfOutDir/publisher_cert.pem `
        --encryption-public-key $ccfOutDir/publisher_enc_pubk.pem `
        --identifier "publisher" `
        --tenant-id $publisherTenantId `
        --query "proposalId" `
        --output tsv `
        --governance-client "ob-consumer-client")

# Vote on the above proposal to accept the membership.
az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client "ob-consumer-client"

# "publisher" deploys client-side containers to interact with the governance service as the new member.
# Set overrides if local registry is to be used for CGS images.
if ($registry -eq "local") {
    $localTag = cat "$ccfOutDir/local-registry-tag.txt"
    $env:AZCLI_CGS_CLIENT_IMAGE = "$repo/cgs-client:$localTag"
    $env:AZCLI_CGS_UI_IMAGE = "$repo/cgs-ui:$localTag"
}
elseif ($registry -eq "acr") {
    $env:AZCLI_CGS_CLIENT_IMAGE = "$repo/cgs-client:$tag"
    $env:AZCLI_CGS_UI_IMAGE = "$repo/cgs-ui:$tag"
}

az cleanroom governance client deploy `
    --ccf-endpoint $ccfEndpoint `
    --signing-cert $ccfOutDir/publisher_cert.pem `
    --signing-key $ccfOutDir/publisher_privk.pem `
    --service-cert $ccfOutDir/service_cert.pem `
    --name "ob-publisher-client"

# "publisher" accepts the invitation and becomes an active member in the consortium.
az cleanroom governance member activate --governance-client "ob-publisher-client"

# Update the recovery threshold of the network to include all the active members.
$newThreshold = 2
$proposalId = (az cleanroom governance network set-recovery-threshold `
        --recovery-threshold $newThreshold `
        --query "proposalId" `
        --output tsv `
        --governance-client "ob-publisher-client")

# Vote on the above proposal to accept the new threshold.
az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client "ob-publisher-client"

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client "ob-consumer-client"

$recoveryThreshold = (az cleanroom governance network show `
        --query "configuration.recoveryThreshold" `
        --output tsv `
        --governance-client "ob-publisher-client")
if ($recoveryThreshold -ne $newThreshold) {
    throw "Expecting recovery threshold to be $newThreshold but value is $recoveryThreshold."
}

# Create storage account, KV and MI resources.
pwsh $collabSamplePath/prepare-resources.ps1 `
    -resourceGroup $publisherResourceGroup `
    -resourceGroupTags $resourceGroupTags `
    -kvType akvpremium `
    -outDir $outDir
$result = Get-Content "$outDir/$publisherResourceGroup/resources.generated.json" | ConvertFrom-Json
az cleanroom datastore add `
    --name publisher-input `
    --config $publisherDatastoreConfig `
    --keystore $publisherKeyStore `
    --encryption-mode CPK `
    --backingstore-type Azure_BlobStorage `
    --backingstore-id $result.sa.id

# Encrypt and upload content.
az cleanroom datastore upload `
    --name publisher-input `
    --config $publisherDatastoreConfig `
    --src $publisherDataSamplePath

# Build the cleanroom config for the publisher.
az cleanroom config init --cleanroom-config $publisherConfig

# Create a KEK entry in the configuration.
az cleanroom config set-kek `
    --kek-key-vault $result.kek.kv.id `
    --maa-url $result.maa_endpoint `
    --cleanroom-config $publisherConfig

$identity = $(az resource show --ids $result.mi.id --query "properties") | ConvertFrom-Json

# Create identity entry in the configuration.
az cleanroom config add-identity az-federated `
    --cleanroom-config $publisherConfig `
    -n publisher-identity `
    --client-id $identity.clientId `
    --tenant-id $identity.tenantId `
    --backing-identity cleanroom_cgs_oidc

# Create a datasource entry in the configuration.
az cleanroom config add-datasource `
    --cleanroom-config $publisherConfig `
    --datastore-name publisher-input `
    --datastore-config $publisherDatastoreConfig `
    --identity publisher-identity `
    --key-vault $result.dek.kv.id

$containerSuffix = $($($(New-Guid).Guid) -replace '-').ToLower()
Write-Host "Using container suffix {$containerSuffix} for application-telemetry"

# $result below refers to the output of the prepare-resources.ps1 that was run earlier.
az cleanroom config set-logging `
    --cleanroom-config $publisherConfig `
    --storage-account $result.sa.id `
    --identity publisher-identity `
    --key-vault $result.dek.kv.id `
    --datastore-config $publisherDatastoreConfig `
    --datastore-keystore $publisherKeyStore `
    --encryption-mode CPK `
    --container-suffix $containerSuffix

$containerSuffix = $($($(New-Guid).Guid) -replace '-').ToLower()
Write-Host "Using container suffix {$containerSuffix} for infrastructure-telemetry"
az cleanroom config set-telemetry `
    --cleanroom-config $publisherConfig `
    --storage-account $result.sa.id `
    --identity publisher-identity `
    --key-vault $result.dek.kv.id `
    --datastore-config $publisherDatastoreConfig `
    --datastore-keystore $publisherKeyStore `
    --encryption-mode CPK `
    --container-suffix $containerSuffix

# Create storage account, KV and MI resources.
pwsh $collabSamplePath/prepare-resources.ps1 `
    -resourceGroup $consumerResourceGroup `
    -resourceGroupTags $resourceGroupTags `
    -kvType akvpremium `
    -outDir $outDir
$result = Get-Content "$outDir/$consumerResourceGroup/resources.generated.json" | ConvertFrom-Json

$containerName = $($($(New-Guid).Guid) -replace '-').ToLower()
Write-Host "Using container name {$containerName} for datastore {consumer-input}"
# Create a datasource entry.
az cleanroom datastore add `
    --name consumer-input `
    --config $consumerDatastoreConfig `
    --keystore $consumerKeyStore `
    --encryption-mode CPK `
    --backingstore-type Azure_BlobStorage `
    --backingstore-id $result.sa.id `
    --container-name $containerName

az cleanroom datastore add `
    --name consumer-output `
    --config $consumerDatastoreConfig `
    --keystore $consumerKeyStore `
    --encryption-mode CPK `
    --backingstore-type Azure_BlobStorage `
    --backingstore-id $result.sa.id

# Build the cleanroom config for the publisher.
az cleanroom config init --cleanroom-config $consumerConfig

# Create a KEK entry in the configuration.
az cleanroom config set-kek `
    --kek-key-vault $result.kek.kv.id `
    --maa-url $result.maa_endpoint `
    --cleanroom-config $consumerConfig

$identity = $(az resource show --ids $result.mi.id --query "properties") | ConvertFrom-Json

# Create identity entry in the configuration.
az cleanroom config add-identity az-federated `
    --cleanroom-config $consumerConfig `
    -n consumer-identity `
    --client-id $identity.clientId `
    --tenant-id $identity.tenantId `
    --backing-identity cleanroom_cgs_oidc

az cleanroom datastore upload `
    --name consumer-input `
    --config $consumerDatastoreConfig `
    --src $consumerDataSamplePath

# Create a datasource entry in the configuration.
az cleanroom config add-datasource `
    --cleanroom-config $consumerConfig `
    --datastore-name consumer-input `
    --datastore-config $consumerDatastoreConfig `
    --identity consumer-identity `
    --key-vault $result.dek.kv.id

# Create a datasink entry in the configuration.
az cleanroom config add-datasink `
    --cleanroom-config $consumerConfig `
    --datastore-name consumer-output `
    --datastore-config $consumerDatastoreConfig `
    --identity consumer-identity `
    --key-vault $result.dek.kv.id

az cleanroom config add-application `
    --cleanroom-config $consumerConfig `
    --name demo-app `
    --image "cleanroomsamples.azurecr.io/azure-cleanroom-samples/demos/analytics@sha256:260becd228b930c4bbaca348b1c226a563f58af7d97c67a963db6a1bf4a48212" `
    --command "python3.10 ./analytics.py" `
    --datasources "publisher-input=/mnt/remote/publisher-input" `
    "consumer-input=/mnt/remote/consumer-input" `
    --env-vars STORAGE_PATH_1=/mnt/remote/publisher-input `
    STORAGE_PATH_2=/mnt/remote/consumer-input `
    --cpu 0.5 `
    --memory 4

az cleanroom config add-application-endpoint `
    --cleanroom-config $consumerConfig `
    --application-name demo-app `
    --port 8310

# Generate the cleanroom config which contains all the datasources, sinks and applications that are
# configured by both the producer and consumer.
az cleanroom config view `
    --cleanroom-config $consumerConfig `
    --configs $publisherConfig `
    --out-file $outDir/configurations/cleanroom-config

az cleanroom config validate --cleanroom-config $outDir/configurations/cleanroom-config

$data = Get-Content -Raw $outDir/configurations/cleanroom-config
az cleanroom governance contract create `
    --data "$data" `
    --id $contractId `
    --governance-client "ob-consumer-client"

# Submitting a contract proposal.
$version = (az cleanroom governance contract show `
        --id $contractId `
        --query "version" `
        --output tsv `
        --governance-client "ob-consumer-client")

az cleanroom governance contract propose `
    --version $version `
    --id $contractId `
    --governance-client "ob-consumer-client"

$contract = (az cleanroom governance contract show `
        --id $contractId `
        --governance-client "ob-publisher-client" | ConvertFrom-Json)

# Accept it.
az cleanroom governance contract vote `
    --id $contractId `
    --proposal-id $contract.proposalId `
    --action accept `
    --governance-client "ob-publisher-client"

$contract = (az cleanroom governance contract show `
        --id $contractId `
        --governance-client "ob-consumer-client" | ConvertFrom-Json)

# Accept it.
az cleanroom governance contract vote `
    --id $contractId `
    --proposal-id $contract.proposalId `
    --action accept `
    --governance-client "ob-consumer-client"

mkdir -p $outDir/deployments
# Set overrides if local registry is to be used for clean room container images.
if ($registry -ne "mcr") {
    $env:AZCLI_CLEANROOM_CONTAINER_REGISTRY_URL = $repo
    $env:AZCLI_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL = "${repo}/sidecar-digests:$tag"
}

if ($withSecurityPolicy) {
    az cleanroom governance deployment generate `
        --contract-id $contractId `
        --governance-client "ob-consumer-client" `
        --output-dir $outDir/deployments
}
else {
    az cleanroom governance deployment generate `
        --contract-id $contractId `
        --governance-client "ob-consumer-client" `
        --output-dir $outDir/deployments `
        --security-policy-creation-option allow-all
}

if ($env:COLLAB_FORCE_MANAGED_IDENTITY -eq "true") {
    Import-Module $root/test/onebox/multi-party-collab/force-managed-identity.ps1 -Force -DisableNameChecking
    $publisherMi = (Get-Content "$outDir/$publisherResourceGroup/resources.generated.json" | ConvertFrom-Json).mi.id
    $consumerMi = (Get-Content "$outDir/$consumerResourceGroup/resources.generated.json" | ConvertFrom-Json).mi.id
    Force-Managed-Identity `
        -deploymentTemplateFile "$outDir/deployments/cleanroom-arm-template.json" `
        -managedIdentities @($publisherMi, $consumerMi)
}

az cleanroom governance deployment template propose `
    --template-file $outDir/deployments/cleanroom-arm-template.json `
    --contract-id $contractId `
    --governance-client "ob-consumer-client"

az cleanroom governance deployment policy propose `
    --policy-file $outDir/deployments/cleanroom-governance-policy.json `
    --contract-id $contractId `
    --governance-client "ob-consumer-client"

# Propose enabling log and telemetry collection during cleanroom execution.
az cleanroom governance contract runtime-option propose `
    --option logging `
    --action enable `
    --contract-id $contractId `
    --governance-client "ob-consumer-client"

az cleanroom governance contract runtime-option propose `
    --option telemetry `
    --action enable `
    --contract-id $contractId `
    --governance-client "ob-consumer-client"

$clientName = "ob-publisher-client"
pwsh $collabSamplePath/verify-deployment-proposals.ps1 `
    -cleanroomConfig $publisherConfig `
    -governanceClient $clientName

# Vote on the proposed deployment template.
$proposalId = az cleanroom governance deployment template show `
    --contract-id $contractId `
    --governance-client $clientName `
    --query "proposalIds[0]" `
    --output tsv

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $clientName

# Vote on the proposed cce policy.
$proposalId = az cleanroom governance deployment policy show `
    --contract-id $contractId `
    --governance-client $clientName `
    --query "proposalIds[0]" `
    --output tsv

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $clientName

# Vote on the enable logging proposal.
$proposalId = az cleanroom governance contract runtime-option get `
    --option logging `
    --contract-id $contractId `
    --governance-client $clientName `
    --query "proposalIds[0]" `
    --output tsv

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $clientName

# Vote on the enable telemetry proposal.
$proposalId = az cleanroom governance contract runtime-option get `
    --option telemetry `
    --contract-id $contractId `
    --governance-client $clientName `
    --query "proposalIds[0]" `
    --output tsv

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $clientName

$clientName = "ob-consumer-client"
pwsh $collabSamplePath/verify-deployment-proposals.ps1 `
    -cleanroomConfig $consumerConfig `
    -governanceClient $clientName

# Vote on the proposed deployment template.
$proposalId = az cleanroom governance deployment template show `
    --contract-id $contractId `
    --governance-client $clientName `
    --query "proposalIds[0]" `
    --output tsv

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $clientName

# Vote on the proposed cce policy.
$proposalId = az cleanroom governance deployment policy show `
    --contract-id $contractId `
    --governance-client $clientName `
    --query "proposalIds[0]" `
    --output tsv

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $clientName

# Vote on the enable logging proposal.
$proposalId = az cleanroom governance contract runtime-option get `
    --option logging `
    --contract-id $contractId `
    --governance-client $clientName `
    --query "proposalIds[0]" `
    --output tsv

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $clientName

# Vote on the enable telemetry proposal.
$proposalId = az cleanroom governance contract runtime-option get `
    --option telemetry `
    --contract-id $contractId `
    --governance-client $clientName `
    --query "proposalIds[0]" `
    --output tsv

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $clientName

# Creates a KEK with SKR policy, wraps DEKs with the KEK and put in kv.
az cleanroom config wrap-deks `
    --contract-id $contractId `
    --cleanroom-config $publisherConfig `
    --datastore-config $publisherDatastoreConfig `
    --key-store $publisherKeyStore `
    --governance-client "ob-publisher-client"

# Setup OIDC issuer and managed identity access to storage/KV in publisher tenant.
pwsh $collabSamplePath/setup-access.ps1 `
    -resourceGroup $publisherResourceGroup `
    -contractId $contractId  `
    -kvType akvpremium `
    -outDir $outDir `
    -governanceClient "ob-publisher-client"

# Creates a KEK with SKR policy, wraps DEKs with the KEK and put in kv.
az cleanroom config wrap-deks `
    --contract-id $contractId `
    --cleanroom-config $consumerConfig `
    --datastore-config $consumerDatastoreConfig `
    --key-store $consumerKeyStore `
    --governance-client "ob-consumer-client"

# Setup OIDC issuer endpoint and managed identity access to storage/KV in consumer tenant.
pwsh $collabSamplePath/setup-access.ps1 `
    -resourceGroup $consumerResourceGroup `
    -contractId $contractId `
    -kvType akvpremium `
    -outDir $outDir `
    -governanceClient "ob-consumer-client"

# defining query
$data = "SELECT author, COUNT(*) AS Number_Of_Mentions FROM COMBINED_TWEETS WHERE mentions LIKE '%MikeDoesBigData%'  GROUP BY author ORDER BY Number_Of_Mentions DESC"
$documentId = "12"
az cleanroom governance document create `
    --data $data `
    --id $documentId `
    --contract-id $contractId `
    --governance-client "ob-consumer-client"

$version = az cleanroom governance document show `
    --id $documentId `
    --governance-client "ob-consumer-client" `
| jq -r ".version"

# Submitting a document proposal.
$proposalId = az cleanroom governance document propose `
    --version $version `
    --id $documentId `
    --governance-client "ob-consumer-client" `
| jq -r '.proposalId'

# Vote on the query
#Consumer
az cleanroom governance document vote `
    --id $documentId `
    --proposal-id $proposalId `
    --action accept `
    --governance-client "ob-consumer-client" `
| jq

#publisher
az cleanroom governance document vote `
    --id $documentId `
    --proposal-id $proposalId `
    --action accept `
    --governance-client "ob-publisher-client" `
| jq