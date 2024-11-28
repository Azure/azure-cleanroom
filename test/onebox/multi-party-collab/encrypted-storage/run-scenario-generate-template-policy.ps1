[CmdletBinding()]
param
(
    [string]
    $outDir = "$PSScriptRoot/generated",

    [string]
    $ccfEndpoint = "https://host.docker.internal:9081",

    [string]
    $ccfOutDir = "",

    [string]
    $datastoreOutdir = "",

    [string]
    $contractId = "collab1",

    [switch]
    $y,

    [ValidateSet('mcr', 'local', 'acr')]
    [string]$registry = "local",

    [string]$registryUrl = "localhost:5001",

    [string]$registryTag = "latest",

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
    $ccfOutDir = "$root/test/onebox/multi-party-collab/generated/ccf"
}

if ($datastoreOutdir -eq "") {
    $datastoreOutdir = "$root/test/onebox/multi-party-collab/generated/datastores"
}

$serviceCert = $ccfOutDir + "/service_cert.pem"
if (-not (Test-Path -Path $serviceCert)) {
    throw "serviceCert at $serviceCert does not exist."
}

if ($env:GITHUB_ACTIONS -eq "true" -and $ccfEndpoint -eq "https://host.docker.internal:9081") {
    # 172.17.0.1: https://stackoverflow.com/questions/48546124/what-is-the-linux-equivalent-of-host-docker-internal
    $ccfEndpoint = "https://172.17.0.1:9081"
}

mkdir -p "$outDir/configurations"
$publisherConfig = "$outDir/configurations/publisher-config"
$consumerConfig = "$outDir/configurations/consumer-config"
mkdir -p "$datastoreOutdir"
$publisherDatastoreConfig = "$datastoreOutdir/encrypted-storage-publisher-datastore-config"
$consumerDatastoreConfig = "$datastoreOutdir/encrypted-storage-consumer-datastore-config"

mkdir -p "$datastoreOutdir/secrets"
$publisherSecretStoreConfig = "$datastoreOutdir/secrets/encrypted-storage-publisher-secretstore-config"
$consumerSecretStoreConfig = "$datastoreOutdir/secrets/encrypted-storage-consumer-secretstore-config"

$publisherLocalSecretStore = "$datastoreOutdir/secrets/encrypted-storage-publisher-secretstore-local"
$consumerLocalSecretStore = "$datastoreOutdir/secrets/encrypted-storage-consumer-secretstore-local"

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

# if (!$y) {
#     Read-Host "Press Enter to continue or Ctrl+C to quit" | Out-Null
# }

# Publisher identity creation.
if (-not (Test-Path -Path "$ccfOutDir/publisher_cert.pem")) {
    az cleanroom governance member keygenerator-sh | bash -s -- --name "publisher" --out "$ccfOutDir"
}

# Invite publisher to the consortium.
$publisherTenantId = az account show --query "tenantId" --output tsv

# "consumer" member makes a proposal for adding the new member "publisher".
$proposalId = (az cleanroom governance member add `
        --certificate $ccfOutDir/publisher_cert.pem `
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

# if (!$y) {
#     Read-Host "Press Enter to continue or Ctrl+C to quit" | Out-Null
# }

# "publisher" deploys client-side containers to interact with the governance service as the new member.
# Set overrides if local registry is to be used for CGS images.
if ($registry -eq "local") {
    $tag = cat "$ccfOutDir/local-registry-tag.txt"
    $env:AZCLI_CGS_CLIENT_IMAGE = "localhost:5000/cgs-client:$tag"
    $env:AZCLI_CGS_UI_IMAGE = "localhost:5000/cgs-ui:$tag"
}

az cleanroom governance client deploy `
    --ccf-endpoint $ccfEndpoint `
    --signing-cert $ccfOutDir/publisher_cert.pem `
    --signing-key $ccfOutDir/publisher_privk.pem `
    --service-cert $ccfOutDir/service_cert.pem `
    --name "ob-publisher-client"

# if (!$y) {
#     Read-Host "Press Enter to continue or Ctrl+C to quit" | Out-Null
# }

# "publisher" accepts the invitation and becomes an active member in the consortium.
az cleanroom governance member activate --governance-client "ob-publisher-client"

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
    --encryption-mode CSE `
    --backingstore-type Azure_BlobStorage `
    --backingstore-id $result.sa.id

# Encrypt and upload content.
az cleanroom datastore encrypt `
    --config $publisherDatastoreConfig `
    --name publisher-input `
    --source-path $PSScriptRoot/publisher/input `
    --destination-path $PSScriptRoot/publisher/input-encrypted

az cleanroom datastore upload `
    --name publisher-input `
    --config $publisherDatastoreConfig `
    --src $PSScriptRoot/publisher/input-encrypted

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
# if (!$y) {
#     Read-Host "prepare-resources done. Press Enter to continue or Ctrl+C to quit" | Out-Null
# }

$identity = $(az resource show --ids $result.mi.id --query "properties") | ConvertFrom-Json

# Create identity entry in the configuration.
az cleanroom config add-identity az-federated `
    --cleanroom-config $publisherConfig `
    -n publisher-identity `
    --client-id $identity.clientId `
    --tenant-id $identity.tenantId `
    --backing-identity cleanroom_cgs_oidc

# if (!$y) {
#     Read-Host "Press Enter to continue or Ctrl+C to quit" | Out-Null
# }

# Create a datasource entry in the configuration.
az cleanroom config add-datasource `
    --cleanroom-config $publisherConfig `
    --datastore-name publisher-input `
    --datastore-config $publisherDatastoreConfig `
    --secretstore-config $publisherSecretStoreConfig `
    --dek-secret-store publisher-dek-store `
    --kek-secret-store publisher-kek-store `
    --identity publisher-identity 

$containerSuffix = $($($(New-Guid).Guid) -replace '-').ToLower()
Write-Host "Using container suffix {$containerSuffix} for application-telemetry"

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
    --encryption-mode CSE `
    --container-suffix $containerSuffix

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
    --encryption-mode CSE `
    --container-suffix $containerSuffix

# if (!$y) {
#     Read-Host "Press Enter to continue or Ctrl+C to quit" | Out-Null
# }

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

$containerName = $($($(New-Guid).Guid) -replace '-').ToLower()
Write-Host "Using container name {$containerName} for datastore {consumer-output}"

# Create a datasource entry.
az cleanroom datastore add `
    --name consumer-output `
    --config $consumerDatastoreConfig `
    --secretstore consumer-local-store `
    --secretstore-config $consumerSecretStoreConfig `
    --encryption-mode CSE `
    --backingstore-type Azure_BlobStorage `
    --backingstore-id $result.sa.id `
    --container-name $containerName

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

# Create a datasink entry in the configuration.
az cleanroom config add-datasink `
    --cleanroom-config $consumerConfig `
    --datastore-name consumer-output `
    --datastore-config $consumerDatastoreConfig `
    --secretstore-config $consumerSecretStoreConfig `
    --dek-secret-store consumer-dek-store `
    --kek-secret-store consumer-kek-store `
    --identity consumer-identity

$sample_code = $(cat $PSScriptRoot/consumer/application/main.go | base64 -w 0)
az cleanroom config add-application `
    --cleanroom-config $consumerConfig `
    --name demo-app `
    --image "cleanroomsamplesprivate.azurecr.io/golang@sha256:8c64602c2eb46348eee4411edd37eede291f77d0186703e8cbda4dba2af12a51" `
    --command "bash -c 'echo `$CODE | base64 -d > main.go; go run main.go'" `
    --mounts "src=publisher-input,dst=/mnt/remote/input" `
    "src=consumer-output,dst=/mnt/remote/output" `
    --env-vars OUTPUT_LOCATION=/mnt/remote/output `
    INPUT_LOCATION=/mnt/remote/input `
    CODE="$sample_code" `
    --cpu 0.5 `
    --memory 4 `
    --acr-access-identity consumer-identity

# Since we're using the private acr, we need to give access to the identity on it
Write-Host "Assigning AcrPull role to the managed identity on the private ACR"
$resourceID = $(az acr show `
        --resource-group cleanroomdev-rg `
        --name cleanroomsamplesprivate.azurecr.io `
        --query id `
        --output tsv)
az role assignment create `
    --assignee-object-id $result.mi.principalId `
    --assignee-principal-type ServicePrincipal `
    --scope $resourceID `
    --role acrpull

# Generate the cleanroom config which contains all the datasources, sinks and applications that are
# configured by both the producer and consumer.
az cleanroom config view `
    --cleanroom-config $consumerConfig `
    --configs $publisherConfig `
    --output-file $outDir/configurations/cleanroom-config

# if (!$y) {
#     Read-Host "Press Enter to continue or Ctrl+C to quit" | Out-Null
# }

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

# if (!$y) {
#     Read-Host "Press Enter to continue or Ctrl+C to quit" | Out-Null
# }

mkdir -p $outDir/deployments
# Set overrides if a non-mcr registry is to be used for clean room container images.
if ($registry -ne "mcr") {
    $env:AZCLI_CLEANROOM_CONTAINER_REGISTRY_URL = $registryUrl
    $env:AZCLI_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL = "${registryUrl}/sidecar-digests:$registryTag"
}

if ($withSecurityPolicy) {
    az cleanroom governance deployment generate `
        --contract-id $contractId `
        --governance-client "ob-consumer-client" `
        --output-dir $outDir/deployments `
        --security-policy-creation-option cached-debug
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

# if (!$y) {
#     Read-Host "Press Enter to continue or Ctrl+C to quit" | Out-Null
# }

# Creates a KEK with SKR policy, wraps DEKs with the KEK and put in kv.
az cleanroom config wrap-deks `
    --contract-id $contractId `
    --cleanroom-config $publisherConfig `
    --datastore-config $publisherDatastoreConfig `
    --secretstore-config $publisherSecretStoreConfig `
    --governance-client "ob-publisher-client"

$usePreprovisionedOIDC = $false
if ($env:USE_PREPROVISIONED_OIDC -eq "true") {
    $usePreprovisionedOIDC = $true
}

Write-Host "usePreprovisionedOIDC:$usePreprovisionedOIDC"
# Setup OIDC issuer and managed identity access to storage/KV in publisher tenant.
pwsh $PSScriptRoot/../setup-access.ps1 `
    -resourceGroup $publisherResourceGroup `
    -contractId $contractId  `
    -outDir $outDir `
    -kvType akvpremium `
    -governanceClient "ob-publisher-client" `
    -usePreprovisionedOIDC:$usePreprovisionedOIDC

# Creates a KEK with SKR policy, wraps DEKs with the KEK and put in kv.
az cleanroom config wrap-deks `
    --contract-id $contractId `
    --cleanroom-config $consumerConfig `
    --datastore-config $consumerDatastoreConfig `
    --secretstore-config $consumerSecretStoreConfig `
    --governance-client "ob-consumer-client"

# Setup OIDC issuer endpoint and managed identity access to storage/KV in consumer tenant.
pwsh $PSScriptRoot/../setup-access.ps1 `
    -resourceGroup $consumerResourceGroup `
    -contractId $contractId `
    -outDir $outDir `
    -kvType akvpremium `
    -governanceClient "ob-consumer-client" `
    -usePreprovisionedOIDC:$usePreprovisionedOIDC
