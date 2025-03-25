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
    $contractId = "collab1-db",

    [ValidateSet('mcr', 'local', 'acr')]
    [string]$registry = "local",

    [string]$repo = "localhost:5000",

    [string]$tag = "latest"
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# This script assumes a CCF instance was deployed in docker with the initial member that acts as the
# consumer for the multi-party collab sample.
$root = git rev-parse --show-toplevel
$collabSamplePath = "$root/test/onebox/multi-party-collab"

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
$publisherConfig = "$outDir/configurations/publisher-config"
$consumerConfig = "$outDir/configurations/consumer-config"
$publisherDatastoreConfig = "$datastoreOutdir/quote-of-the-day-publisher-datastore-config"
$consumerDatastoreConfig = "$datastoreOutdir/quote-of-the-day-consumer-datastore-config"

mkdir -p "$datastoreOutdir/secrets"
$publisherSecretStoreConfig = "$datastoreOutdir/secrets/quote-of-the-day-publisher-secretstore-config"
$consumerSecretStoreConfig = "$datastoreOutdir/secrets/quote-of-the-day-consumer-secretstore-config"

$publisherLocalSecretStore = "$datastoreOutdir/secrets/quote-of-the-day-publisher-secretstore-local"
$consumerLocalSecretStore = "$datastoreOutdir/secrets/quote-of-the-day-consumer-secretstore-local"

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
$pubResult = Get-Content "$outDir/$publisherResourceGroup/resources.generated.json" | ConvertFrom-Json

az cleanroom secretstore add `
    --name publisher-local-store `
    --config $publisherSecretStoreConfig `
    --backingstore-type Local_File `
    --backingstore-path $publisherLocalSecretStore

# Add DEK and KEK secret stores.
az cleanroom secretstore add `
    --name publisher-dek-store `
    --config $publisherSecretStoreConfig `
    --backingstore-type Azure_KeyVault `
    --backingstore-id $pubResult.dek.kv.id 

az cleanroom secretstore add `
    --name publisher-kek-store `
    --config $publisherSecretStoreConfig `
    --backingstore-type Azure_KeyVault_Managed_HSM `
    --backingstore-id $pubResult.kek.kv.id `
    --attestation-endpoint $pubResult.maa_endpoint

az cleanroom config init --cleanroom-config $publisherConfig

$identity = $(az resource show --ids $pubResult.mi.id --query "properties") | ConvertFrom-Json

# Create identity entry in the configuration.
az cleanroom config add-identity az-federated `
    --cleanroom-config $publisherConfig `
    -n publisher-identity `
    --client-id $identity.clientId `
    --tenant-id $identity.tenantId `
    --backing-identity cleanroom_cgs_oidc

$containerSuffix = $($($(New-Guid).Guid) -replace '-').ToLower()
Write-Host "Using container suffix {$containerSuffix} for application-telemetry"

# $pubResult below refers to the output of the prepare-resources.ps1 that was run earlier.
az cleanroom config set-logging `
    --cleanroom-config $publisherConfig `
    --storage-account $pubResult.sa.id `
    --identity publisher-identity `
    --datastore-config $publisherDatastoreConfig `
    --secretstore-config $publisherSecretStoreConfig `
    --datastore-secret-store publisher-local-store `
    --dek-secret-store publisher-dek-store `
    --kek-secret-store publisher-kek-store `
    --encryption-mode CPK `
    --container-suffix $containerSuffix

$containerSuffix = $($($(New-Guid).Guid) -replace '-').ToLower()
Write-Host "Using container suffix {$containerSuffix} for infrastructure-telemetry"

az cleanroom config set-telemetry `
    --cleanroom-config $publisherConfig `
    --storage-account $pubResult.sa.id `
    --identity publisher-identity `
    --datastore-config $publisherDatastoreConfig `
    --secretstore-config $publisherSecretStoreConfig `
    --datastore-secret-store publisher-local-store `
    --dek-secret-store publisher-dek-store `
    --kek-secret-store publisher-kek-store `
    --encryption-mode CPK `
    --container-suffix $containerSuffix

pwsh $collabSamplePath/quote-of-the-day/build-outbound-opa-policy.ps1 -tag $tag -repo $repo -push

az cleanroom config network http enable `
    --cleanroom-config $publisherConfig `
    --direction outbound `
    --policy $repo/quote-of-the-day-policy:$tag

az cleanroom config network dns enable `
    --cleanroom-config $publisherConfig `
    --port 53

az cleanroom config init --cleanroom-config $consumerConfig

pwsh $collabSamplePath/quote-of-the-day/build-application.ps1 -tag $tag -repo $repo -push

. $collabSamplePath/quote-of-the-day/helpers.ps1

$imageDigest = Get-Digest -repo $repo -containerName quote-of-the-day -tag $tag

az cleanroom config add-application `
    --cleanroom-config $consumerConfig `
    --name demo-app `
    --image "$repo/quote-of-the-day@$imageDigest" `
    --command "python app.py" `
    --cpu 0.5 `
    --memory 4 `
    --ports 8080 `
    --auto-start

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
az cleanroom governance deployment generate `
    --contract-id $contractId `
    --governance-client "ob-consumer-client" `
    --output-dir $outDir/deployments `
    --security-policy-creation-option allow-all

if ($env:COLLAB_FORCE_MANAGED_IDENTITY -eq "true") {
    Import-Module $root/test/onebox/multi-party-collab/force-managed-identity.ps1 -Force -DisableNameChecking
    Force-Managed-Identity `
        -deploymentTemplateFile "$outDir/deployments/cleanroom-arm-template.json" `
        -managedIdentities @($pubResult.mi.id)
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

az cleanroom governance ca propose-enable `
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
pwsh $collabSamplePath/setup-access.ps1 `
    -resourceGroup $publisherResourceGroup `
    -contractId $contractId  `
    -outDir $outDir `
    -kvType akvpremium `
    -governanceClient "ob-publisher-client"