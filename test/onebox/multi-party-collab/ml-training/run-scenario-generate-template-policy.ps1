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

    [string]
    [ValidateSet('mhsm', 'akvpremium')]
    $kvType,

    [switch]
    $withSecurityPolicy
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# This script assumes a CCF instance was deployed in docker with the initial member that acts as the
# isv for the covid-data ml-training collab sample.
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
$tdpConfig = "$outDir/configurations/tdp-config"
$tdcConfig = "$outDir/configurations/tdc-config"
mkdir -p "$datastoreOutdir"
$publisherDatastoreConfig = "$datastoreOutdir/ml-training-publisher-datastore-config"
$consumerDatastoreConfig = "$datastoreOutdir/ml-training-consumer-datastore-config"

mkdir -p "$datastoreOutdir/secrets"
$publisherSecretStoreConfig = "$datastoreOutdir/secrets/ml-training-publisher-secretstore-config"
$consumerSecretStoreConfig = "$datastoreOutdir/secrets/ml-training-consumer-secretstore-config"

$publisherLocalSecretStore = "$datastoreOutdir/secrets/ml-training-publisher-secretstore-local"
$consumerLocalSecretStore = "$datastoreOutdir/secrets/ml-training-consumer-secretstore-local"

$resourceGroupTags = ""
if ($env:GITHUB_ACTIONS -eq "true") {
    $tdpResourceGroup = "cl-ob-tdp-$kvType-${env:JOB_ID}-${env:RUN_ID}"
    $tdcResourceGroup = "cl-ob-tdc-$kvType-${env:JOB_ID}-${env:RUN_ID}"
    $resourceGroupTags = "github_actions=multi-party-collab-$kvType-${env:JOB_ID}-${env:RUN_ID}"
}
else {
    $user = $env:CODESPACES -eq "true" ? $env:GITHUB_USER : $env:USER
    $tdpResourceGroup = "cl-ob-tdp-${user}"
    $tdcResourceGroup = "cl-ob-tdc-${user}"
}

# "isv" member makes a proposal for adding the new member "tdc" and "tdp".

# Invite tdp to the consortium.
if (-not (Test-Path -Path "$ccfOutDir/tdp_cert.pem")) {
    az cleanroom governance member keygenerator-sh | bash -s -- --name "tdp" --gen-enc-key --out "$ccfOutDir"
}
$tdpTenantId = az account show --query "tenantId" --output tsv
$proposalId = (az cleanroom governance member add `
        --certificate $ccfOutDir/tdp_cert.pem `
        --encryption-public-key $ccfOutDir/tdp_enc_pubk.pem `
        --identifier "tdp" `
        --tenant-id $tdpTenantId `
        --query "proposalId" `
        --output tsv `
        --governance-client "ob-isv-client")

# Vote on the above proposal to accept the membership.
az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client "ob-isv-client"

# Invite tdc to the consortium.
if (-not (Test-Path -Path "$ccfOutDir/tdc_cert.pem")) {
    az cleanroom governance member keygenerator-sh | bash -s -- --name "tdc" --gen-enc-key --out "$ccfOutDir"
}
$tdcTenantId = az account show --query "tenantId" --output tsv
$proposalId = (az cleanroom governance member add `
        --certificate $ccfOutDir/tdc_cert.pem `
        --encryption-public-key $ccfOutDir/tdc_enc_pubk.pem `
        --identifier "tdc" `
        --tenant-id $tdcTenantId `
        --query "proposalId" `
        --output tsv `
        --governance-client "ob-isv-client")

# Vote on the above proposal to accept the membership.
az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client "ob-isv-client"

# tdp/tdc deploys client-side containers to interact with the governance service as the new member.
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
    --signing-cert $ccfOutDir/tdp_cert.pem `
    --signing-key $ccfOutDir/tdp_privk.pem `
    --service-cert $ccfOutDir/service_cert.pem `
    --name "ob-tdp-client"

az cleanroom governance client deploy `
    --ccf-endpoint $ccfEndpoint `
    --signing-cert $ccfOutDir/tdc_cert.pem `
    --signing-key $ccfOutDir/tdc_privk.pem `
    --service-cert $ccfOutDir/service_cert.pem `
    --name "ob-tdc-client"

# tdp/tdc accepts the invitation and becomes an active member in the consortium.
az cleanroom governance member activate --governance-client "ob-tdp-client"
az cleanroom governance member activate --governance-client "ob-tdc-client"

# Update the recovery threshold of the network to include all the active members.
$newThreshold = 3
$proposalId = (az cleanroom governance network set-recovery-threshold `
        --recovery-threshold $newThreshold `
        --query "proposalId" `
        --output tsv `
        --governance-client "ob-tdp-client")

# Vote on the above proposal to accept the new threshold.
az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client "ob-tdp-client"

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client "ob-tdc-client"

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client "ob-isv-client"

$recoveryThreshold = (az cleanroom governance network show `
        --query "configuration.recoveryThreshold" `
        --output tsv `
        --governance-client "ob-tdp-client")
if ($recoveryThreshold -ne $newThreshold) {
    throw "Expecting recovery threshold to be $newThreshold but value is $recoveryThreshold."
}

# Create storage account, KV and MI resources.
if ($env:GITHUB_ACTIONS -eq "true") {
    mkdir -p "$outDir/$tdpResourceGroup"
    $overrides = @"
`$HSM_RESOURCE_GROUP = "$env:TDP_HSM_RESOURCE_GROUP"
`$MHSM_NAME="$env:TDP_MHSM_NAME"
"@ > $outDir/$tdpResourceGroup/overrides
}

pwsh $PSScriptRoot/../prepare-resources.ps1 `
    -resourceGroup $tdpResourceGroup `
    -resourceGroupTags $resourceGroupTags `
    -kvType $kvType `
    -overridesFilePath $outDir/$tdpResourceGroup/overrides `
    -outDir $outDir
$result = Get-Content "$outDir/$tdpResourceGroup/resources.generated.json" | ConvertFrom-Json

az cleanroom secretstore add `
    --name publisher-local-store `
    --config $publisherSecretStoreConfig `
    --backingstore-type Local_File `
    --backingstore-path $publisherLocalSecretStore

# Create a datasource entry.
az cleanroom datastore add `
    --name cowin `
    --config $publisherDatastoreConfig `
    --secretstore publisher-local-store `
    --secretstore-config $publisherSecretStoreConfig `
    --encryption-mode CPK `
    --backingstore-type Azure_BlobStorage `
    --backingstore-id $result.sa.id

pwsh $root/test/onebox/multi-party-collab/wait-for-container-access.ps1 `
    --containerName cowin `
    --storageAccountId $result.sa.id
# Encrypt and upload content.
az cleanroom datastore upload `
    --name cowin `
    --config $publisherDatastoreConfig `
    --src $PSScriptRoot/publisher/cowin

# Create a datasource entry.
az cleanroom datastore add `
    --name icmr `
    --config $publisherDatastoreConfig `
    --secretstore publisher-local-store `
    --secretstore-config $publisherSecretStoreConfig `
    --encryption-mode CPK `
    --backingstore-type Azure_BlobStorage `
    --backingstore-id $result.sa.id

pwsh $root/test/onebox/multi-party-collab/wait-for-container-access.ps1 `
    --containerName icmr `
    --storageAccountId $result.sa.id

# Encrypt and upload content.
az cleanroom datastore upload `
    --name icmr `
    --config $publisherDatastoreConfig `
    --src $PSScriptRoot/publisher/icmr

# Create a datasource entry.
az cleanroom datastore add `
    --name index `
    --config $publisherDatastoreConfig `
    --secretstore publisher-local-store `
    --secretstore-config $publisherSecretStoreConfig `
    --encryption-mode CPK `
    --backingstore-type Azure_BlobStorage `
    --backingstore-id $result.sa.id

pwsh $root/test/onebox/multi-party-collab/wait-for-container-access.ps1 `
    --containerName index `
    --storageAccountId $result.sa.id

# Encrypt and upload content.
az cleanroom datastore upload `
    --name index `
    --config $publisherDatastoreConfig `
    --src $PSScriptRoot/publisher/index

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

az cleanroom config init --cleanroom-config $tdpConfig

$identity = $(az resource show --ids $result.mi.id --query "properties") | ConvertFrom-Json

# Create identity entry in the configuration.
az cleanroom config add-identity az-federated `
    --cleanroom-config $tdpConfig `
    -n publisher-identity `
    --client-id $identity.clientId `
    --tenant-id $identity.tenantId `
    --backing-identity cleanroom_cgs_oidc

$kekName = $($($(New-Guid).Guid) -replace '-').ToLower()
Write-Host "Using KEK name {$kekName} for publisher"

# Create a datasource entry in the configuration.
az cleanroom config add-datasource `
    --cleanroom-config $tdpConfig `
    --datastore-name cowin `
    --datastore-config $publisherDatastoreConfig `
    --secretstore-config $publisherSecretStoreConfig `
    --dek-secret-store publisher-dek-store `
    --kek-secret-store publisher-kek-store `
    --identity publisher-identity `
    --kek-name $kekName

az cleanroom config add-datasource `
    --cleanroom-config $tdpConfig `
    --datastore-name icmr `
    --datastore-config $publisherDatastoreConfig `
    --secretstore-config $publisherSecretStoreConfig `
    --dek-secret-store publisher-dek-store `
    --kek-secret-store publisher-kek-store `
    --identity publisher-identity `
    --kek-name $kekName

az cleanroom config add-datasource `
    --cleanroom-config $tdpConfig `
    --datastore-name index `
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
    --cleanroom-config $tdpConfig `
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
    --cleanroom-config $tdpConfig `
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

if ($env:GITHUB_ACTIONS -eq "true") {
    mkdir -p "$outDir/$tdcResourceGroup"
    $overrides = @"
`$HSM_RESOURCE_GROUP = "$env:TDC_HSM_RESOURCE_GROUP"
`$MHSM_NAME="$env:TDC_MHSM_NAME"
"@ > $outDir/$tdcResourceGroup/overrides
}

# Create storage account, KV and MI resources.
pwsh $PSScriptRoot/../prepare-resources.ps1 `
    -resourceGroup $tdcResourceGroup `
    -resourceGroupTags $resourceGroupTags `
    -kvType $kvType `
    -overridesFilePath $outDir/$tdcResourceGroup/overrides `
    -outDir $outDir
$result = Get-Content "$outDir/$tdcResourceGroup/resources.generated.json" | ConvertFrom-Json

az cleanroom secretstore add `
    --name consumer-local-store `
    --config $consumerSecretStoreConfig `
    --backingstore-type Local_File `
    --backingstore-path $consumerLocalSecretStore

az cleanroom datastore add `
    --name model `
    --config $consumerDatastoreConfig `
    --secretstore consumer-local-store `
    --secretstore-config $consumerSecretStoreConfig `
    --encryption-mode CPK `
    --backingstore-type Azure_BlobStorage `
    --backingstore-id $result.sa.id

pwsh $root/test/onebox/multi-party-collab/wait-for-container-access.ps1 `
    --containerName model `
    --storageAccountId $result.sa.id
    
# Encrypt and upload content.
az cleanroom datastore upload `
    --name model `
    --config $consumerDatastoreConfig `
    --src $PSScriptRoot/consumer/model

az cleanroom datastore add `
    --name config `
    --config $consumerDatastoreConfig `
    --secretstore consumer-local-store `
    --secretstore-config $consumerSecretStoreConfig `
    --encryption-mode CPK `
    --backingstore-type Azure_BlobStorage `
    --backingstore-id $result.sa.id

pwsh $root/test/onebox/multi-party-collab/wait-for-container-access.ps1 `
    --containerName config `
    --storageAccountId $result.sa.id
    
# Encrypt and upload content.
az cleanroom datastore upload `
    --name config `
    --config $consumerDatastoreConfig `
    --src $PSScriptRoot/consumer/config

$containerName = $($($(New-Guid).Guid) -replace '-').ToLower()
Write-Host "Using container name {$containerName} for datastore {output}"
az cleanroom datastore add `
    --name output `
    --config $consumerDatastoreConfig `
    --secretstore consumer-local-store `
    --secretstore-config $consumerSecretStoreConfig `
    --encryption-mode CPK `
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

az cleanroom config init --cleanroom-config $tdcConfig

$identity = $(az resource show --ids $result.mi.id --query "properties") | ConvertFrom-Json

# Create identity entry in the configuration.
az cleanroom config add-identity az-federated `
    --cleanroom-config $tdcConfig `
    -n consumer-identity `
    --client-id $identity.clientId `
    --tenant-id $identity.tenantId `
    --backing-identity cleanroom_cgs_oidc

$kekName = $($($(New-Guid).Guid) -replace '-').ToLower()
Write-Host "Using KEK name {$kekName} for consumer"

az cleanroom config add-datasource `
    --cleanroom-config $tdcConfig `
    --datastore-name model `
    --datastore-config $consumerDatastoreConfig `
    --secretstore-config $consumerSecretStoreConfig `
    --dek-secret-store consumer-dek-store `
    --kek-secret-store consumer-kek-store `
    --identity consumer-identity `
    --kek-name $kekName

az cleanroom config add-datasource `
    --cleanroom-config $tdcConfig `
    --datastore-name config `
    --datastore-config $consumerDatastoreConfig `
    --secretstore-config $consumerSecretStoreConfig `
    --dek-secret-store consumer-dek-store `
    --kek-secret-store consumer-kek-store `
    --identity consumer-identity `
    --kek-name $kekName

# Create a datasink entry in the configuration.
az cleanroom config add-datasink `
    --cleanroom-config $tdcConfig `
    --datastore-name output `
    --datastore-config $consumerDatastoreConfig `
    --secretstore-config $consumerSecretStoreConfig `
    --dek-secret-store consumer-dek-store `
    --kek-secret-store consumer-kek-store `
    --identity consumer-identity `
    --kek-name $kekName

az cleanroom config add-application `
    --cleanroom-config $tdcConfig `
    --name depa-training `
    --image "cleanroomsamples.azurecr.io/depa-training@sha256:3a9b8d8d165bbc1867e23bba7b87d852025d96bd3cb2bb167a6cfc965134ba79" `
    --command "/bin/bash run.sh" `
    --datasources "config=/mnt/remote/config" `
    "cowin=/mnt/remote/cowin" `
    "icmr=/mnt/remote/icmr" `
    "index=/mnt/remote/index" `
    "model=/mnt/remote/model" `
    --datasinks "output=/mnt/remote/output" `
    --env-vars model_config=/mnt/remote/config/model_config.json `
    query_config=/mnt/remote/config/query_config.json `
    --cpu 0.5 `
    --memory 4 `
    --auto-start

# Generate the cleanroom config which contains all the datasources, sinks and applications that are
# configured by both the tdp and tdc.
az cleanroom config view `
    --cleanroom-config $tdcConfig `
    --configs $tdpConfig `
    --output-file $outDir/configurations/cleanroom-config

az cleanroom config validate --cleanroom-config $outDir/configurations/cleanroom-config

# Submitting a contract proposal as isv.
$data = Get-Content -Raw $outDir/configurations/cleanroom-config
az cleanroom governance contract create `
    --data "$data" `
    --id $contractId `
    --governance-client "ob-isv-client"

$version = (az cleanroom governance contract show `
        --id $contractId `
        --query "version" `
        --output tsv `
        --governance-client "ob-isv-client")

az cleanroom governance contract propose `
    --version $version `
    --id $contractId `
    --governance-client "ob-isv-client"

$contract = (az cleanroom governance contract show `
        --id $contractId `
        --governance-client "ob-isv-client" | ConvertFrom-Json)

# Accept it as isv.
az cleanroom governance contract vote `
    --id $contractId `
    --proposal-id $contract.proposalId `
    --action accept `
    --governance-client "ob-isv-client"

$contract = (az cleanroom governance contract show `
        --id $contractId `
        --governance-client "ob-tdc-client" | ConvertFrom-Json)

# Accept it tdc.
az cleanroom governance contract vote `
    --id $contractId `
    --proposal-id $contract.proposalId `
    --action accept `
    --governance-client "ob-tdc-client"

# Accept it tdp.
az cleanroom governance contract vote `
    --id $contractId `
    --proposal-id $contract.proposalId `
    --action accept `
    --governance-client "ob-tdp-client"

mkdir -p $outDir/deployments
# Set overrides if a non-mcr registry is to be used for clean room container images.
if ($registry -ne "mcr") {
    $env:AZCLI_CLEANROOM_CONTAINER_REGISTRY_URL = $repo
    $env:AZCLI_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL = "${repo}/sidecar-digests:$tag"
}
if ($withSecurityPolicy) {
    az cleanroom governance deployment generate `
        --contract-id $contractId `
        --governance-client "ob-isv-client" `
        --output-dir $outDir/deployments `
        --security-policy-creation-option cached-debug
}
else {
    az cleanroom governance deployment generate `
        --contract-id $contractId `
        --governance-client "ob-isv-client" `
        --output-dir $outDir/deployments `
        --security-policy-creation-option allow-all
}

if ($env:COLLAB_FORCE_MANAGED_IDENTITY -eq "true") {
    Import-Module $root/test/onebox/multi-party-collab/force-managed-identity.ps1 -Force -DisableNameChecking
    $publisherMi = (Get-Content "$outDir/$tdpResourceGroup/resources.generated.json" | ConvertFrom-Json).mi.id
    $consumerMi = (Get-Content "$outDir/$tdcResourceGroup/resources.generated.json" | ConvertFrom-Json).mi.id
    Force-Managed-Identity `
        -deploymentTemplateFile "$outDir/deployments/cleanroom-arm-template.json" `
        -managedIdentities @($publisherMi, $consumerMi)
}

az cleanroom governance deployment template propose `
    --template-file $outDir/deployments/cleanroom-arm-template.json `
    --contract-id $contractId `
    --governance-client "ob-isv-client"

az cleanroom governance deployment policy propose `
    --policy-file $outDir/deployments/cleanroom-governance-policy.json `
    --contract-id $contractId `
    --governance-client "ob-isv-client"

# Propose enabling log and telemetry collection during cleanroom execution.
az cleanroom governance contract runtime-option propose `
    --option logging `
    --action enable `
    --contract-id $contractId `
    --governance-client "ob-isv-client"

az cleanroom governance contract runtime-option propose `
    --option telemetry `
    --action enable `
    --contract-id $contractId `
    --governance-client "ob-isv-client"

Write-Output "Enabling CA..."
az cleanroom governance ca propose-enable `
    --contract-id $contractId `
    --governance-client "ob-isv-client"

$clientName = "ob-tdp-client"
pwsh $PSScriptRoot/../verify-deployment-proposals.ps1 `
    -cleanroomConfig $tdpConfig `
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

$clientName = "ob-tdc-client"
pwsh $PSScriptRoot/../verify-deployment-proposals.ps1 `
    -cleanroomConfig $tdcConfig `
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

$clientName = "ob-isv-client"
pwsh $PSScriptRoot/../verify-deployment-proposals.ps1 `
    -cleanroomConfig $tdcConfig `
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
    --cleanroom-config $tdpConfig `
    --datastore-config $publisherDatastoreConfig `
    --secretstore-config $publisherSecretStoreConfig `
    --governance-client "ob-tdp-client"

# Setup OIDC issuer and managed identity access to storage/KV in tdp tenant.
pwsh $PSScriptRoot/../setup-oidc-issuer.ps1 `
    -resourceGroup $tdpResourceGroup `
    -outDir $outDir `
    -governanceClient "ob-tdp-client"
$issuerUrl = Get-Content $outDir/$tdpResourceGroup/issuer-url.txt

pwsh $PSScriptRoot/../setup-access.ps1 `
    -resourceGroup $tdpResourceGroup `
    -subject $contractId `
    -issuerUrl $issuerUrl `
    -outDir $outDir `
    -kvType $kvType `
    -governanceClient "ob-tdp-client"

# Creates a KEK with SKR policy, wraps DEKs with the KEK and put in kv.
az cleanroom config wrap-deks `
    --contract-id $contractId `
    --cleanroom-config $tdcConfig `
    --datastore-config $consumerDatastoreConfig `
    --secretstore-config $consumerSecretStoreConfig `
    --governance-client "ob-tdc-client"

# Setup OIDC issuer endpoint and managed identity access to storage/KV in tdc tenant.
pwsh $PSScriptRoot/../setup-oidc-issuer.ps1 `
    -resourceGroup $tdcResourceGroup `
    -outDir $outDir `
    -governanceClient "ob-tdc-client"
$issuerUrl = Get-Content $outDir/$tdcResourceGroup/issuer-url.txt

pwsh $PSScriptRoot/../setup-access.ps1 `
    -resourceGroup $tdcResourceGroup `
    -subject $contractId `
    -issuerUrl $issuerUrl `
    -outDir $outDir `
    -kvType $kvType `
    -governanceClient "ob-tdc-client"