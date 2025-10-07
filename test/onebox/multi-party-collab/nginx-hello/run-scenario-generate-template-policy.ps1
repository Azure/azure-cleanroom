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

    [switch]
    $y,

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
$nginxConfig = "$outDir/configurations/nginx-config"

mkdir -p "$datastoreOutdir"
$nginxDatastoreConfig = "$datastoreOutdir/nginx-hello-nginx-datastore-config"

mkdir -p "$datastoreOutdir/secrets"
$nginxSecretStoreConfig = "$datastoreOutdir/secrets/nginx-hello-nginx-secretstore-config"
$nginxLocalSecretStore = "$datastoreOutdir/secrets/nginx-hello-nginx-secretstore-local"

$resourceGroupTags = ""
if ($env:GITHUB_ACTIONS -eq "true") {
    $nginxResourceGroup = "cl-ob-nginx-${env:JOB_ID}-${env:RUN_ID}"
    $resourceGroupTags = "github_actions=multi-party-collab-${env:JOB_ID}-${env:RUN_ID}"
}
else {
    $user = $env:CODESPACES -eq "true" ? $env:GITHUB_USER : $env:USER
    $nginxResourceGroup = "cl-ob-nginx-${user}"
}

# Set tenant Id as a part of the nginx's member data.
# This is required to enable OIDC provider in the later steps.
$nginxTenantId = az account show --query "tenantId" --output tsv
$proposalId = (az cleanroom governance member set-tenant-id `
        --identifier nginx `
        --tenant-id $nginxTenantId `
        --query "proposalId" `
        --output tsv `
        --governance-client "ob-nginx-client")

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client "ob-nginx-client"

az cleanroom secretstore add `
    --name nginx-local-store `
    --config $nginxSecretStoreConfig `
    --backingstore-type Local_File `
    --backingstore-path $nginxLocalSecretStore

# Create storage account, KV and MI resources.
pwsh $PSScriptRoot/../prepare-resources.ps1 `
    -resourceGroup $nginxResourceGroup `
    -resourceGroupTags $resourceGroupTags `
    -kvType akvpremium `
    -outDir $outDir

$result = Get-Content "$outDir/$nginxResourceGroup/resources.generated.json" | ConvertFrom-Json

az cleanroom config init --cleanroom-config $nginxConfig

$identity = $(az resource show --ids $result.mi.id --query "properties") | ConvertFrom-Json

# Create identity entry in the configuration.
az cleanroom config add-identity az-federated `
    --cleanroom-config $nginxConfig `
    -n nginx-identity `
    --client-id $identity.clientId `
    --tenant-id $identity.tenantId `
    --backing-identity cleanroom_cgs_oidc

# Add DEK and KEK secret stores.
az cleanroom secretstore add `
    --name nginx-dek-store `
    --config $nginxSecretStoreConfig `
    --backingstore-type Azure_KeyVault `
    --backingstore-id $result.dek.kv.id 

az cleanroom secretstore add `
    --name nginx-kek-store `
    --config $nginxSecretStoreConfig `
    --backingstore-type Azure_KeyVault_Managed_HSM `
    --backingstore-id $result.kek.kv.id `
    --attestation-endpoint $result.maa_endpoint

$containerSuffix = $($($(New-Guid).Guid) -replace '-').ToLower()
Write-Host "Using container suffix {$containerSuffix} for application-telemetry"

# $result below refers to the output of the prepare-resources.ps1 that was run earlier.
az cleanroom config set-logging `
    --cleanroom-config $nginxConfig `
    --storage-account $result.sa.id `
    --identity nginx-identity `
    --datastore-config $nginxDatastoreConfig `
    --secretstore-config $nginxSecretStoreConfig `
    --datastore-secret-store nginx-local-store `
    --dek-secret-store nginx-dek-store `
    --kek-secret-store nginx-kek-store `
    --encryption-mode CPK `
    --container-suffix $containerSuffix

$containerSuffix = $($($(New-Guid).Guid) -replace '-').ToLower()
Write-Host "Using container suffix {$containerSuffix} for infrastructure-telemetry"
az cleanroom config set-telemetry `
    --cleanroom-config $nginxConfig `
    --storage-account $result.sa.id `
    --identity nginx-identity `
    --datastore-config $nginxDatastoreConfig `
    --secretstore-config $nginxSecretStoreConfig `
    --datastore-secret-store nginx-local-store `
    --dek-secret-store nginx-dek-store `
    --kek-secret-store nginx-kek-store `
    --encryption-mode CPK `
    --container-suffix $containerSuffix

az cleanroom config add-application `
    --cleanroom-config $nginxConfig `
    --name nginx-hello `
    --image "docker.io/nginxdemos/nginx-hello@sha256:d976f016b32fc381dfb74119cc421d42787b5a63a6b661ab57891b7caa5ad12e" `
    --ports 8080 `
    --cpu 0.5 `
    --memory 4

# Use a pre-built policy bundle if the registry is 'mcr'.
$policyBundleUrl = "cleanroomsamples.azurecr.io/nginx-hello/nginx-hello-policy@sha256:d7b91031287ca532acbc8fa9117f982f694d9412adfd186800edef2adfb2b0e3"
if ($registry -ne "mcr") {
    pwsh $PSScriptRoot/build-policy-bundle.ps1 -tag $tag -repo $repo
    $policyBundleUrl = "${repo}/nginx-hello-policy:$tag"
}
az cleanroom config network http enable `
    --cleanroom-config $nginxConfig `
    --direction inbound `
    --policy $policyBundleUrl

az cleanroom config view `
    --cleanroom-config $nginxConfig `
    --output-file $outDir/configurations/cleanroom-config

az cleanroom config validate --cleanroom-config $outDir/configurations/cleanroom-config

$data = Get-Content -Raw $outDir/configurations/cleanroom-config
az cleanroom governance contract create `
    --data "$data" `
    --id $contractId `
    --governance-client "ob-nginx-client"

# Submitting a contract proposal.
$version = (az cleanroom governance contract show `
        --id $contractId `
        --query "version" `
        --output tsv `
        --governance-client "ob-nginx-client")

az cleanroom governance contract propose `
    --version $version `
    --id $contractId `
    --governance-client "ob-nginx-client"

$contract = (az cleanroom governance contract show `
        --id $contractId `
        --governance-client "ob-nginx-client" | ConvertFrom-Json)

# Accept it.
az cleanroom governance contract vote `
    --id $contractId `
    --proposal-id $contract.proposalId `
    --action accept `
    --governance-client "ob-nginx-client"


mkdir -p $outDir/deployments
# Set overrides if a non-mcr registry is to be used for clean room container images.
if ($registry -ne "mcr") {
    $env:AZCLI_CLEANROOM_CONTAINER_REGISTRY_URL = $repo
    $env:AZCLI_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL = "${repo}/sidecar-digests:$tag"
}

if ($withSecurityPolicy) {
    az cleanroom governance deployment generate `
        --contract-id $contractId `
        --governance-client "ob-nginx-client" `
        --output-dir $outDir/deployments `
        --security-policy-creation-option cached-debug
}
else {
    az cleanroom governance deployment generate `
        --contract-id $contractId `
        --governance-client "ob-nginx-client" `
        --output-dir $outDir/deployments `
        --security-policy-creation-option allow-all
}

az cleanroom governance deployment template propose `
    --template-file $outDir/deployments/cleanroom-arm-template.json `
    --contract-id $contractId `
    --governance-client "ob-nginx-client"

az cleanroom governance deployment policy propose `
    --policy-file $outDir/deployments/cleanroom-governance-policy.json `
    --contract-id $contractId `
    --governance-client "ob-nginx-client"

# Propose enabling log and telemetry collection during cleanroom execution.
az cleanroom governance contract runtime-option propose `
    --option logging `
    --action enable `
    --contract-id $contractId `
    --governance-client "ob-nginx-client"

az cleanroom governance contract runtime-option propose `
    --option telemetry `
    --action enable `
    --contract-id $contractId `
    --governance-client "ob-nginx-client"

az cleanroom governance ca propose-enable `
    --contract-id $contractId `
    --governance-client "ob-nginx-client"

$clientName = "ob-nginx-client"
pwsh $PSScriptRoot/../verify-deployment-proposals.ps1 `
    -cleanroomConfig $nginxConfig `
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
    --governance-client "ob-nginx-client"

az cleanroom governance ca show `
    --contract-id $contractId `
    --governance-client "ob-nginx-client" `
    --query "caCert" `
    --output tsv > $outDir/cleanroomca.crt

# Creates a KEK with SKR policy, wraps DEKs with the KEK and put in kv.
az cleanroom config wrap-deks `
    --contract-id $contractId `
    --cleanroom-config $nginxConfig `
    --datastore-config $nginxDatastoreConfig `
    --secretstore-config $nginxSecretStoreConfig `
    --governance-client "ob-nginx-client"

# Setup OIDC issuer endpoint and managed identity access to storage/KV.
pwsh $PSScriptRoot/../setup-oidc-issuer.ps1 `
    -resourceGroup $nginxResourceGroup `
    -outDir $outDir `
    -governanceClient "ob-nginx-client"
$issuerUrl = Get-Content $outDir/$nginxResourceGroup/issuer-url.txt

pwsh $PSScriptRoot/../setup-access.ps1 `
    -resourceGroup $nginxResourceGroup `
    -subject $contractId `
    -issuerUrl $issuerUrl `
    -outDir $outDir `
    -kvType akvpremium `
    -governanceClient "ob-nginx-client"
