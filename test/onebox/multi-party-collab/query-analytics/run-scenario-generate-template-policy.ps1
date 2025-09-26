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
    $datastoreOutdir = "",

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

if ($ccfOutDir -eq "") {
    $ccfOutDir = "$outDir/ccf"
}

if ($datastoreOutdir -eq "") {
    $datastoreOutdir = "$outDir/datastores"
}

$serviceCert = $ccfOutDir + "/service_cert.pem"
if (-not (Test-Path -Path $serviceCert)) {
    throw "serviceCert at $serviceCert does not exist."
}

mkdir -p "$outDir/configurations"
$consumerDataSamplePath = "$root/samples/multi-party-collab/scenarios/analytics/consumer-demo/consumer-input"
$publisherDataSamplePath = "$root/samples/multi-party-collab/scenarios/analytics/publisher-demo/publisher-input"
mkdir -p $consumerDataSamplePath
mkdir -p $publisherDataSamplePath

# Generate data
$src = "https://github.com/Azure-Samples/Synapse/raw/refs/heads/main/Data/Tweets"
$consumerHandles = ("BrigitMurtaughTweets", "FranmerMSTweets", "JeremyLiknessTweets")
$producerHandles = ("RahulPotharajuTweets", "MikeDoesBigDataTweets", "SQLCindyTweets")

foreach ($handle in $consumerHandles) {
    curl -L "$src/$handle.csv" -o "$consumerDataSamplePath/$handle.csv"
}

foreach ($handle in $producerHandles) {
    curl -L "$src/$handle.csv" -o "$publisherDataSamplePath/$handle.csv"
}

$publisherConfig = "$outDir/configurations/publisher-config"
$consumerConfig = "$outDir/configurations/consumer-config"
$datastoreOutdir = "$outDir/datastores"
mkdir -p "$datastoreOutdir"
$publisherDatastoreConfig = "$datastoreOutdir/analytics-publisher-datastore-config"
$consumerDatastoreConfig = "$datastoreOutdir/analytics-consumer-datastore-config"

mkdir -p "$datastoreOutdir/keys"
$publisherKeyStore = "$datastoreOutdir/keys/analytics-publisher-datastore-config-publisher-keys"
$consumerKeyStore = "$datastoreOutdir/keys/analytics-publisher-datastore-config-consumer-keys"

mkdir -p "$datastoreOutdir/secrets"
$publisherSecretStoreConfig = "$datastoreOutdir/secrets/analytics-publisher-secretstore-config"
$consumerSecretStoreConfig = "$datastoreOutdir/secrets/analytics-consumer-secretstore-config"

$publisherLocalSecretStore = "$datastoreOutdir/secrets/analytics-publisher-secretstore-local"
$consumerLocalSecretStore = "$datastoreOutdir/secrets/analytics-consumer-secretstore-local"

$resourceGroupTags = ""
if ($env:GITHUB_ACTIONS -eq "true") {
    $publisherResourceGroup = "cl-ob-publisher-${env:JOB_ID}-${env:RUN_ID}"
    $consumerResourceGroup = "cl-ob-consumer-${env:JOB_ID}-${env:RUN_ID}"
    $resourceGroupTags = "github_actions=multi-party-collab-${env:JOB_ID}-${env:RUN_ID}"
}
else {
    $user = $env:CODESPACES -eq "true" ? $env:GITHUB_USER : $env:USER
    $publisherResourceGroup = "cl-ob-publisher-${user}"
    $consumerResourceGroup = "cl-ob-consumer-${user}"
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
pwsh $PSScriptRoot/../prepare-resources.ps1 `
    -resourceGroup $publisherResourceGroup `
    -resourceGroupTags $resourceGroupTags `
    -kvType akvpremium `
    -outDir $outDir

$result = Get-Content "$outDir/$publisherResourceGroup/resources.generated.json" | ConvertFrom-Json

az cleanroom secretstore add `
    --name publisher-local-store `
    --config $publisherSecretStoreConfig `
    --backingstore-type Local_File `
    --backingstore-path $publisherLocalSecretStore

# Create a datasource entry.
az cleanroom datastore add `
    --name publisher-input `
    --config $publisherDatastoreConfig `
    --secretstore publisher-local-store `
    --secretstore-config $publisherSecretStoreConfig `
    --encryption-mode CPK `
    --backingstore-type Azure_BlobStorage `
    --backingstore-id $result.sa.id

pwsh $root/test/onebox/multi-party-collab/wait-for-container-access.ps1 `
    --containerName publisher-input `
    --storageAccountId $result.sa.id

# Encrypt and upload content.
az cleanroom datastore upload `
    --name publisher-input `
    --config $publisherDatastoreConfig `
    --src $publisherDataSamplePath

# Add DEK and KEK secret stores.
az cleanroom secretstore add `
    --name publisher-dek-store `
    --config $publisherSecretStoreConfig `
    --backingstore-type Azure_KeyVault `
    --backingstore-id $result.dek.kv.id

az cleanroom secretstore add `
    --name publisher-kek-store `
    --config $publisherSecretStoreConfig `
    --backingstore-type Azure_KeyVault_Managed_HSM `
    --backingstore-id $result.kek.kv.id `
    --attestation-endpoint $result.maa_endpoint

# Build the cleanroom config for the publisher.
az cleanroom config init --cleanroom-config $publisherConfig

$identity = $(az resource show --ids $result.mi.id --query "properties") | ConvertFrom-Json

# Create identity entry in the configuration.
az cleanroom config add-identity az-federated `
    --cleanroom-config $publisherConfig `
    -n publisher-identity `
    --client-id $identity.clientId `
    --tenant-id $identity.tenantId `
    --backing-identity cleanroom_cgs_oidc

$kekName = $($($(New-Guid).Guid) -replace '-').ToLower()
Write-Host "Using KEK name {$kekName} for publisher"

# Create a datasource entry in the configuration.
az cleanroom config add-datasource `
    --cleanroom-config $publisherConfig `
    --datastore-name publisher-input `
    --datastore-config $publisherDatastoreConfig `
    --secretstore-config $publisherSecretStoreConfig `
    --dek-secret-store publisher-dek-store `
    --kek-secret-store publisher-kek-store `
    --identity publisher-identity `
    --kek-name $kekName

$containerSuffix = $($($(New-Guid).Guid) -replace '-').ToLower()
Write-Host "Using container suffix {$containerSuffix} for application-telemetry"

# $result below refers to the output of the prepare-resources.ps1 that was run earlier.
az cleanroom config set-logging `
    --cleanroom-config $publisherConfig `
    --storage-account $result.sa.id `
    --identity publisher-identity `
    --datastore-config $publisherDatastoreConfig `
    --secretstore-config $publisherSecretStoreConfig `
    --datastore-secret-store publisher-local-store `
    --dek-secret-store publisher-dek-store `
    --kek-secret-store publisher-kek-store `
    --encryption-mode CPK `
    --container-suffix $containerSuffix `
    --kek-name $kekName

$containerSuffix = $($($(New-Guid).Guid) -replace '-').ToLower()
Write-Host "Using container suffix {$containerSuffix} for infrastructure-telemetry"
az cleanroom config set-telemetry `
    --cleanroom-config $publisherConfig `
    --storage-account $result.sa.id `
    --identity publisher-identity `
    --datastore-config $publisherDatastoreConfig `
    --secretstore-config $publisherSecretStoreConfig `
    --datastore-secret-store publisher-local-store `
    --dek-secret-store publisher-dek-store `
    --kek-secret-store publisher-kek-store `
    --encryption-mode CPK `
    --container-suffix $containerSuffix `
    --kek-name $kekName

# Create storage account, KV and MI resources.
pwsh $PSScriptRoot/../prepare-resources.ps1 `
    -resourceGroup $consumerResourceGroup `
    -resourceGroupTags $resourceGroupTags `
    -kvType akvpremium `
    -outDir $outDir
$result = Get-Content "$outDir/$consumerResourceGroup/resources.generated.json" | ConvertFrom-Json

az cleanroom secretstore add `
    --name consumer-local-store `
    --config $consumerSecretStoreConfig `
    --backingstore-type Local_File `
    --backingstore-path $consumerLocalSecretStore

# Create a datasource entry.
az cleanroom datastore add `
    --name consumer-input `
    --config $consumerDatastoreConfig `
    --secretstore consumer-local-store `
    --secretstore-config $consumerSecretStoreConfig `
    --encryption-mode CPK `
    --backingstore-type Azure_BlobStorage `
    --backingstore-id $result.sa.id

pwsh $root/test/onebox/multi-party-collab/wait-for-container-access.ps1 `
    --containerName consumer-input `
    --storageAccountId $result.sa.id

az cleanroom datastore upload `
    --name consumer-input `
    --config $consumerDatastoreConfig `
    --src $consumerDataSamplePath

az cleanroom datastore add `
    --name consumer-output `
    --config $consumerDatastoreConfig `
    --secretstore consumer-local-store `
    --secretstore-config $consumerSecretStoreConfig `
    --encryption-mode CPK `
    --backingstore-type Azure_BlobStorage `
    --backingstore-id $result.sa.id

# Add DEK and KEK secret stores.
az cleanroom secretstore add `
    --name consumer-dek-store `
    --config $consumerSecretStoreConfig `
    --backingstore-type Azure_KeyVault `
    --backingstore-id $result.dek.kv.id

az cleanroom secretstore add `
    --name consumer-kek-store `
    --config $consumerSecretStoreConfig `
    --backingstore-type Azure_KeyVault_Managed_HSM `
    --backingstore-id $result.kek.kv.id `
    --attestation-endpoint $result.maa_endpoint

# Build the cleanroom config for the publisher.
az cleanroom config init --cleanroom-config $consumerConfig

$identity = $(az resource show --ids $result.mi.id --query "properties") | ConvertFrom-Json

# Create identity entry in the configuration.
az cleanroom config add-identity az-federated `
    --cleanroom-config $consumerConfig `
    -n consumer-identity `
    --client-id $identity.clientId `
    --tenant-id $identity.tenantId `
    --backing-identity cleanroom_cgs_oidc

$kekName = $($($(New-Guid).Guid) -replace '-').ToLower()
Write-Host "Using KEK name {$kekName} for consumer"

# Create a datasource entry in the configuration.
az cleanroom config add-datasource `
    --cleanroom-config $consumerConfig `
    --datastore-name consumer-input `
    --datastore-config $consumerDatastoreConfig `
    --secretstore-config $consumerSecretStoreConfig `
    --dek-secret-store consumer-dek-store `
    --kek-secret-store consumer-kek-store `
    --identity consumer-identity `
    --kek-name $kekName

# Create a datasink entry in the configuration.
az cleanroom config add-datasink `
    --cleanroom-config $consumerConfig `
    --datastore-name consumer-output `
    --datastore-config $consumerDatastoreConfig `
    --secretstore-config $consumerSecretStoreConfig `
    --dek-secret-store consumer-dek-store `
    --kek-secret-store consumer-kek-store `
    --identity consumer-identity `
    --kek-name $kekName

pwsh $PSScriptRoot/build-application.ps1 -tag $tag -repo $repo -push
az cleanroom config add-application `
    --cleanroom-config $consumerConfig `
    --name demo-app `
    --image "$repo/analytics:$tag" `
    --command "python ./analytics.py" `
    --datasources "publisher-input=/mnt/remote/publisher-input" `
    "consumer-input=/mnt/remote/consumer-input" `
    --env-vars STORAGE_PATH_1=/mnt/remote/publisher-input `
    STORAGE_PATH_2=/mnt/remote/consumer-input `
    --cpu 0.5 `
    --memory 4 `
    --port 8310 `
    --auto-start

# Note: This will allow all incoming connections to the application.
az cleanroom config network http enable `
    --cleanroom-config $consumerConfig `
    --direction inbound

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

Write-Output "Enabling CA..."
az cleanroom governance ca propose-enable `
    --contract-id $contractId `
    --governance-client "ob-consumer-client"

$clientName = "ob-publisher-client"
pwsh $PSScriptRoot/../verify-deployment-proposals.ps1 `
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

# Vote on the proposed CA enable.
$proposalId = az cleanroom governance ca show `
    --contract-id $contractId `
    --governance-client $clientName `
    --query "proposalIds[0]" `
    --output tsv

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $clientName

$clientName = "ob-consumer-client"
pwsh $PSScriptRoot/../verify-deployment-proposals.ps1 `
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

# Vote on the proposed CA enable.
$proposalId = az cleanroom governance ca show `
    --contract-id $contractId `
    --governance-client $clientName `
    --query "proposalIds[0]" `
    --output tsv

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $clientName

az cleanroom governance ca generate-key `
    --contract-id $contractId `
    --governance-client $clientName

az cleanroom governance ca show `
    --contract-id $contractId `
    --governance-client $clientName `
    --query "caCert" `
    --output tsv > $outDir/cleanroomca.crt

# Creates a KEK with SKR policy, wraps DEKs with the KEK and put in kv.
az cleanroom config wrap-deks `
    --contract-id $contractId `
    --cleanroom-config $publisherConfig `
    --datastore-config $publisherDatastoreConfig `
    --secretstore-config $publisherSecretStoreConfig `
    --governance-client "ob-publisher-client"

# Setup OIDC issuer and managed identity access to storage/KV in publisher tenant.
pwsh $PSScriptRoot/../setup-oidc-issuer.ps1 `
    -resourceGroup $publisherResourceGroup `
    -outDir $outDir `
    -governanceClient "ob-publisher-client"
$issuerUrl = Get-Content $outDir/$publisherResourceGroup/issuer-url.txt

pwsh $PSScriptRoot/../setup-access.ps1 `
    -resourceGroup $publisherResourceGroup `
    -subject $contractId `
    -issuerUrl $issuerUrl `
    -kvType akvpremium `
    -outDir $outDir `
    -governanceClient "ob-publisher-client"

# Creates a KEK with SKR policy, wraps DEKs with the KEK and put in kv.
az cleanroom config wrap-deks `
    --contract-id $contractId `
    --cleanroom-config $consumerConfig `
    --datastore-config $consumerDatastoreConfig `
    --secretstore-config $consumerSecretStoreConfig `
    --governance-client "ob-consumer-client"

# Setup OIDC issuer endpoint and managed identity access to storage/KV in consumer tenant.
pwsh $PSScriptRoot/../setup-oidc-issuer.ps1 `
    -resourceGroup $consumerResourceGroup `
    -outDir $outDir `
    -governanceClient "ob-consumer-client"
$issuerUrl = Get-Content $outDir/$consumerResourceGroup/issuer-url.txt

pwsh $PSScriptRoot/../setup-access.ps1 `
    -resourceGroup $consumerResourceGroup `
    -subject $contractId `
    -issuerUrl $issuerUrl `
    -kvType akvpremium `
    -outDir $outDir `
    -governanceClient "ob-consumer-client"

# defining query
$data = "SELECT author, COUNT(*) AS Number_Of_Mentions FROM COMBINED_TWEETS WHERE mentions LIKE '%MikeDoesBigData%'  GROUP BY author ORDER BY Number_Of_Mentions DESC"
$documentId = "12"
az cleanroom governance member-document create `
    --data $data `
    --id $documentId `
    --contract-id $contractId `
    --governance-client "ob-consumer-client"

$version = az cleanroom governance member-document show `
    --id $documentId `
    --governance-client "ob-consumer-client" `
| jq -r ".version"

# Submitting a document proposal.
$proposalId = az cleanroom governance member-document propose `
    --version $version `
    --id $documentId `
    --governance-client "ob-consumer-client" `
| jq -r '.proposalId'

# Vote on the query
#Consumer
az cleanroom governance member-document vote `
    --id $documentId `
    --proposal-id $proposalId `
    --action accept `
    --governance-client "ob-consumer-client" `
| jq

#publisher
az cleanroom governance member-document vote `
    --id $documentId `
    --proposal-id $proposalId `
    --action accept `
    --governance-client "ob-publisher-client" `
| jq