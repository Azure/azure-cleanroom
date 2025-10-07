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
    $clClusterOutDir = "",

    [string]
    $contractId = "analytics",

    [ValidateSet('mcr', 'local', 'acr')]
    [string]$registry = "local",

    [string]$repo = "localhost:5000",

    [string]$tag = "latest",

    [string]$infraType = "virtual",

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
if ($clClusterOutDir -eq "") {
    $clClusterOutDir = "$outDir/cl-cluster"
}

$serviceCert = $ccfOutDir + "/service_cert.pem"
if (-not (Test-Path -Path $serviceCert)) {
    throw "serviceCert at $serviceCert does not exist."
}

$resourceGroupTags = ""
if ($env:GITHUB_ACTIONS -eq "true") {
    $publisherResourceGroup = "cl-ob-publisher-big-data-analytics-${env:JOB_ID}-${env:RUN_ID}"
    $consumerResourceGroup = "cl-ob-consumer-big-data-analytics-${env:JOB_ID}-${env:RUN_ID}"
    $resourceGroupTags = "github_actions=multi-party-collab-${env:JOB_ID}-${env:RUN_ID}"
    $consumerInputS3BucketName = "consumer-input-${env:JOB_ID}-${env:RUN_ID}" # Also update remove-old-buckets.ps1 -Prefix parameter usages if changing bucket name.
    $consumerOutputS3BucketName = "consumer-output-${env:JOB_ID}-${env:RUN_ID}" # Also update remove-old-buckets.ps1 -Prefix parameter usages if changing bucket name.
}
else {
    $user = $env:CODESPACES -eq "true" ? $env:GITHUB_USER : $env:USER
    $publisherResourceGroup = "cl-ob-publisher-big-data-analytics-${user}"
    $consumerResourceGroup = "cl-ob-consumer-big-data-analytics-${user}"
    $consumerInputS3BucketName = "consumer-input-${user}" -replace "_", "-"
    $consumerOutputS3BucketName = "consumer-output-${user}" -replace "_", "-"
}

if ($datastoreOutdir -eq "") {
    $datastoreOutdir = "$outDir/datastores"
}

$serviceCert = $ccfOutDir + "/service_cert.pem"
if (-not (Test-Path -Path $serviceCert)) {
    throw "serviceCert at $serviceCert does not exist."
}

rm -rf "$outDir/configurations"
mkdir -p "$outDir/configurations"
$publisherConfig = "$outDir/configurations/publisher-config"

rm -rf "$datastoreOutdir"
mkdir -p "$datastoreOutdir"
$publisherDatastoreConfig = "$datastoreOutdir/big-data-query-publisher-datastore-config"

rm -rf "$datastoreOutdir/secrets"
mkdir -p "$datastoreOutdir/secrets"
$publisherSecretStoreConfig = "$datastoreOutdir/secrets/big-data-query-publisher-secretstore-config"
$publisherLocalSecretStore = "$datastoreOutdir/secrets/big-data-query-publisher-secretstore-local"

$consumerConfig = "$outDir/configurations/consumer-config"
$consumerDatastoreConfig = "$datastoreOutdir/big-data-query-consumer-datastore-config"

$consumerSecretStoreConfig = "$datastoreOutdir/secrets/big-data-query-consumer-secretstore-config"
$consumerLocalSecretStore = "$datastoreOutdir/secrets/big-data-query-consumer-secretstore-local"

$ownerClient = "ob-cr-owner-client"

# Set tenant Id as a part of the owner's member data.
# This is required to enable OIDC provider in the later steps.
$ownerTenantId = az account show --query "tenantId" --output tsv
$proposalId = (az cleanroom governance member set-tenant-id `
        --identifier cr-owner `
        --tenant-id $ownerTenantId `
        --query "proposalId" `
        --output tsv `
        --governance-client $ownerClient)

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $ownerClient

if ($registry -ne "mcr") {
    $env:AZCLI_CGS_CLIENT_IMAGE = "$repo/cgs-client:$tag"
    $env:AZCLI_CGS_UI_IMAGE = "$repo/cgs-ui:$tag"
}

# Start a local IDP server that can provide token to local users.
$idpPort = "8399"
pwsh $root/test/onebox/multi-party-collab/setup-local-idp.ps1 `
    -outDir $outDir `
    -idpPort $idpPort `
    -cgsProjectName $ownerClient

if ($env:CODESPACES -ne "true" -and $env:GITHUB_ACTIONS -ne "true") {
    $localIdpEndpoint = "http://host.docker.internal:$idpPort"
}
else {
    # 172.17.0.1: https://stackoverflow.com/questions/48546124/what-is-the-linux-equivalent-of-host-docker-internal
    $localIdpEndpoint = "http://172.17.0.1:$idpPort"
}

# Add two users "publisher" and "consumer" to the CCF.
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

$consumerTenantId = [Guid]::NewGuid().ToString()
$consumerUserId = [Guid]::NewGuid().ToString("N")
Write-Output "Adding user $consumerUserId with tenant Id: $consumerTenantId in CCF."
$proposalId = (az cleanroom governance user-identity add `
        --object-id $consumerUserId `
        --identifier consumer `
        --tenant-id $consumerTenantId `
        --account-type microsoft `
        --governance-client $ownerClient `
        --query "proposalId" --output tsv)
az cleanroom governance proposal vote --proposal-id $proposalId --action accept --governance-client $ownerClient

Write-Output "Starting cgs-client for the publisher"
$publisherProjectName = "ob-cr-publisher-user-client"
# Remove the project so as to avoid any caching of oids.
az cleanroom governance client remove --name $publisherProjectName
az cleanroom governance client deploy `
    --ccf-endpoint $ccfEndpoint `
    --use-local-identity `
    --local-identity-endpoint "$localIdpEndpoint/oauth/token?oid=$publisherUserId&tid=$publisherTenantId" `
    --service-cert $serviceCert `
    --name $publisherProjectName

Write-Output "Publisher details"
az cleanroom governance user-identity show --identity-id $publisherUserId --governance-client $publisherProjectName

Write-Output "Starting cgs-client for the consumer"
$consumerProjectName = "ob-cr-consumer-user-client"
# Remove the project so as to avoid any caching of oids.
az cleanroom governance client remove --name $consumerProjectName
az cleanroom governance client deploy `
    --ccf-endpoint $ccfEndpoint `
    --use-local-identity `
    --local-identity-endpoint "$localIdpEndpoint/oauth/token?oid=$consumerUserId&tid=$consumerTenantId" `
    --service-cert $serviceCert `
    --name $consumerProjectName

Write-Output "Consumer details"
az cleanroom governance user-identity show --identity-id $consumerUserId --governance-client $consumerProjectName

# Publish the datasets.
# Create storage account, KV and MI resources for the publisher and publish the data.
pwsh $PSScriptRoot/../prepare-resources.ps1 `
    -resourceGroup $publisherResourceGroup `
    -resourceGroupTags $resourceGroupTags `
    -kvType akvpremium `
    -outDir $outDir

$publisherResult = Get-Content "$outDir/$publisherResourceGroup/resources.generated.json" | ConvertFrom-Json

az cleanroom secretstore add `
    --name publisher-local-store `
    --config $publisherSecretStoreConfig `
    --backingstore-type Local_File `
    --backingstore-path $publisherLocalSecretStore

# Create a datastore entry for azure.
az cleanroom datastore add `
    --name publisher-input `
    --config $publisherDatastoreConfig `
    --secretstore publisher-local-store `
    --secretstore-config $publisherSecretStoreConfig `
    --encryption-mode CPK `
    --backingstore-type Azure_BlobStorage `
    --backingstore-id $publisherResult.sa.id

# Create a publisher-input-sse datastore in Azure Blob Storage (for S3 consumer flows)
az cleanroom datastore add `
    --name publisher-input-sse `
    --config $publisherDatastoreConfig `
    --encryption-mode SSE `
    --backingstore-type Azure_BlobStorage `
    --backingstore-id $publisherResult.sa.id

pwsh $root/test/onebox/multi-party-collab/wait-for-container-access.ps1 `
    --containerName publisher-input `
    --storageAccountId $publisherResult.sa.id

pwsh $root/test/onebox/multi-party-collab/wait-for-container-access.ps1 `
    --containerName publisher-input-sse `
    --storageAccountId $publisherResult.sa.id

. $PSScriptRoot/get-input-data.ps1

mkdir -p $PSScriptRoot/publisher/input
$today = [DateTimeOffset]"2025-09-01"
Get-PublisherData -dataDir $PSScriptRoot/publisher/input -startDate $today

az cleanroom datastore upload `
    --name publisher-input `
    --config $publisherDatastoreConfig `
    --src $PSScriptRoot/publisher/input

az cleanroom datastore upload `
    --name publisher-input-sse `
    --config $publisherDatastoreConfig `
    --src $PSScriptRoot/publisher/input

# Add DEK and KEK secret stores.
az cleanroom secretstore add `
    --name publisher-dek-store `
    --config $publisherSecretStoreConfig `
    --backingstore-type Azure_KeyVault `
    --backingstore-id $publisherResult.dek.kv.id 

az cleanroom secretstore add `
    --name publisher-kek-store `
    --config $publisherSecretStoreConfig `
    --backingstore-type Azure_KeyVault_Managed_HSM `
    --backingstore-id $publisherResult.kek.kv.id `
    --attestation-endpoint $publisherResult.maa_endpoint

# Create storage account, KV and MI resources for the consumer and publish the data.
pwsh $PSScriptRoot/../prepare-resources.ps1 `
    -resourceGroup $consumerResourceGroup `
    -resourceGroupTags $resourceGroupTags `
    -kvType akvpremium `
    -outDir $outDir

$consumerResult = Get-Content "$outDir/$consumerResourceGroup/resources.generated.json" | ConvertFrom-Json

az cleanroom secretstore add `
    --name consumer-local-store `
    --config $consumerSecretStoreConfig `
    --backingstore-type Local_File `
    --backingstore-path $consumerLocalSecretStore

# Create a datastore entry.
az cleanroom datastore add `
    --name consumer-input `
    --config $consumerDatastoreConfig `
    --secretstore consumer-local-store `
    --secretstore-config $consumerSecretStoreConfig `
    --encryption-mode CPK `
    --backingstore-type Azure_BlobStorage `
    --backingstore-id $consumerResult.sa.id

pwsh $root/test/onebox/multi-party-collab/wait-for-container-access.ps1 `
    --containerName consumer-input `
    --storageAccountId $consumerResult.sa.id

mkdir -p $PSScriptRoot/consumer/input
Get-ConsumerData -dataDir $PSScriptRoot/consumer/input

az cleanroom datastore upload `
    --name consumer-input `
    --config $consumerDatastoreConfig `
    --src $PSScriptRoot/consumer/input

pwsh $PSScriptRoot/create-bucket.ps1 `
    -bucketName $consumerInputS3BucketName

pwsh $PSScriptRoot/upload-bucket.ps1 `
    -bucketName $consumerInputS3BucketName `
    -src $PSScriptRoot/consumer/input

pwsh $PSScriptRoot/create-bucket.ps1 `
    -bucketName $consumerOutputS3BucketName

# Create a datastore entry.
az cleanroom datastore add `
    --name consumer-output `
    --config $consumerDatastoreConfig `
    --secretstore consumer-local-store `
    --secretstore-config $consumerSecretStoreConfig `
    --encryption-mode CPK `
    --backingstore-type Azure_BlobStorage `
    --backingstore-id $consumerResult.sa.id

# Add DEK and KEK secret stores.
az cleanroom secretstore add `
    --name consumer-dek-store `
    --config $consumerSecretStoreConfig `
    --backingstore-type Azure_KeyVault `
    --backingstore-id $consumerResult.dek.kv.id 

az cleanroom secretstore add `
    --name consumer-kek-store `
    --config $consumerSecretStoreConfig `
    --backingstore-type Azure_KeyVault_Managed_HSM `
    --backingstore-id $consumerResult.kek.kv.id `
    --attestation-endpoint $consumerResult.maa_endpoint

# Use CCF service certificate discovery to dynamically figure out the CCF network's
# service certificate.
$agent = Get-Content $ccfOutDir/ccf.recovery-agent.json | ConvertFrom-Json
$agentEndpoint = $agent.endpoint
$agentNetworkReport = curl -k -s -S $agentEndpoint/network/report | ConvertFrom-Json
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
"@ > $outDir/cl-cluster/contract.json

$data = Get-Content -Raw $outDir/cl-cluster/contract.json
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

# Section: Publisher publishes datasets.
# TODO (HPrabh): These commands are placeholders until the analytics publish-dataset command is created.
# az cleanroom analytics-workload dataset create `
#     --name publisher-input `
#     --format csv `
#     --schema {}
#     --datastore $datasource
az cleanroom config init --cleanroom-config $publisherConfig

$identity = $(az resource show --ids $publisherResult.mi.id --query "properties") | ConvertFrom-Json

# Create identity entry in the configuration.
pwsh $PSScriptRoot/../setup-oidc-issuer.ps1 `
    -resourceGroup $publisherResourceGroup `
    -outDir $outDir `
    -oidcIssuerLevel "user" `
    -governanceClient $publisherProjectName
$publisherIssuerUrl = Get-Content $outDir/$publisherResourceGroup/issuer-url.txt

az cleanroom config add-identity az-federated `
    --cleanroom-config $publisherConfig `
    -n publisher-identity `
    --client-id $identity.clientId `
    --tenant-id $identity.tenantId `
    --issuer-url $publisherIssuerUrl `
    --backing-identity cleanroom_cgs_oidc

# Create a datasource entry in the configuration.
az cleanroom config add-datasource `
    --cleanroom-config $publisherConfig `
    --datastore-name publisher-input `
    --datastore-config $publisherDatastoreConfig `
    --secretstore-config $publisherSecretStoreConfig `
    --dek-secret-store publisher-dek-store `
    --kek-secret-store publisher-kek-store `
    --identity publisher-identity 

az cleanroom config add-datasource `
    --cleanroom-config $publisherConfig `
    --datastore-name publisher-input-sse `
    --datastore-config $publisherDatastoreConfig `
    --identity publisher-identity

$config = Get-Content $publisherConfig | ConvertFrom-Yaml
$publisherDatasource = $config["datasources"] | Where-Object { $_.name -eq "publisher-input" }
$publisherSSEDatasource = $config["datasources"] | Where-Object { $_.name -eq "publisher-input-sse" }

# Create a datastore entry for the AWS S3 storage to be used as a datasink with CGS secret Id as 
# its configuration.
$awsAccessKeyId = az keyvault secret show  --vault-name azcleanroomemukv -n aws-access-key-id --query value -o tsv
$awsSecretAccessKey = az keyvault secret show  --vault-name azcleanroomemukv -n aws-secret-access-key --query value -o tsv
$secretConfig = @{
    awsAccessKeyId     = $awsAccessKeyId
    awsSecretAccessKey = $awsSecretAccessKey
} | ConvertTo-Json | base64 -w 0

$awsConfigCgsSecretName = "consumer-aws-config"
$awsConfigCgsSecretId = (az cleanroom governance contract secret set `
        --secret-name $awsConfigCgsSecretName `
        --value $secretConfig `
        --contract-id $contractId `
        --governance-client $consumerProjectName `
        --query "secretId" `
        --output tsv)

$awsUrl = "https://s3.amazonaws.com"
az cleanroom datastore add `
    --name consumer-output-s3 `
    --config $consumerDatastoreConfig `
    --backingstore-type Aws_S3 `
    --backingstore-id $awsUrl `
    --aws-config-cgs-secret-id $awsConfigCgsSecretId `
    --container-name $consumerOutputS3BucketName

az cleanroom datastore add `
    --name consumer-input-s3 `
    --config $consumerDatastoreConfig `
    --backingstore-type Aws_S3 `
    --backingstore-id $awsUrl `
    --aws-config-cgs-secret-id $awsConfigCgsSecretId `
    --container-name $consumerInputS3BucketName

# Section: Consumer publishes datasets and queries.
az cleanroom config init --cleanroom-config $consumerConfig

$identity = $(az resource show --ids $consumerResult.mi.id --query "properties") | ConvertFrom-Json

# Create identity entry in the configuration.
pwsh $PSScriptRoot/../setup-oidc-issuer.ps1 `
    -resourceGroup $consumerResourceGroup `
    -outDir $outDir `
    -oidcIssuerLevel "user" `
    -governanceClient $consumerProjectName
$consumerIssuerUrl = Get-Content $outDir/$consumerResourceGroup/issuer-url.txt

az cleanroom config add-identity az-federated `
    --cleanroom-config $consumerConfig `
    -n consumer-identity `
    --client-id $identity.clientId `
    --tenant-id $identity.tenantId `
    --issuer-url $consumerIssuerUrl `
    --backing-identity cleanroom_cgs_oidc

# Create a datasource entry in the configuration.
az cleanroom config add-datasource `
    --cleanroom-config $consumerConfig `
    --datastore-name consumer-input `
    --datastore-config $consumerDatastoreConfig `
    --secretstore-config $consumerSecretStoreConfig `
    --dek-secret-store consumer-dek-store `
    --kek-secret-store consumer-kek-store `
    --identity consumer-identity 

az cleanroom config add-datasink `
    --cleanroom-config $consumerConfig `
    --datastore-name consumer-output `
    --datastore-config $consumerDatastoreConfig `
    --secretstore-config $consumerSecretStoreConfig `
    --dek-secret-store consumer-dek-store `
    --kek-secret-store consumer-kek-store `
    --identity consumer-identity

az cleanroom config add-datasource `
    --cleanroom-config $consumerConfig `
    --datastore-name consumer-input-s3 `
    --datastore-config $consumerDatastoreConfig

az cleanroom config add-datasink `
    --cleanroom-config $consumerConfig `
    --datastore-name consumer-output-s3 `
    --datastore-config $consumerDatastoreConfig

$config = Get-Content $consumerConfig | ConvertFrom-Yaml
$consumerDatasource = $config["datasources"] | Where-Object { $_.name -eq "consumer-input" }
$datasink = $config["datasinks"] | Where-Object { $_.name -eq "consumer-output" }
$consumerS3Datasource = $config["datasources"] | Where-Object { $_.name -eq "consumer-input-s3" }
$s3datasink = $config["datasinks"] | Where-Object { $_.name -eq "consumer-output-s3" }

# Publish the datasets.
$datasetsContractId = $contractId
# $datasetsContractId = "datasets"
# mkdir -p $outDir/datasets
# Write-Output "Creating datasets contract $datasetsContractId..."
# @"
# {
# }
# "@ > $outDir/datasets/contract.json

# $data = Get-Content -Raw $outDir/datasets/contract.json
# az cleanroom governance contract create `
#     --data "$data" `
#     --id $datasetsContractId `
#     --governance-client $ownerClient

# # Submitting a contract proposal.
# $version = (az cleanroom governance contract show `
#         --id $datasetsContractId `
#         --query "version" `
#         --output tsv `
#         --governance-client $ownerClient)

# az cleanroom governance contract propose `
#     --version $version `
#     --id $datasetsContractId `
#     --governance-client $ownerClient

# $contract = (az cleanroom governance contract show `
#         --id $datasetsContractId `
#         --governance-client $ownerClient | ConvertFrom-Json)

# # Accept it.
# az cleanroom governance contract vote `
#     --id $datasetsContractId `
#     --proposal-id $contract.proposalId `
#     --action accept `
#     --governance-client $ownerClient

# TODO (HPrabh): This is a placeholder until the analytics publish-dataset command is created.
# If the run-collab-aci.ps1 script is run again then for the new contract Id we need unique document
# IDs to be created.
$runId = (New-Guid).ToString().Substring(0, 8)
$publisherInputDatasetName = "publisher-input-$runId"
$publisherInputSSEDatasetName = "publisher-input-sse-$runId"
$consumerInputDatasetName = "consumer-input-$runId"
$consumerOutputDatasetName = "consumer-output-$runId"
$consumerInputS3DatasetName = "consumer-input-s3-$runId"
$consumerOutputS3DatasetName = "consumer-output-s3-$runId"
$publisherInputDataset = [ordered]@{
    "name"        = "$publisherInputDatasetName"
    "format"      = "csv"
    "schema"      = [ordered]@{
        "date"     = @{ "type" = "date" }
        "time"     = @{ "type" = "string" }
        "author"   = @{ "type" = "string" }
        "mentions" = @{ "type" = "string" }
    }
    "accessPoint" = $publisherDatasource
} 
$publisherInputSSEDataset = [ordered]@{
    "name"        = "$publisherInputSSEDatasetName"
    "format"      = "csv"
    "schema"      = [ordered]@{
        "date"     = @{ "type" = "date" }
        "time"     = @{ "type" = "string" }
        "author"   = @{ "type" = "string" }
        "mentions" = @{ "type" = "string" }
    }
    "accessPoint" = $publisherSSEDatasource
}
$consumerInputDataset = [ordered]@{
    "name"        = "$consumerInputDatasetName"
    "format"      = "csv"
    "schema"      = [ordered]@{
        "date"     = @{ "type" = "date" }
        "time"     = @{ "type" = "string" }
        "author"   = @{ "type" = "string" }
        "mentions" = @{ "type" = "string" }
    }
    "accessPoint" = $consumerDatasource
}

$consumerInputS3Dataset = [ordered]@{
    "name"        = "$consumerInputS3DatasetName"
    "format"      = "csv"
    "schema"      = [ordered]@{
        "date"     = @{ "type" = "date" }
        "time"     = @{ "type" = "string" }
        "author"   = @{ "type" = "string" }
        "mentions" = @{ "type" = "string" }
    }
    "accessPoint" = $consumerS3Datasource
}

$outDataset = [ordered]@{
    "name"        = "$consumerOutputDatasetName"
    "format"      = "csv"
    "schema"      = [ordered]@{
        "author"             = @{ "type" = "string" }
        "Number_Of_Mentions" = @{ "type" = "long" }
    }
    "accessPoint" = $datasink
}

$outS3Dataset = [ordered]@{
    "name"        = "$consumerOutputS3DatasetName"
    "format"      = "csv"
    "schema"      = [ordered]@{
        "author"             = @{ "type" = "string" }
        "Number_Of_Mentions" = @{ "type" = "long" }
    }
    "accessPoint" = $s3Datasink
}

$documentId = $publisherInputDatasetName
# Need to use the ", @(...)" syntax to force ConvertTo-Json to treat "documentApprovers" as an array.
# Otherwise, it creates it as a single json string.
$documentApprovers = , @(
    @{
        "id"   = "$publisherUserId"
        "type" = "user"
    }
) | ConvertTo-Json -Depth 100
Write-Output "Adding user document for publisher input dataset with approvers as $documentApprovers..."
# TODO (HPrabh): This is a placeholder until the analytics publish-dataset command is created.
# Create user documents for the datasets.
az cleanroom governance user-document create `
    --data $($publisherInputDataset | ConvertTo-Json -Depth 100)`
    --id $documentId `
    --approvers $documentApprovers `
    --contract-id $datasetsContractId `
    --governance-client $publisherProjectName

az cleanroom governance user-document show `
    --id $documentId `
    --governance-client $publisherProjectName | jq

Write-Output "Submitting user document proposal for input dataset"
$version = (az cleanroom governance user-document show `
        --id $documentId `
        --governance-client $publisherProjectName `
        --query "version" `
        --output tsv)
$proposalId = (az cleanroom governance user-document propose `
        --version $version `
        --id $documentId `
        --governance-client $publisherProjectName `
        --query "proposalId" `
        --output tsv)

Write-Output "Accepting the user document proposal"
az cleanroom governance user-document vote `
    --id $documentId `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $publisherProjectName | jq

$documentId = $publisherInputSSEDatasetName
Write-Output "Adding user document for publisher input SSE dataset with approvers as $documentApprovers..."
az cleanroom governance user-document create `
    --data $($publisherInputSSEDataset | ConvertTo-Json -Depth 100)`
    --id $documentId `
    --approvers $documentApprovers `
    --contract-id $datasetsContractId `
    --governance-client $publisherProjectName

az cleanroom governance user-document show `
    --id $documentId `
    --governance-client $publisherProjectName | jq

Write-Output "Submitting user document proposal for publisher input SSE dataset"
$version = (az cleanroom governance user-document show `
        --id $documentId `
        --governance-client $publisherProjectName `
        --query "version" `
        --output tsv)
$proposalId = (az cleanroom governance user-document propose `
        --version $version `
        --id $documentId `
        --governance-client $publisherProjectName `
        --query "proposalId" `
        --output tsv)

Write-Output "Accepting the user document proposal for publisher input SSE dataset"
az cleanroom governance user-document vote `
    --id $documentId `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $publisherProjectName | jq

$documentId = $consumerInputDatasetName
$documentApprovers = , @(
    @{
        "id"   = "$consumerUserId"
        "type" = "user"
    }
) | ConvertTo-Json -Depth 100
Write-Output "Adding user document for consumer input dataset with approvers as $documentApprovers..."
az cleanroom governance user-document create `
    --data $($consumerInputDataset | ConvertTo-Json -Depth 100) `
    --id $documentId `
    --approvers $documentApprovers `
    --contract-id $datasetsContractId `
    --governance-client $consumerProjectName

Write-Output "Submitting user document proposal for consumer input dataset"
$version = (az cleanroom governance user-document show `
        --id $documentId `
        --governance-client $consumerProjectName `
        --query "version" `
        --output tsv)
$proposalId = (az cleanroom governance user-document propose `
        --version $version `
        --id $documentId `
        --governance-client $consumerProjectName `
        --query "proposalId" `
        --output tsv)

Write-Output "Accepting the user document proposal"
az cleanroom governance user-document vote --id $documentId --proposal-id $proposalId --action accept --governance-client $consumerProjectName | jq

$documentId = $consumerInputS3DatasetName
Write-Output "Adding user document for consumer input S3 dataset with approvers as $documentApprovers..."
az cleanroom governance user-document create `
    --data $($consumerInputS3Dataset | ConvertTo-Json -Depth 100) `
    --id $documentId `
    --approvers $documentApprovers `
    --contract-id $datasetsContractId `
    --governance-client $consumerProjectName

Write-Output "Submitting user document proposal for consumer input dataset"
$version = (az cleanroom governance user-document show `
        --id $documentId `
        --governance-client $consumerProjectName `
        --query "version" `
        --output tsv)
$proposalId = (az cleanroom governance user-document propose `
        --version $version `
        --id $documentId `
        --governance-client $consumerProjectName `
        --query "proposalId" `
        --output tsv)

Write-Output "Accepting the user document proposal"
az cleanroom governance user-document vote --id $documentId --proposal-id $proposalId --action accept --governance-client $consumerProjectName | jq

Write-Output "Adding user document for consumer-output dataset with approvers as $documentApprovers..."
$documentId = $consumerOutputDatasetName
az cleanroom governance user-document create `
    --data $($outDataset | ConvertTo-Json -Depth 100) `
    --id $documentId `
    --approvers $documentApprovers `
    --contract-id $datasetsContractId `
    --governance-client $consumerProjectName

Write-Output "Submitting user document proposal for consumer-output dataset"
$version = (az cleanroom governance user-document show `
        --id $documentId `
        --governance-client $consumerProjectName `
        --query "version" `
        --output tsv)
$proposalId = (az cleanroom governance user-document propose `
        --version $version `
        --id $documentId `
        --governance-client $consumerProjectName `
        --query "proposalId" `
        --output tsv)
Write-Output "Accepting the user document proposal"
az cleanroom governance user-document vote `
    --id $documentId `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $consumerProjectName | jq

Write-Output "Adding user document for consumer-output S3 dataset with approvers as $documentApprovers..."
$documentId = $consumerOutputS3DatasetName
az cleanroom governance user-document create `
    --data $($outS3Dataset | ConvertTo-Json -Depth 100) `
    --id $documentId `
    --approvers $documentApprovers `
    --contract-id $datasetsContractId `
    --governance-client $consumerProjectName

Write-Output "Submitting user document proposal for consumer-output S3 dataset"
$version = (az cleanroom governance user-document show `
        --id $documentId `
        --governance-client $consumerProjectName `
        --query "version" `
        --output tsv)
$proposalId = (az cleanroom governance user-document propose `
        --version $version `
        --id $documentId `
        --governance-client $consumerProjectName `
        --query "proposalId" `
        --output tsv)
Write-Output "Accepting the user document proposal"
az cleanroom governance user-document vote `
    --id $documentId `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $consumerProjectName | jq

# Publish the query document.
# The analytics client will create the query document with the raw query, referred datasets and the output datasink.
# az cleanroom analytics-workload query create `
#     --id $queryDocumentId `
#     --contract-id $datasetsContractId `
#     --query "$query" `
#     --datasets "data=/datasets/publisher-input"

# TODO (HPrabh): This is a placeholder until the analytics publish-query command is created.
$query = Get-Content "$PSScriptRoot/consumer/query/query.txt"
$queryDocumentId = "consumer-query-$runId"
@"
{
    "query" : "$query",
    "datasets": {
        "publisher_data": "$publisherInputDatasetName",
        "consumer_data": "$consumerInputDatasetName"
    },
    "datasink": "$consumerOutputDatasetName"
}
"@ > $outDir/consumer-query.json

$documentApprovers = @(
    @{
        "id"   = "$publisherUserId"
        "type" = "user"
    },
    @{
        "id"   = "$consumerUserId"
        "type" = "user"
    }
) | ConvertTo-Json -Depth 100 | Out-File "$outDir/consumer-query-approvers.json"
$queryDocument = Get-Content -Raw $outDir/consumer-query.json

Write-Output "Adding user document for query with approvers as $(Get-Content -Raw $outDir/consumer-query-approvers.json)..."
az cleanroom governance user-document create `
    --data $queryDocument `
    --id $queryDocumentId `
    --approvers $outDir/consumer-query-approvers.json `
    --contract-id $contractId `
    --governance-client $consumerProjectName

Write-Output "Submitting user document proposal for query"
$version = (az cleanroom governance user-document show `
        --id $queryDocumentId `
        --governance-client $consumerProjectName `
        --query "version" `
        --output tsv)
$proposalId = (az cleanroom governance user-document propose `
        --version $version `
        --id $queryDocumentId `
        --governance-client $consumerProjectName `
        --query "proposalId" `
        --output tsv)
Write-Output "Accepting the user document proposal for query as consumer"
az cleanroom governance user-document vote `
    --id $queryDocumentId `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $consumerProjectName | jq

Write-Output "Accepting the user document proposal for query as publisher"
$proposalId = (az cleanroom governance user-document show `
        --id $queryDocumentId `
        --governance-client $publisherProjectName `
        --query "proposalId" `
        --output tsv)
az cleanroom governance user-document vote `
    --id $queryDocumentId `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $publisherProjectName | jq

Write-Output "Creating query document with S3 datasource and datasink..."
$s3QueryDocumentId = "consumer-query-s3-$runId"
@"
{
    "query" : "$query",
    "datasets": {
        "publisher_data": "$publisherInputSSEDatasetName",
        "consumer_data": "$consumerInputS3DatasetName"
    },
    "datasink": "$consumerOutputS3DatasetName"
}
"@ > $outDir/consumer-query-s3.json
$queryDocument = Get-Content -Raw $outDir/consumer-query-s3.json

Write-Output "Adding user document for query with approvers as $(Get-Content -Raw $outDir/consumer-query-approvers.json)..."
az cleanroom governance user-document create `
    --data $queryDocument `
    --id $s3QueryDocumentId `
    --approvers $outDir/consumer-query-approvers.json `
    --contract-id $contractId `
    --governance-client $consumerProjectName

Write-Output "Submitting user document proposal for query"
$version = (az cleanroom governance user-document show `
        --id $s3QueryDocumentId `
        --governance-client $consumerProjectName `
        --query "version" `
        --output tsv)
$proposalId = (az cleanroom governance user-document propose `
        --version $version `
        --id $s3QueryDocumentId `
        --governance-client $consumerProjectName `
        --query "proposalId" `
        --output tsv)
Write-Output "Accepting the user document proposal for query as consumer"
az cleanroom governance user-document vote `
    --id $s3QueryDocumentId `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $consumerProjectName | jq

Write-Output "Accepting the user document proposal for query as publisher"
$proposalId = (az cleanroom governance user-document show `
        --id $s3QueryDocumentId `
        --governance-client $publisherProjectName `
        --query "proposalId" `
        --output tsv)
az cleanroom governance user-document vote `
    --id $s3QueryDocumentId `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $publisherProjectName | jq

Write-Output "Submitting a malicious query and approving it..."
$query = Get-Content "$PSScriptRoot/consumer/query/malicious_query.txt"
$maliciousQueryDocumentId = "consumer-malicious-query-$runId"
@"
{
    "query" : "$query",
    "datasets": {
        "publisher_data": "$publisherInputDatasetName"
    },
    "datasink": "$consumerOutputDatasetName"
}
"@ > $outDir/consumer-malicious-query.json

$documentApprovers = , @(
    @{
        "id"   = "$consumerUserId"
        "type" = "user"
    }
) | ConvertTo-Json -Depth 100 | Out-File $outDir/consumer-malicious-query-approvers.json
$queryDocument = Get-Content -Raw $outDir/consumer-malicious-query.json

Write-Output "Adding user document for malicious query with approvers as $(Get-Content -Raw $outDir/consumer-malicious-query-approvers.json)..."
az cleanroom governance user-document create `
    --data $queryDocument `
    --id $maliciousQueryDocumentId `
    --approvers $outDir/consumer-malicious-query-approvers.json `
    --contract-id $contractId `
    --governance-client $consumerProjectName

Write-Output "Submitting user document proposal for malicious query"
$version = (az cleanroom governance user-document show `
        --id $maliciousQueryDocumentId `
        --governance-client $consumerProjectName `
        --query "version" `
        --output tsv)
$proposalId = (az cleanroom governance user-document propose `
        --version $version `
        --id $maliciousQueryDocumentId `
        --governance-client $consumerProjectName `
        --query "proposalId" `
        --output tsv)
Write-Output "Accepting the user document proposal for malicious query as consumer"
az cleanroom governance user-document vote `
    --id $maliciousQueryDocumentId `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $consumerProjectName | jq

mkdir -p $outDir/deployments
$repoConfig = Get-Content $outDir/cl-cluster/repoConfig.json | ConvertFrom-Json
$clusterProviderProjectName = $repoConfig.clusterProviderProjectName

if ($withSecurityPolicy) {
    $option = "cached-debug"
}
else {
    $option = "allow-all"
}

Write-Output "Generating deployment template/policy with $option creation option for analytics workload..."
az cleanroom cluster analytics-workload deployment generate `
    --contract-id $contractId `
    --governance-client $ownerClient `
    --output-dir $outDir/deployments `
    --security-policy-creation-option $option `
    --infra-type $infraType `
    --provider-client $clusterProviderProjectName `
    --provider-config $outDir/cl-cluster/providerConfig.json

Write-Output "Setting deployment template..."
az cleanroom governance deployment template propose `
    --contract-id $contractId `
    --template-file $outDir/deployments/analytics-workload.deployment-template.json `
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
    --policy-file $outDir/deployments/analytics-workload.governance-policy.json `
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

# Deploy the analytics agent using the CGS /deploymentspec endpoint as the analytics config endpoint.
@"
{
    "url": "${ccfEndpoint}/app/contracts/$contractId/deploymentspec",
    "caCert": "$((Get-Content $serviceCert -Raw).ReplaceLineEndings("\n"))"
}
"@ > $outDir/analytics-workload-config-endpoint.json

pwsh $root/samples/spark/azcli/enable-analytics-workload.ps1 `
    -outDir $outDir/cl-cluster `
    -securityPolicyCreationOption $option `
    -configEndpointFile $outDir/analytics-workload-config-endpoint.json

# Creates a KEK with SKR policy, wraps DEKs with the KEK and put in kv.
az cleanroom config wrap-deks `
    --contract-id $contractId `
    --cleanroom-config $publisherConfig `
    --datastore-config $publisherDatastoreConfig `
    --secretstore-config $publisherSecretStoreConfig `
    --governance-client $publisherProjectName

# Setup OIDC issuer and managed identity access to storage/KV in publisher tenant.
$subject = $contractId + "-" + $publisherUserId
pwsh $PSScriptRoot/../setup-access.ps1 `
    -resourceGroup $publisherResourceGroup `
    -subject $subject `
    -issuerUrl $publisherIssuerUrl `
    -outDir $outDir `
    -kvType akvpremium `
    -governanceClient $ownerClient

# Creates a KEK with SKR policy, wraps DEKs with the KEK and put in kv.
az cleanroom config wrap-deks `
    --contract-id $contractId `
    --cleanroom-config $consumerConfig `
    --datastore-config $consumerDatastoreConfig `
    --secretstore-config $consumerSecretStoreConfig `
    --governance-client $consumerProjectName

# Setup managed identity access to storage/KV in consumer tenant.
$subject = $contractId + "-" + $consumerUserId
pwsh $PSScriptRoot/../setup-access.ps1 `
    -resourceGroup $consumerResourceGroup `
    -subject $subject `
    -issuerUrl $consumerIssuerUrl `
    -outDir $outDir `
    -kvType akvpremium `
    -governanceClient $ownerClient

@"
{
    "queryDocumentId": "$queryDocumentId",
    "s3QueryDocumentId": "$s3QueryDocumentId",
    "maliciousQueryDocumentId": "$maliciousQueryDocumentId",
    "cgsClient": "$consumerProjectName"
}
"@ > $outDir/submitSqlJobConfig.json

pwsh $PSScriptRoot/submit-sql-job.ps1 -outDir $outDir

mkdir -p $outDir/s3queryOutput
pwsh $PSScriptRoot/download-bucket.ps1 `
    -bucketName $consumerOutputS3BucketName `
    -dst $outDir/s3queryOutput/$contractId
