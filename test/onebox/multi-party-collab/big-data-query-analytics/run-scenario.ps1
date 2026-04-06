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
    $contractId = "analytics",

    [ValidateSet('mcr', 'local', 'acr')]
    [string]$registry = "local",

    [string]$repo = "localhost:5000",

    [string]$tag = "latest",

    [string]$frontendServiceEndpoint = "localhost:61001",

    [string]$mockMembershipMgrEndpoint = "localhost:61003",

    [ValidateSet('json', 'parquet')]
    [string[]]$additionalFormats = @(),

    [switch]
    $useFrontendService,

    [switch]
    $withSecurityPolicy,

    [string]$location = "centralindia"
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
mkdir -p $outDir

$ccfOutDir = "$deploymentConfigDir/ccf"
$clClusterOutDir = "$deploymentConfigDir/cl-cluster"

$serviceCert = $ccfOutDir + "/service_cert.pem"
if (-not (Test-Path -Path $serviceCert)) {
    throw "serviceCert at $serviceCert does not exist."
}

$resourceGroupTags = ""
if ($env:GITHUB_ACTIONS -eq "true") {
    $publisherResourceGroup = "cl-ob-publisher-big-data-analytics-${env:JOB_ID}-${env:RUN_ID}"
    $consumerResourceGroup = "cl-ob-consumer-big-data-analytics-${env:JOB_ID}-${env:RUN_ID}"
    $resourceGroupTags = "github_actions=${env:JOB_ID}-${env:RUN_ID}"
    $consumerInputS3BucketName = "consumer-input-big-data-${env:RUN_ID}" # Also update remove-old-buckets.ps1 -Prefix parameter usages if changing bucket name.
    $consumerOutputS3BucketName = "consumer-output-big-data-${env:RUN_ID}" # Also update remove-old-buckets.ps1 -Prefix parameter usages if changing bucket name.
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

rm -rf "$datastoreOutdir"
mkdir -p "$datastoreOutdir"
$publisherDatastoreConfig = "$datastoreOutdir/big-data-query-publisher-datastore-config"

rm -rf "$datastoreOutdir/secrets"
mkdir -p "$datastoreOutdir/secrets"
$publisherSecretStoreConfig = "$datastoreOutdir/secrets/big-data-query-publisher-secretstore-config"
$publisherLocalSecretStore = "$datastoreOutdir/secrets/big-data-query-publisher-secretstore-local"

$consumerDatastoreConfig = "$datastoreOutdir/big-data-query-consumer-datastore-config"

$consumerSecretStoreConfig = "$datastoreOutdir/secrets/big-data-query-consumer-secretstore-config"
$consumerLocalSecretStore = "$datastoreOutdir/secrets/big-data-query-consumer-secretstore-local"

# Set tenant Id as a part of the owner's member data.
# This is required to enable OIDC provider in the later steps.
$ownerTenantId = az account show --query "tenantId" --output tsv
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

# Remove stale config files from previous runs to avoid confusion.
Remove-Item -Path "$outDir/collaboration-config-*.yaml" -Force -ErrorAction SilentlyContinue
$runId = (New-Guid).ToString().Substring(0, 8)
$env:CLEANROOM_COLLABORATION_CONFIG_FILE = "$outDir/collaboration-config-$runId.yaml"

Write-Output "Starting cgs-client for the publisher"
$publisherProjectName = "ob-cr-publisher-user-client"
$envFilePath = "$ccfOutDir/governance-client.env"
# Remove the project so as to avoid any caching of oids.
az cleanroom governance client remove --name $publisherProjectName
az cleanroom governance client deploy `
    --ccf-endpoint $ccfEndpoint `
    --use-local-identity `
    --local-identity-endpoint "$localIdpEndpoint/oauth/token?oid=$publisherUserId&tid=$publisherTenantId&preferred_username=publisher@example.com" `
    --service-cert $serviceCert `
    --name $publisherProjectName `
    --env-file $envFilePath

Write-Output "Publisher details"
az cleanroom governance user-identity show --identity-id $publisherUserId --governance-client $publisherProjectName

write-Host "Getting publisher user token for the frontend call"
$publisherUserToken = (az cleanroom governance client get-access-token --query accessToken -o tsv --name $publisherProjectName)

az cleanroom collaboration context add `
    --collaboration-name $publisherProjectName `
    --collaborator-id $publisherUserId `
    --governance-client $publisherProjectName

Write-Output "Starting cgs-client for the consumer"
$consumerProjectName = "ob-cr-consumer-user-client"
# Remove the project so as to avoid any caching of oids.
az cleanroom governance client remove --name $consumerProjectName
az cleanroom governance client deploy `
    --ccf-endpoint $ccfEndpoint `
    --use-local-identity `
    --local-identity-endpoint "$localIdpEndpoint/oauth/token?oid=$consumerUserId&tid=$consumerTenantId&preferred_username=consumer@example.com" `
    --service-cert $serviceCert `
    --name $consumerProjectName `
    --env-file $envFilePath

Write-Output "Consumer details"
az cleanroom governance user-identity show --identity-id $consumerUserId --governance-client $consumerProjectName

write-Host "Getting consumer user token for the frontend call"
$userToken = (az cleanroom governance client get-access-token --query accessToken -o tsv --name $consumerProjectName)
if ($useFrontendService) {
    write-Host "Waiting for mock membership manager to be ready..."
    curl --fail-with-body -sS -X GET http://${mockMembershipMgrEndpoint}/ready `
        -H "content-type: application/json"

    $serviceCertPem = Get-Content -Path "$ccfOutDir/service_cert.pem" -Raw
    $consortiumDetails = @{
        "ccfEndpoint"       = $ccfEndpoint
        "ccfServiceCertPem" = $serviceCertPem
    } | ConvertTo-Json -Depth 10

    write-Host "Adding collaboration details to mock membership manager..."
    curl --fail-with-body -sS -X POST http://${mockMembershipMgrEndpoint}/collaborations/${consumerProjectName} `
        -H "content-type: application/json" `
        -d $consortiumDetails

    curl --fail-with-body -sS -X POST http://${mockMembershipMgrEndpoint}/collaborations/${publisherProjectName} `
        -H "content-type: application/json" `
        -d $consortiumDetails
}

az cleanroom collaboration context add `
    --collaboration-name $consumerProjectName `
    --collaborator-id $consumerUserId `
    --governance-client $consumerProjectName

# Add the datastores.
# Create storage account, KV and MI resources for the publisher and publish the data.
pwsh $PSScriptRoot/../prepare-resources.ps1 `
    -resourceGroup $publisherResourceGroup `
    -resourceGroupTags $resourceGroupTags `
    -kvType akvpremium `
    -storageType blob `
    -outDir $outDir `
    -location $location

$publisherResult = Get-Content "$outDir/$publisherResourceGroup/resources.generated.json" | ConvertFrom-Json

az cleanroom secretstore add `
    --name publisher-local-store `
    --config $publisherSecretStoreConfig `
    --backingstore-type Local_File `
    --backingstore-path $publisherLocalSecretStore

$schemaFields = "date:date,time:string,author:string,mentions:string"
$formats = @("csv") + $additionalFormats

foreach ($format in $formats) {
    # Create a datastore entry for azure.
    $datastoreName = "publisher-input-$format"
    az cleanroom datastore add `
        --name $datastoreName `
        --config $publisherDatastoreConfig `
        --secretstore publisher-local-store `
        --secretstore-config $publisherSecretStoreConfig `
        --encryption-mode CPK `
        --backingstore-type Azure_BlobStorage `
        --backingstore-id $publisherResult.sa.id `
        --schema-format $format `
        --schema-fields $schemaFields
    # Create a publisher-input-sse datastore in Azure Blob Storage (for S3 consumer flows)
    $sseDatastoreName = "publisher-input-sse-$format"
    az cleanroom datastore add `
        --name $sseDatastoreName `
        --config $publisherDatastoreConfig `
        --encryption-mode SSE `
        --backingstore-type Azure_BlobStorage `
        --backingstore-id $publisherResult.sa.id `
        --schema-format $format `
        --schema-fields $schemaFields

    pwsh $root/test/onebox/multi-party-collab/wait-for-container-access.ps1 `
        --containerName $datastoreName `
        --storageAccountId $publisherResult.sa.id

    pwsh $root/test/onebox/multi-party-collab/wait-for-container-access.ps1 `
        --containerName $sseDatastoreName `
        --storageAccountId $publisherResult.sa.id

    . $PSScriptRoot/get-input-data.ps1

    mkdir -p $PSScriptRoot/publisher/input
    $today = [DateTimeOffset]"2025-09-01"
    Get-PublisherData -dataDir $PSScriptRoot/publisher/input -startDate $today -format $format -schemaFields $schemaFields

    az cleanroom datastore upload `
        --name $datastoreName `
        --config $publisherDatastoreConfig `
        --src "$PSScriptRoot/publisher/input/$format"

    az cleanroom datastore upload `
        --name $sseDatastoreName `
        --config $publisherDatastoreConfig `
        --src "$PSScriptRoot/publisher/input/$format"
}

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
    -storageType adlsgen2 `
    -outDir $outDir `
    -location $location

$consumerResult = Get-Content "$outDir/$consumerResourceGroup/resources.generated.json" | ConvertFrom-Json

az cleanroom secretstore add `
    --name consumer-local-store `
    --config $consumerSecretStoreConfig `
    --backingstore-type Local_File `
    --backingstore-path $consumerLocalSecretStore

# Create a datastore entry.
foreach ($format in $formats) {
    $datastoreName = "consumer-input-$format"
    az cleanroom datastore add `
        --name $datastoreName `
        --config $consumerDatastoreConfig `
        --secretstore consumer-local-store `
        --secretstore-config $consumerSecretStoreConfig `
        --encryption-mode CPK `
        --backingstore-type Azure_BlobStorage_DataLakeGen2 `
        --backingstore-id $consumerResult.sa.id `
        --schema-format $format `
        --schema-fields $schemaFields

    pwsh $root/test/onebox/multi-party-collab/wait-for-container-access.ps1 `
        --containerName $datastoreName `
        --storageAccountId $consumerResult.sa.id

    mkdir -p $PSScriptRoot/consumer/input
    Get-ConsumerData -dataDir $PSScriptRoot/consumer/input -format $format -schemaFields $schemaFields

    az cleanroom datastore upload `
        --name $datastoreName `
        --config $consumerDatastoreConfig `
        --src "$PSScriptRoot/consumer/input/$format"

    pwsh $PSScriptRoot/create-bucket.ps1 `
        -bucketName "$consumerInputS3BucketName-$format"

    pwsh $PSScriptRoot/upload-bucket.ps1 `
        -bucketName "$consumerInputS3BucketName-$format" `
        -src "$PSScriptRoot/consumer/input/$format"

    pwsh $PSScriptRoot/create-bucket.ps1 `
        -bucketName "$consumerOutputS3BucketName-$format"

    $outputDatastoreName = "consumer-output-$format"

    az cleanroom datastore add `
        --name $outputDatastoreName `
        --config $consumerDatastoreConfig `
        --secretstore consumer-local-store `
        --secretstore-config $consumerSecretStoreConfig `
        --encryption-mode CPK `
        --backingstore-type Azure_BlobStorage_DataLakeGen2 `
        --backingstore-id $consumerResult.sa.id `
        --schema-format $format `
        --schema-fields "author:string,Number_Of_Mentions:long,Restricted_Sum:number"
}

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
$version = ""
if (-not $useFrontendService) {
    $version = (az cleanroom governance contract show `
            --id $contractId `
            --query "version" `
            --output tsv `
            --governance-client $ownerClient)
}
else {
    write-Host "Setting analytics contract id in frontend service..."
    curl --fail-with-body -sS -X POST http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/${contractId}?api-version=2026-03-01-preview `
        -H "content-type: application/json" `
        -H "Authorization: Bearer $userToken"

    $returnObject = curl --fail-with-body -sS -X GET http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics?api-version=2026-03-01-preview `
        -H "content-type: application/json" `
        -H "Authorization: Bearer $userToken"
    $contractDetails = $returnObject | ConvertFrom-Json
    $version = $contractDetails.version
    if ($contractDetails.id -ne $contractId) {
        throw "❌ ContractID returned by frontend service ${contractDetails.id} does not match expected contractId $contractId"
    }
}

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

Write-Output "Generating deployment template/policy with $option creation option for analytics workload..."
az cleanroom cluster analytics-workload deployment generate `
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

$proposalId = az cleanroom governance deployment policy show `
    --contract-id $contractId `
    --governance-client $ownerClient `
    --query "proposalIds[0]" `
    --output tsv

# Vote on the proposed cce policy.
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

pwsh $root/samples/workloads/azcli/enable-analytics-workload.ps1 `
    -outDir $clClusterOutDir `
    -securityPolicyCreationOption $option `
    -configEndpointFile $outDir/analytics-workload-config-endpoint.json

Write-Output "Fetching deployment information..."
# Get the analytics endpoint from the deployed cluster.
$analyticsEndpoint = $clCluster.analyticsWorkloadProfile.endpoint
Write-Output "Fetched analytics endpoint: $analyticsEndpoint"

#
# Instead of accessing the service via ${analyticsEndpoint}, we will use kubectl proxy to access it via localhost.
# This is needed as the public IP address for AKS load balancer is not accessible from machines that are not on corpnet.
# https://kubernetes.io/docs/tasks/access-application-cluster/access-cluster-services/#manually-constructing-apiserver-proxy-urls
# For Kind cluster infra also this technique works fine to access the service as it would be having a clusterIP
# and thus not reachable from outside the cluster.
#
$proxy_address = "localhost"
if ($env:CODESPACES -eq "true" -or $env:GITHUB_ACTIONS -eq "true") {
    $proxy_address = "172.17.0.1"
}
else {
    if ($useFrontendService) {
        $proxy_address = "host.docker.internal"
    }
}
$analyticsEndpoint = "http://${proxy_address}:8181/api/v1/namespaces/cleanroom-spark-analytics-agent/services/https:cleanroom-spark-analytics-agent:443/proxy"
Write-Output "Using analytics endpoint: $analyticsEndpoint"
$deploymentInformation = @{
    url = $analyticsEndpoint
} | ConvertTo-Json
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

# Section: Publisher publishes datasets.
$identity = $(az resource show --ids $publisherResult.mi.id --query "properties") | ConvertFrom-Json

# Create identity entry in the configuration.
pwsh $PSScriptRoot/../setup-oidc-issuer.ps1 `
    -resourceGroup $publisherResourceGroup `
    -outDir $outDir `
    -oidcIssuerLevel "user" `
    -governanceClient $publisherProjectName `
    -useFrontendService:$useFrontendService `
    -frontendServiceEndpoint $frontendServiceEndpoint

$publisherIssuerUrl = Get-Content $outDir/$publisherResourceGroup/issuer-url.txt

az cleanroom collaboration context set `
    --collaboration-name $publisherProjectName

az cleanroom collaboration identity add az-federated `
    --identity-name publisher-identity `
    --client-id $identity.clientId `
    --tenant-id $identity.tenantId `
    --token-issuer-url $publisherIssuerUrl `
    --backing-identity cleanroom_cgs_oidc

$publisherDatasets = @{}
foreach ($format in $formats) {
    $publisherInputDatasetName = "publisher-input-$format-$runId"
    $publisherDatasets["${format}_input"] = $publisherInputDatasetName

    if (-not $useFrontendService) {
        az cleanroom collaboration dataset publish `
            --contract-id $contractId `
            --dataset-name $publisherInputDatasetName `
            --datastore-name "publisher-input-$format" `
            --identity-name publisher-identity `
            --dek-secret-store-name publisher-dek-store `
            --kek-secret-store-name publisher-kek-store `
            --policy-access-mode read `
            --policy-allowed-fields "date,author,mentions" `
            --datastore-config-file $publisherDatastoreConfig `
            --secretstore-config-file $publisherSecretStoreConfig
    }
    else {
        # Build dataset specification using build-dataset-spec.py
        $datastoreName = "publisher-input-$format"
        $dekName = "${publisherInputDatasetName}-dek"
        $kekName = "${publisherInputDatasetName}-kek"
        
        $datasetSpecJson = python3 $PSScriptRoot/build-dataset-spec.py `
            --dataset-name $publisherInputDatasetName `
            --datastore-name $datastoreName `
            --datastore-config $publisherDatastoreConfig `
            --access-mode read `
            --allowed-fields "date,author,mentions" `
            --identity-name "publisher-identity" `
            --client-id $identity.clientId `
            --tenant-id $identity.tenantId `
            --issuer-url $publisherIssuerUrl `
            --dek-secret-id $dekName `
            --dek-kv-url $publisherResult.dek.kv.properties.vaultUri `
            --kek-secret-id $kekName `
            --kek-kv-url $publisherResult.kek.kv.properties.vaultUri `
            --kek-maa-url $publisherResult.maa_endpoint
        
        $datasetInputDetails = $datasetSpecJson | ConvertFrom-Json

        # Performing two attempts to ensure that the dataset publishing endpoint in the frontend service is idempotent.
        foreach ($attempt in 1..2) {
            Write-Host "🔄 Attempt #$attempt Publishing dataset $publisherInputDatasetName via frontend service..." -ForegroundColor Yellow

            curl --fail-with-body -sS -X POST http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/datasets/${publisherInputDatasetName}/publish?api-version=2026-03-01-preview `
                -H "content-type: application/json" `
                -H "Authorization: Bearer $publisherUserToken" `
                -d ($datasetInputDetails | ConvertTo-Json -Depth 10 -Compress)
        }
        Write-Host "✅ Successfully Published dataset $publisherInputDatasetName via frontend service for 2 attempts, the endpoint is idempotent." -ForegroundColor Green

        Write-Output "Getting publisher dataset details..."
        $publisherDatasetJson = curl --fail-with-body -sS -X GET `
            "http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/datasets/${publisherInputDatasetName}?api-version=2026-03-01-preview" `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $publisherUserToken"

        $publisherDataset = $publisherDatasetJson | ConvertFrom-Json

        if ($publisherDataset -and $publisherDataset.id -eq $publisherInputDatasetName) {
            if ($publisherDataset.state -eq "Accepted") {
                Write-Host "Successfully verified dataset '$($publisherDataset.id)' is published (State: $($publisherDataset.state))" -ForegroundColor Green
            }
            else {
                throw "Expected dataset '$publisherInputDatasetName' to be in 'Accepted' state, but got state: $($publisherDataset.state)"
            }
        }
        else {
            throw "Failed to verify dataset '$publisherInputDatasetName'"
        }

        # Get the SKR policy for KEK creation.
        Write-Output "Getting SKR policy for KEK creation from frontend service..."
        $skrPolicyJson = curl --fail-with-body -sS -X GET `
            "http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/datasets/${publisherInputDatasetName}/skrpolicy?api-version=2026-03-01-preview" `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $publisherUserToken"
        $skrPolicy = $skrPolicyJson | ConvertFrom-Json
        
        # Convert the SKR policy response to JSON.
        $skrPolicyJsonCompressed = $skrPolicy | ConvertTo-Json -Depth 100 -Compress

        # Create KEK and import to Key Vault.
        Write-Output "Creating KEK '$kekName' and importing to Key Vault..."
        python3 $PSScriptRoot/create-kek.py `
            --kek-name $kekName `
            --key-vault-url $publisherResult.kek.kv.properties.vaultUri `
            --skr-policy-json $skrPolicyJsonCompressed `
            --output-dir $publisherLocalSecretStore

        # Generate wrapped DEK and upload to Key Vault.
        Write-Output "Generating wrapped DEK '$dekName' and uploading to Key Vault..."
        $wrappedDekBase64 = python3 $PSScriptRoot/generate-wrapped-dek.py `
            --dek-file "$publisherLocalSecretStore/$datastoreName.bin" `
            --kek-public-key-file "$publisherLocalSecretStore/$kekName.pem"

        # Upload wrapped DEK to Key Vault.
        az keyvault secret set `
            --vault-name $publisherResult.dek.kv.name `
            --name $dekName `
            --value $wrappedDekBase64

        Write-Host "✅ Successfully created KEK '$kekName' and wrapped DEK '$dekName'" -ForegroundColor Green
    }

    $publisherInputSseDatasetName = "publisher-input-sse-$format-$runId"
    $publisherDatasets["${format}_input_sse"] = $publisherInputSseDatasetName

    if (-not $useFrontendService) {
        az cleanroom collaboration dataset publish `
            --contract-id $contractId `
            --dataset-name $publisherInputSseDatasetName `
            --datastore-name "publisher-input-sse-$format" `
            --identity-name publisher-identity `
            --policy-access-mode read `
            --policy-allowed-fields "date,time,author,mentions" `
            --datastore-config-file $publisherDatastoreConfig
    }
    else {
        # Build dataset specification for SSE dataset (no encryption keys needed)
        $datastoreName = "publisher-input-sse-$format"
        
        $datasetSpecJson = python3 $PSScriptRoot/build-dataset-spec.py `
            --dataset-name $publisherInputSseDatasetName `
            --datastore-name $datastoreName `
            --datastore-config $publisherDatastoreConfig `
            --access-mode read `
            --allowed-fields "date,time,author,mentions" `
            --identity-name "publisher-identity" `
            --client-id $identity.clientId `
            --tenant-id $identity.tenantId `
            --issuer-url $publisherIssuerUrl
        
        $datasetInputDetails = $datasetSpecJson | ConvertFrom-Json
        
        # Test validation: Attempt to publish without identity property for Azure store type- this should fail with 400 response.
        try {
            # Create deep copy of $datasetInputDetails and remove identity property.
            $datasetInputDetailsCopy = $datasetInputDetails | ConvertTo-Json -Depth 10 | ConvertFrom-Json
            $datasetInputDetailsCopy.PSObject.Properties.Remove("identity")

            $headers = @{
                "Authorization" = "Bearer $publisherUserToken";
                "content-type"  = "application/json";
            }
            Invoke-RestMethod -Method Post -Uri "http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/datasets/${publisherInputSseDatasetName}/publish?api-version=2026-03-01-preview" `
                -Headers $headers -Body ($datasetInputDetailsCopy | ConvertTo-Json -Depth 10 -Compress)

            # If we reach here, the request succeeded when it shouldn't have.
            throw "❌ Expected dataset publish attempt without identity to fail with 400 for store type Azure, but it succeeded"
        }
        catch {
            $httpStatusMessage = $_.Exception.Message

            # Check if the error contains 400 status code.
            if ($httpStatusMessage -like "*400*") {
                Write-Host "✅ Expected 400 error occurred when publishing dataset without identity property for store type Azure" -ForegroundColor "Green"
            }
            else {
                # There was a different failure.
                Write-Host "Status: $httpStatusMessage" -ForegroundColor "Red"
                throw "❌ Unexpected error during dataset publish without identity validation for store type Azure: $httpStatusMessage"
            }
        }

        curl --fail-with-body -sS -X POST http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/datasets/${publisherInputSseDatasetName}/publish?api-version=2026-03-01-preview `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $publisherUserToken" `
            -d ($datasetInputDetails | ConvertTo-Json -Depth 10 -Compress)

        Write-Output "Getting publisher dataset details..."
        $publisherDatasetJson = curl --fail-with-body -sS -X GET `
            "http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/datasets/${publisherInputSseDatasetName}?api-version=2026-03-01-preview" `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $publisherUserToken"

        $publisherDataset = $publisherDatasetJson | ConvertFrom-Json

        if ($publisherDataset -and $publisherDataset.id -eq $publisherInputSseDatasetName) {
            if ($publisherDataset.state -eq "Accepted") {
                Write-Host "Successfully verified dataset '$($publisherDataset.id)' is published (State: $($publisherDataset.state))" -ForegroundColor Green
            }
            else {
                throw "Expected dataset '$publisherInputSseDatasetName' to be in 'Accepted' state, but got state: $($publisherDataset.state)"
            }
        }
        else {
            throw "Failed to verify dataset '$publisherInputSseDatasetName'"
        }
    }
}

# Create a datastore entry for the AWS S3 storage to be used as a datasink with CGS secret Id as
# its configuration.
$awsAccessKeyId = az keyvault secret show  --vault-name azcleanroomemukv -n aws-access-key-id --query value -o tsv
$awsSecretAccessKey = az keyvault secret show  --vault-name azcleanroomemukv -n aws-secret-access-key --query value -o tsv
$secretConfig = @{
    awsAccessKeyId     = $awsAccessKeyId
    awsSecretAccessKey = $awsSecretAccessKey
} | ConvertTo-Json | base64 -w 0

$awsConfigCgsSecretName = "consumer-aws-config"
if ($useFrontendService) {
    Write-Host "Setting AWS config CGS secret in frontend service..."
    $awsConfigCgsSecretResponse = curl --fail-with-body -sS -X PUT http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/secrets/${awsConfigCgsSecretName}?api-version=2026-03-01-preview `
        -H "content-type: application/json" `
        -H "Authorization: Bearer $userToken" `
        -d (@{"secretConfig" = $secretConfig } | ConvertTo-Json -Depth 10 -Compress)
    
    $awsConfigCgsSecretId = ($awsConfigCgsSecretResponse | ConvertFrom-Json).secretId
}
else {
    $awsConfigCgsSecretId = (az cleanroom governance contract secret set `
            --secret-name $awsConfigCgsSecretName `
            --value $secretConfig `
            --contract-id $contractId `
            --governance-client $consumerProjectName `
            --query "secretId" `
            --output tsv)
}

$awsUrl = "https://s3.amazonaws.com"
foreach ($format in $formats) {
    az cleanroom datastore add `
        --name consumer-output-s3-$format `
        --config $consumerDatastoreConfig `
        --backingstore-type Aws_S3 `
        --backingstore-id $awsUrl `
        --aws-config-cgs-secret-id $awsConfigCgsSecretId `
        --container-name "$consumerOutputS3BucketName-$format" `
        --schema-format $format `
        --schema-fields "author:string,Number_Of_Mentions:long,Restricted_Sum:number"

    az cleanroom datastore add `
        --name consumer-input-s3-$format `
        --config $consumerDatastoreConfig `
        --backingstore-type Aws_S3 `
        --backingstore-id $awsUrl `
        --aws-config-cgs-secret-id $awsConfigCgsSecretId `
        --container-name "$consumerInputS3BucketName-$format" `
        --schema-format $format `
        --schema-fields $schemaFields
}

# Section: Consumer publishes datasets and queries.
$identity = $(az resource show --ids $consumerResult.mi.id --query "properties") | ConvertFrom-Json

# Create identity entry in the configuration.
pwsh $PSScriptRoot/../setup-oidc-issuer.ps1 `
    -resourceGroup $consumerResourceGroup `
    -outDir $outDir `
    -oidcIssuerLevel "user" `
    -governanceClient $consumerProjectName `
    -useFrontendService:$useFrontendService `
    -frontendServiceEndpoint $frontendServiceEndpoint

$consumerIssuerUrl = Get-Content $outDir/$consumerResourceGroup/issuer-url.txt

az cleanroom collaboration context set `
    --collaboration-name $consumerProjectName

az cleanroom collaboration identity add az-federated `
    --identity-name consumer-identity `
    --client-id $identity.clientId `
    --tenant-id $identity.tenantId `
    --token-issuer-url $consumerIssuerUrl `
    --backing-identity cleanroom_cgs_oidc

$consumerDatasets = @{}
foreach ($format in $formats) {
    $consumerInputDatasetName = "consumer-input-$format-$runId"
    $consumerDatasets["${format}_input"] = $consumerInputDatasetName

    if (-not $useFrontendService) {
        az cleanroom collaboration dataset publish `
            --contract-id $contractId `
            --dataset-name $consumerInputDatasetName `
            --datastore-name "consumer-input-$format" `
            --identity-name consumer-identity `
            --dek-secret-store-name consumer-dek-store `
            --kek-secret-store-name consumer-kek-store `
            --policy-access-mode read `
            --policy-allowed-fields "date,author,mentions" `
            --datastore-config-file $consumerDatastoreConfig `
            --secretstore-config-file $consumerSecretStoreConfig
    }
    else {
        # Build dataset specification for consumer input dataset
        $datastoreName = "consumer-input-$format"
        $dekName = "${consumerInputDatasetName}-dek"
        $kekName = "${consumerInputDatasetName}-kek"
        
        $datasetSpecJson = python3 $PSScriptRoot/build-dataset-spec.py `
            --dataset-name $consumerInputDatasetName `
            --datastore-name $datastoreName `
            --datastore-config $consumerDatastoreConfig `
            --access-mode read `
            --allowed-fields "date,author,mentions" `
            --identity-name "consumer-identity" `
            --client-id $identity.clientId `
            --tenant-id $identity.tenantId `
            --issuer-url $consumerIssuerUrl `
            --dek-secret-id $dekName `
            --dek-kv-url $consumerResult.dek.kv.properties.vaultUri `
            --kek-secret-id $kekName `
            --kek-kv-url $consumerResult.kek.kv.properties.vaultUri `
            --kek-maa-url $consumerResult.maa_endpoint
        
        $datasetInputDetails = $datasetSpecJson | ConvertFrom-Json

        curl --fail-with-body -sS -X POST http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/datasets/${consumerInputDatasetName}/publish?api-version=2026-03-01-preview `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $userToken" `
            -d ($datasetInputDetails | ConvertTo-Json -Depth 10 -Compress)

        Write-Output "Getting ${consumerInputDatasetName} dataset details..."
        $consumerDatasetJson = curl --fail-with-body -sS -X GET `
            "http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/datasets/${consumerInputDatasetName}?api-version=2026-03-01-preview" `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $userToken"

        $consumerDataset = $consumerDatasetJson | ConvertFrom-Json

        if ($consumerDataset -and $consumerDataset.id -eq $consumerInputDatasetName) {
            if ($consumerDataset.state -eq "Accepted") {
                Write-Host "Successfully verified dataset '$($consumerDataset.id)' is published (State: $($consumerDataset.state))" -ForegroundColor Green
            }
            else {
                throw "Expected dataset '$consumerInputDatasetName' to be in 'Accepted' state, but got state: $($consumerDataset.state)"
            }
        }
        else {
            throw "Failed to verify dataset '$consumerInputDatasetName'"
        }

        # Get the SKR policy for KEK creation.
        Write-Output "Getting SKR policy for KEK creation from frontend service for consumer input..."
        $skrPolicyJson = curl --fail-with-body -sS -X GET `
            "http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/datasets/${consumerInputDatasetName}/skrpolicy?api-version=2026-03-01-preview" `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $userToken"
        $skrPolicy = $skrPolicyJson | ConvertFrom-Json
        
        # Convert the SKR policy response to JSON.
        $skrPolicyJsonCompressed = $skrPolicy | ConvertTo-Json -Depth 100 -Compress

        # Create KEK and import to Key Vault.
        Write-Output "Creating KEK '$kekName' and importing to Key Vault..."
        python3 $PSScriptRoot/create-kek.py `
            --kek-name $kekName `
            --key-vault-url $consumerResult.kek.kv.properties.vaultUri `
            --skr-policy-json $skrPolicyJsonCompressed `
            --output-dir $consumerLocalSecretStore

        # Generate wrapped DEK and upload to Key Vault.
        Write-Output "Generating wrapped DEK '$dekName' and uploading to Key Vault..."
        $wrappedDekBase64 = python3 $PSScriptRoot/generate-wrapped-dek.py `
            --dek-file "$consumerLocalSecretStore/$datastoreName.bin" `
            --kek-public-key-file "$consumerLocalSecretStore/$kekName.pem"

        # Upload wrapped DEK to Key Vault.
        az keyvault secret set `
            --vault-name $consumerResult.dek.kv.name `
            --name $dekName `
            --value $wrappedDekBase64

        Write-Host "✅ Successfully created KEK '$kekName' and wrapped DEK '$dekName' for consumer input" -ForegroundColor Green
    }

    $consumerOutputDatasetName = "consumer-output-$format-$runId"
    $consumerDatasets["${format}_output"] = $consumerOutputDatasetName

    if (-not $useFrontendService) {
        az cleanroom collaboration dataset publish `
            --contract-id $contractId `
            --dataset-name $consumerOutputDatasetName `
            --datastore-name "consumer-output-$format" `
            --identity-name consumer-identity `
            --dek-secret-store-name consumer-dek-store `
            --kek-secret-store-name consumer-kek-store `
            --policy-access-mode write `
            --policy-allowed-fields "author,Number_Of_Mentions" `
            --datastore-config-file $consumerDatastoreConfig `
            --secretstore-config-file $consumerSecretStoreConfig
    }
    else {
        # Build dataset specification for consumer output dataset
        $datastoreName = "consumer-output-$format"
        $dekName = "${consumerOutputDatasetName}-dek"
        $kekName = "${consumerOutputDatasetName}-kek"
        
        $datasetSpecJson = python3 $PSScriptRoot/build-dataset-spec.py `
            --dataset-name $consumerOutputDatasetName `
            --datastore-name $datastoreName `
            --datastore-config $consumerDatastoreConfig `
            --access-mode write `
            --allowed-fields "author,Number_Of_Mentions" `
            --identity-name "consumer-identity" `
            --client-id $identity.clientId `
            --tenant-id $identity.tenantId `
            --issuer-url $consumerIssuerUrl `
            --dek-secret-id $dekName `
            --dek-kv-url $consumerResult.dek.kv.properties.vaultUri `
            --kek-secret-id $kekName `
            --kek-kv-url $consumerResult.kek.kv.properties.vaultUri `
            --kek-maa-url $consumerResult.maa_endpoint
        
        $datasetInputDetails = $datasetSpecJson | ConvertFrom-Json

        curl --fail-with-body -sS -X POST http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/datasets/${consumerOutputDatasetName}/publish?api-version=2026-03-01-preview `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $userToken" `
            -d ($datasetInputDetails | ConvertTo-Json -Depth 10 -Compress)

        Write-Output "Getting ${consumerOutputDatasetName} dataset details..."
        $consumerDatasetJson = curl --fail-with-body -sS -X GET `
            "http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/datasets/${consumerOutputDatasetName}?api-version=2026-03-01-preview" `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $userToken"

        $consumerDataset = $consumerDatasetJson | ConvertFrom-Json

        if ($consumerDataset -and $consumerDataset.id -eq $consumerOutputDatasetName) {
            if ($consumerDataset.state -eq "Accepted") {
                Write-Host "Successfully verified dataset '$($consumerDataset.id)' is published (State: $($consumerDataset.state))" -ForegroundColor Green
            }
            else {
                throw "Expected dataset '$consumerOutputDatasetName' to be in 'Accepted' state, but got state: $($consumerDataset.state)"
            }
        }
        else {
            throw "Failed to verify dataset '$consumerOutputDatasetName'"
        }

        # Get the SKR policy for KEK creation.
        Write-Output "Getting SKR policy for KEK creation from frontend service for consumer output..."
        $skrPolicyJson = curl --fail-with-body -sS -X GET `
            "http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/datasets/${consumerOutputDatasetName}/skrpolicy?api-version=2026-03-01-preview" `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $userToken"
        $skrPolicy = $skrPolicyJson | ConvertFrom-Json
        
        # Convert the SKR policy response to JSON.
        $skrPolicyJsonCompressed = $skrPolicy | ConvertTo-Json -Depth 100 -Compress

        # Create KEK and import to Key Vault.
        Write-Output "Creating KEK '$kekName' and importing to Key Vault..."
        python3 $PSScriptRoot/create-kek.py `
            --kek-name $kekName `
            --key-vault-url $consumerResult.kek.kv.properties.vaultUri `
            --skr-policy-json $skrPolicyJsonCompressed `
            --output-dir $consumerLocalSecretStore

        # Generate wrapped DEK and upload to Key Vault.
        Write-Output "Generating wrapped DEK '$dekName' and uploading to Key Vault..."
        $wrappedDekBase64 = python3 $PSScriptRoot/generate-wrapped-dek.py `
            --dek-file "$consumerLocalSecretStore/$datastoreName.bin" `
            --kek-public-key-file "$consumerLocalSecretStore/$kekName.pem"

        # Upload wrapped DEK to Key Vault.
        az keyvault secret set `
            --vault-name $consumerResult.dek.kv.name `
            --name $dekName `
            --value $wrappedDekBase64

        Write-Host "✅ Successfully created KEK '$kekName' and wrapped DEK '$dekName' for consumer output" -ForegroundColor Green
    }

    $consumerInputS3DatasetName = "consumer-input-s3-$format-$runId"
    $consumerDatasets["${format}_input_s3"] = $consumerInputS3DatasetName

    if (-not $useFrontendService) {
        az cleanroom collaboration dataset publish `
            --contract-id $contractId `
            --dataset-name $consumerInputS3DatasetName `
            --datastore-name "consumer-input-s3-$format" `
            --identity-name cleanroom_cgs_oidc `
            --policy-access-mode read `
            --policy-allowed-fields "date,time,author,mentions" `
            --datastore-config-file $consumerDatastoreConfig
    }
    else {
        # Build dataset specification for S3 input dataset (no identity needed for S3)
        $datastoreName = "consumer-input-s3-$format"
        
        $datasetSpecJson = python3 $PSScriptRoot/build-dataset-spec.py `
            --dataset-name $consumerInputS3DatasetName `
            --datastore-name $datastoreName `
            --datastore-config $consumerDatastoreConfig `
            --access-mode read `
            --allowed-fields "date,time,author,mentions" `
        
        $datasetInputDetails = $datasetSpecJson | ConvertFrom-Json

        curl --fail-with-body -sS -X POST http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/datasets/${consumerInputS3DatasetName}/publish?api-version=2026-03-01-preview `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $userToken" `
            -d ($datasetInputDetails | ConvertTo-Json -Depth 10 -Compress)

        Write-Output "Getting ${consumerInputS3DatasetName} dataset details..."
        $consumerDatasetJson = curl --fail-with-body -sS -X GET `
            "http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/datasets/${consumerInputS3DatasetName}?api-version=2026-03-01-preview" `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $userToken"

        $consumerDataset = $consumerDatasetJson | ConvertFrom-Json

        if ($consumerDataset -and $consumerDataset.id -eq $consumerInputS3DatasetName) {
            if ($consumerDataset.state -eq "Accepted") {
                Write-Host "Successfully verified dataset '$($consumerDataset.id)' is published (State: $($consumerDataset.state))" -ForegroundColor Green
            }
            else {
                throw "Expected dataset '$consumerInputS3DatasetName' to be in 'Accepted' state, but got state: $($consumerDataset.state)"
            }
        }
        else {
            throw "Failed to verify dataset '$consumerInputS3DatasetName'"
        }
    }

    $consumerOutputS3DatasetName = "consumer-output-s3-$format-$runId"
    $consumerDatasets["${format}_output_s3"] = $consumerOutputS3DatasetName

    if (-not $useFrontendService) {
        az cleanroom collaboration dataset publish `
            --contract-id $contractId `
            --dataset-name $consumerOutputS3DatasetName `
            --datastore-name "consumer-output-s3-$format" `
            --identity-name cleanroom_cgs_oidc `
            --policy-access-mode write `
            --policy-allowed-fields "author,Number_Of_Mentions" `
            --datastore-config-file $consumerDatastoreConfig
    }
    else {
        # Build dataset specification for S3 output dataset (no identity needed for S3)
        $datastoreName = "consumer-output-s3-$format"
        
        $datasetSpecJson = python3 $PSScriptRoot/build-dataset-spec.py `
            --dataset-name $consumerOutputS3DatasetName `
            --datastore-name $datastoreName `
            --datastore-config $consumerDatastoreConfig `
            --access-mode write `
            --allowed-fields "author,Number_Of_Mentions" `
        
        $datasetInputDetails = $datasetSpecJson | ConvertFrom-Json

        curl --fail-with-body -sS -X POST http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/datasets/${consumerOutputS3DatasetName}/publish?api-version=2026-03-01-preview `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $userToken" `
            -d ($datasetInputDetails | ConvertTo-Json -Depth 10 -Compress)

        Write-Output "Getting ${consumerOutputS3DatasetName} dataset details..."
        $consumerDatasetJson = curl --fail-with-body -sS -X GET `
            "http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/datasets/${consumerOutputS3DatasetName}?api-version=2026-03-01-preview" `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $userToken"

        $consumerDataset = $consumerDatasetJson | ConvertFrom-Json

        if ($consumerDataset -and $consumerDataset.id -eq $consumerOutputS3DatasetName) {
            if ($consumerDataset.state -eq "Accepted") {
                Write-Host "Successfully verified dataset '$($consumerDataset.id)' is published (State: $($consumerDataset.state))" -ForegroundColor Green
            }
            else {
                throw "Expected dataset '$consumerOutputS3DatasetName' to be in 'Accepted' state, but got state: $($consumerDataset.state)"
            }
        }
        else {
            throw "Failed to verify dataset '$consumerOutputS3DatasetName'"
        }
    }
}

# Define segment data for a query
$segment1Data = Get-Content "$PSScriptRoot/consumer/query/segment1.txt"
$segment2Data = Get-Content "$PSScriptRoot/consumer/query/segment2.txt"
$segment3Data = Get-Content "$PSScriptRoot/consumer/query/segment3.txt"

$queryDocuments = @{}

foreach ($format in $formats) {
    $queryDocumentId = "consumer-query-$format-$runId"
    $consumerQueryConfigFile = "$outDir/$queryDocumentId.yaml"

    az cleanroom collaboration spark-sql query segment add `
        --config-file $consumerQueryConfigFile `
        --query-content $segment1Data `
        --execution-sequence 1

    az cleanroom collaboration spark-sql query segment add `
        --config-file $consumerQueryConfigFile `
        --query-content $segment2Data `
        --execution-sequence 1

    az cleanroom collaboration spark-sql query segment add `
        --config-file $consumerQueryConfigFile `
        --query-content $segment3Data `
        --execution-sequence 2

    az cleanroom collaboration context set `
        --collaboration-name $consumerProjectName
    if (-not $useFrontendService) {
        az cleanroom collaboration spark-sql publish `
            --application-name $queryDocumentId `
            --application-query $consumerQueryConfigFile `
            --application-input-dataset "publisher_data:$($publisherDatasets["${format}_input"]), consumer_data:$($consumerDatasets["${format}_input"])" `
            --application-output-dataset "output:$($consumerDatasets["${format}_output"])" `
            --contract-id $contractId
    }
    else {
        $queryInputDetails = az cleanroom collaboration spark-sql publish `
            --application-name $queryDocumentId `
            --application-query $consumerQueryConfigFile `
            --application-input-dataset "publisher_data:$($publisherDatasets["${format}_input"]), consumer_data:$($consumerDatasets["${format}_input"])" `
            --application-output-dataset "output:$($consumerDatasets["${format}_output"])" `
            --contract-id $contractId `
            --prepare-only | ConvertFrom-Json

        # Performing two attempts to ensure that the query publishing endpoint in the frontend service is idempotent.
        foreach ($attempt in 1..2) {
            Write-Host "🔄 Attempt #$attempt Publishing query $queryDocumentId via frontend service..." -ForegroundColor Yellow

            curl --fail-with-body -sS -X POST http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/queries/${queryDocumentId}/publish?api-version=2026-03-01-preview `
                -H "content-type: application/json" `
                -H "Authorization: Bearer $userToken" `
                -d ($queryInputDetails | ConvertTo-Json -Depth 10 -Compress)
        }
        Write-Host "✅ Successfully Published dataset $publisherInputDatasetName via frontend service for 2 attempts, the endpoint is idempotent." -ForegroundColor Green

        Write-Output "Getting query details for ${queryDocumentId} before voting..."
        $queryDetailsJson = curl --fail-with-body -sS -X GET `
            "http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/queries/${queryDocumentId}?api-version=2026-03-01-preview" `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $userToken"

        $queryDetails = $queryDetailsJson | ConvertFrom-Json
        Write-Output "Query details for ${queryDocumentId} before voting: State=$($queryDetails.state)"

        if ($queryDetails.state -ne "Proposed") {
            throw "Query '$queryDocumentId' is not in Proposed state before voting. Current state: $($queryDetails.state)"
        }
    }

    Write-Output "Accepting the user document proposal for query as consumer"
    if (-not $useFrontendService) {
        $proposalId = (az cleanroom governance user-document show `
                --id $queryDocumentId `
                --governance-client $consumerProjectName `
                --query "proposalId" `
                --output tsv)
        az cleanroom governance user-document vote `
            --id $queryDocumentId `
            --proposal-id $proposalId `
            --action accept `
            --governance-client $consumerProjectName | jq
    }
    else {
        Write-Output "Getting query details to retrieve proposal ID..."
        $queryDetailsJson = curl --fail-with-body -sS -X GET `
            "http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/queries/${queryDocumentId}?api-version=2026-03-01-preview" `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $userToken"

        $queryDetails = $queryDetailsJson | ConvertFrom-Json
        $proposalId = $queryDetails.proposalId
        $frontendJsonPayload = @{
            "proposalId" = $proposalId
            "voteAction" = "accept"
        } | ConvertTo-Json -Depth 10

        curl --fail-with-body -sS -X POST http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/queries/${queryDocumentId}/vote?api-version=2026-03-01-preview `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $userToken" `
            -d $frontendJsonPayload

        # Attempt to vote accept again - this should fail with BallotAlreadySubmitted response since we are doing it again.
        # This is to verify that the correct error from CGS service is passed on from the frontend service.
        try {
            $headers = @{
                "Authorization" = "Bearer $userToken";
                "content-type"  = "application/json";
            }
            Invoke-RestMethod -Method Post -Uri "http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/queries/${queryDocumentId}/vote?api-version=2026-03-01-preview" `
                -Headers $headers -Body $frontendJsonPayload

            # If we reach here, the curl succeeded when it shouldn't have
            throw "❌ Expected second vote accept attempt to fail with BallotAlreadySubmitted, but it succeeded"
        }
        catch {
            $httpStatusMessage = $_.Exception.Message
            $responseBody = $_.ErrorDetails.Message

            $expectedError = "The ballot has already been submitted."

            # Check if the error contains the expected phrases and status code
            if ($responseBody -like "*$expectedError*" -and
                $responseBody -like "*BallotAlreadySubmitted*" -and
                $httpStatusMessage -like "*409*") {
                Write-Host "✅ Expected error occurred: $expectedError (409)" -ForegroundColor "Green"
            }
            else {
                # There was a different failure
                Write-Host "Status: $httpStatusMessage" -ForegroundColor "Red"
                Write-Host "Body: $responseBody" -ForegroundColor "Red"
                throw "❌ Unexpected error during second vote accept attempt: $errorMessage"
            }
        }
    }

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

    $queryDocuments["${format}_standard"] = $queryDocumentId

    if ($useFrontendService) {
        Write-Output "Getting query details for ${queryDocumentId} after voting..."
        $queryDetailsJson = curl --fail-with-body -sS -X GET `
            "http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/queries/${queryDocumentId}?api-version=2026-03-01-preview" `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $userToken"

        $queryDetails = $queryDetailsJson | ConvertFrom-Json

        if ($queryDetails -and $queryDetails.state -eq "Accepted") {
            Write-Host "Successfully verified query '$queryDocumentId' is accepted (State: $($queryDetails.state))" -ForegroundColor Green
        }
        else {
            throw "Expected query '$queryDocumentId' to be in 'Accepted' state, but got state: $($queryDetails.state)"
        }
    }

    # S3 query for this format
    Write-Output "Creating query document with S3 datasource and datasink for format: $format"
    $s3QueryDocumentId = "consumer-query-s3-$format-$runId"
    $consumerQueryConfigFile = "$outDir/$s3QueryDocumentId.yaml"

    az cleanroom collaboration spark-sql query segment add `
        --config-file $consumerQueryConfigFile `
        --query-content $segment1Data `
        --execution-sequence 1

    az cleanroom collaboration spark-sql query segment add `
        --config-file $consumerQueryConfigFile `
        --query-content $segment2Data `
        --execution-sequence 1

    az cleanroom collaboration spark-sql query segment add `
        --config-file $consumerQueryConfigFile `
        --query-content $segment3Data `
        --execution-sequence 2

    az cleanroom collaboration context set `
        --collaboration-name $consumerProjectName
    if (-not $useFrontendService) {
        az cleanroom collaboration spark-sql publish `
            --application-name $s3QueryDocumentId `
            --application-query $consumerQueryConfigFile `
            --application-input-dataset "publisher_data:$($publisherDatasets["${format}_input_sse"]), consumer_data:$($consumerDatasets["${format}_input_s3"])" `
            --application-output-dataset "output:$($consumerDatasets["${format}_output_s3"])" `
            --contract-id $contractId
    }
    else {
        $queryInputDetails = az cleanroom collaboration spark-sql publish `
            --application-name $s3QueryDocumentId `
            --application-query $consumerQueryConfigFile `
            --application-input-dataset "publisher_data:$($publisherDatasets["${format}_input_sse"]), consumer_data:$($consumerDatasets["${format}_input_s3"])" `
            --application-output-dataset "output:$($consumerDatasets["${format}_output_s3"])" `
            --contract-id $contractId `
            --prepare-only | ConvertFrom-Json

        curl --fail-with-body -sS -X POST http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/queries/${s3QueryDocumentId}/publish?api-version=2026-03-01-preview `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $userToken" `
            -d ($queryInputDetails | ConvertTo-Json -Depth 10 -Compress)

        Write-Output "Getting query details for ${s3QueryDocumentId} before voting..."
        $queryDetailsJson = curl --fail-with-body -sS -X GET `
            "http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/queries/${s3QueryDocumentId}?api-version=2026-03-01-preview" `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $userToken"

        $queryDetails = $queryDetailsJson | ConvertFrom-Json
        Write-Output "Query details for ${s3QueryDocumentId} before voting: State=$($queryDetails.state)"

        if ($queryDetails.state -ne "Proposed") {
            throw "Query '$s3QueryDocumentId' is not in Proposed state before voting. Current state: $($queryDetails.state)"
        }
    }

    Write-Output "Accepting the user document proposal for query as consumer"
    $proposalId = (az cleanroom governance user-document show `
            --id $s3QueryDocumentId `
            --governance-client $consumerProjectName `
            --query "proposalId" `
            --output tsv)
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

    $queryDocuments["s3_${format}_standard"] = $s3QueryDocumentId

    if ($useFrontendService) {
        Write-Output "Getting query details for ${s3QueryDocumentId} after voting..."
        $queryDetailsJson = curl --fail-with-body -sS -X GET `
            "http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/queries/${s3QueryDocumentId}?api-version=2026-03-01-preview" `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $userToken"

        $queryDetails = $queryDetailsJson | ConvertFrom-Json
        if ($queryDetails -and $queryDetails.state -eq "Accepted") {
            Write-Host "Successfully verified query '$s3QueryDocumentId' is accepted (State: $($queryDetails.state))" -ForegroundColor Green
        }
        else {
            throw "Expected query '$s3QueryDocumentId' to be in 'Accepted' state, but got state: $($queryDetails.state)"
        }
    }
}

$lowkminQueryDocumentId = "consumer-query-lowkmin-csv-$runId"
$consumerQueryConfigFile = "$outDir/$lowkminQueryDocumentId.yaml"

az cleanroom collaboration spark-sql query segment add `
    --config-file $consumerQueryConfigFile `
    --query-content $segment1Data `
    --execution-sequence 1

az cleanroom collaboration spark-sql query segment add `
    --config-file $consumerQueryConfigFile `
    --query-content $segment2Data `
    --execution-sequence 1

az cleanroom collaboration spark-sql query segment add `
    --config-file $consumerQueryConfigFile `
    --query-content $segment3Data `
    --execution-sequence 2 `
    --pre-conditions "publisher_view:100,consumer_view:10000" `
    --post-filters "Number_Of_Mentions:2"

az cleanroom collaboration context set `
    --collaboration-name $consumerProjectName
if (-not $useFrontendService) {
    az cleanroom collaboration spark-sql publish `
        --application-name $lowkminQueryDocumentId `
        --application-query $consumerQueryConfigFile `
        --application-input-dataset "publisher_data:$($publisherDatasets["csv_input"]), consumer_data:$($consumerDatasets["csv_input"])" `
        --application-output-dataset "output:$($consumerDatasets["csv_output_s3"])" `
        --contract-id $contractId
}
else {
    $queryInputDetails = az cleanroom collaboration spark-sql publish `
        --application-name $lowkminQueryDocumentId `
        --application-query $consumerQueryConfigFile `
        --application-input-dataset "publisher_data:$($publisherDatasets["csv_input"]), consumer_data:$($consumerDatasets["csv_input"])" `
        --application-output-dataset "output:$($consumerDatasets["csv_output_s3"])" `
        --contract-id $contractId `
        --prepare-only | ConvertFrom-Json

    curl --fail-with-body -sS -X POST http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/queries/${lowkminQueryDocumentId}/publish?api-version=2026-03-01-preview `
        -H "content-type: application/json" `
        -H "Authorization: Bearer $userToken" `
        -d ($queryInputDetails | ConvertTo-Json -Depth 10 -Compress)

    Write-Output "Getting query details for ${lowkminQueryDocumentId} before voting..."
    $queryDetailsJson = curl --fail-with-body -sS -X GET `
        "http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/queries/${lowkminQueryDocumentId}?api-version=2026-03-01-preview" `
        -H "content-type: application/json" `
        -H "Authorization: Bearer $userToken"

    $queryDetails = $queryDetailsJson | ConvertFrom-Json
    Write-Output "Query details for ${lowkminQueryDocumentId} before voting: State=$($queryDetails.state)"

    if ($queryDetails.state -ne "Proposed") {
        throw "Query '$lowkminQueryDocumentId' is not in Proposed state before voting. Current state: $($queryDetails.state)"
    }
}

Write-Output "Accepting the user document proposal for query as consumer"
$proposalId = (az cleanroom governance user-document show `
        --id $lowkminQueryDocumentId `
        --governance-client $consumerProjectName `
        --query "proposalId" `
        --output tsv)
az cleanroom governance user-document vote `
    --id $lowkminQueryDocumentId `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $consumerProjectName | jq

Write-Output "Accepting the user document proposal for query as publisher"
$proposalId = (az cleanroom governance user-document show `
        --id $lowkminQueryDocumentId `
        --governance-client $publisherProjectName `
        --query "proposalId" `
        --output tsv)
az cleanroom governance user-document vote `
    --id $lowkminQueryDocumentId `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $publisherProjectName | jq

$queryDocuments["csv_lowkmin"] = $lowkminQueryDocumentId

if ($useFrontendService) {
    Write-Output "Getting query details for ${lowkminQueryDocumentId} after voting..."
    $queryDetailsJson = curl --fail-with-body -sS -X GET `
        "http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/queries/${lowkminQueryDocumentId}?api-version=2026-03-01-preview" `
        -H "content-type: application/json" `
        -H "Authorization: Bearer $userToken"

    $queryDetails = $queryDetailsJson | ConvertFrom-Json

    if ($queryDetails -and $queryDetails.state -eq "Accepted") {
        Write-Host "Successfully verified query '$lowkminQueryDocumentId' is accepted (State: $($queryDetails.state))" -ForegroundColor Green
    }
    else {
        throw "Expected query '$lowkminQueryDocumentId' to be in 'Accepted' state, but got state: $($queryDetails.state)"
    }
}

Write-Output "Creating query document with S3 datasource and datasink and Kmin configured for format: csv"
$s3KminQueryDocumentId = "consumer-query-s3-kmin-csv-$runId"
$consumerQueryConfigFile = "$outDir/$s3KminQueryDocumentId.yaml"

az cleanroom collaboration spark-sql query segment add `
    --config-file $consumerQueryConfigFile `
    --query-content $segment1Data `
    --execution-sequence 1

az cleanroom collaboration spark-sql query segment add `
    --config-file $consumerQueryConfigFile `
    --query-content $segment2Data `
    --execution-sequence 1

az cleanroom collaboration spark-sql query segment add `
    --config-file $consumerQueryConfigFile `
    --query-content $segment3Data `
    --execution-sequence 2 `
    --pre-conditions "publisher_view:100,consumer_view:100" `
    --post-filters "Number_Of_Mentions:2"

az cleanroom collaboration context set `
    --collaboration-name $consumerProjectName
if (-not $useFrontendService) {
    az cleanroom collaboration spark-sql publish `
        --application-name $s3KminQueryDocumentId `
        --application-query $consumerQueryConfigFile `
        --application-input-dataset "publisher_data:$($publisherDatasets["csv_input_sse"]), consumer_data:$($consumerDatasets["csv_input_s3"])" `
        --application-output-dataset "output:$($consumerDatasets["csv_output_s3"])" `
        --contract-id $contractId
}
else {
    $queryInputDetails = az cleanroom collaboration spark-sql publish `
        --application-name $s3KminQueryDocumentId `
        --application-query $consumerQueryConfigFile `
        --application-input-dataset "publisher_data:$($publisherDatasets["csv_input_sse"]), consumer_data:$($consumerDatasets["csv_input_s3"])" `
        --application-output-dataset "output:$($consumerDatasets["csv_output_s3"])" `
        --contract-id $contractId `
        --prepare-only | ConvertFrom-Json

    curl --fail-with-body -sS -X POST http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/queries/${s3KminQueryDocumentId}/publish?api-version=2026-03-01-preview `
        -H "content-type: application/json" `
        -H "Authorization: Bearer $userToken" `
        -d ($queryInputDetails | ConvertTo-Json -Depth 10 -Compress)

    Write-Output "Getting query details for ${s3KminQueryDocumentId} before voting..."
    $queryDetailsJson = curl --fail-with-body -sS -X GET `
        "http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/queries/${s3KminQueryDocumentId}?api-version=2026-03-01-preview" `
        -H "content-type: application/json" `
        -H "Authorization: Bearer $userToken"

    $queryDetails = $queryDetailsJson | ConvertFrom-Json
    Write-Output "Query details for ${s3KminQueryDocumentId} before voting: State=$($queryDetails.state)"

    if ($queryDetails.state -ne "Proposed") {
        throw "Query '$s3KminQueryDocumentId' is not in Proposed state before voting. Current state: $($queryDetails.state)"
    }
}

Write-Output "Accepting the user document proposal for query as consumer"
$proposalId = (az cleanroom governance user-document show `
        --id $s3KminQueryDocumentId `
        --governance-client $consumerProjectName `
        --query "proposalId" `
        --output tsv)
az cleanroom governance user-document vote `
    --id $s3KminQueryDocumentId `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $consumerProjectName | jq

Write-Output "Accepting the user document proposal for query as publisher"
$proposalId = (az cleanroom governance user-document show `
        --id $s3KminQueryDocumentId `
        --governance-client $publisherProjectName `
        --query "proposalId" `
        --output tsv)
az cleanroom governance user-document vote `
    --id $s3KminQueryDocumentId `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $publisherProjectName | jq

$queryDocuments["s3_csv_kmin"] = $s3KminQueryDocumentId

if ($useFrontendService) {
    Write-Output "Getting query details for ${s3KminQueryDocumentId} after voting..."
    $queryDetailsJson = curl --fail-with-body -sS -X GET `
        "http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/queries/${s3KminQueryDocumentId}?api-version=2026-03-01-preview" `
        -H "content-type: application/json" `
        -H "Authorization: Bearer $userToken"

    $queryDetails = $queryDetailsJson | ConvertFrom-Json
    if ($queryDetails -and $queryDetails.state -eq "Accepted") {
        Write-Host "Successfully verified query '$s3KminQueryDocumentId' is accepted (State: $($queryDetails.state))" -ForegroundColor Green
    }
    else {
        throw "Expected query '$s3KminQueryDocumentId' to be in 'Accepted' state, but got state: $($queryDetails.state)"
    }
}

Write-Output "Submitting a malicious query for format: csv and approving it..."
$maliciousQuery = Get-Content "$PSScriptRoot/consumer/query/malicious_query.txt"
$maliciousQueryDocumentId = "consumer-malicious-query-csv-$runId"

$queryObject = @{
    segments = @(
        @{
            executionSequence = 1
            data              = $maliciousQuery
            preConditions     = $null
            postFilters       = $null
        }
    )
}
$queryJson = $queryObject | ConvertTo-Json -Depth 100 -Compress
$maliciousQueryEncoded = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($queryJson), 'None')
@"
{
"name": "$maliciousQueryDocumentId",
"application": {
"applicationType": "Spark-SQL",
"inputDataset": [
{
    "specification": "$($publisherDatasets["csv_input"])",
    "view": "publisher_data"
}
],
"outputDataset": {
    "specification": "$($consumerDatasets["csv_output"])",
    "view": "output"
},
"query": "$maliciousQueryEncoded"
}
}
"@ > $outDir/consumer-malicious-query-csv.json

, @(
    @{
        "id"   = "$consumerUserId"
        "type" = "user"
    }
) | ConvertTo-Json -Depth 100 | Out-File $outDir/consumer-malicious-query-csv-approvers.json
$queryDocument = Get-Content -Raw $outDir/consumer-malicious-query-csv.json

Write-Output "Adding user document for malicious query with approvers as $(Get-Content -Raw $outDir/consumer-malicious-query-csv-approvers.json)..."
az cleanroom governance user-document create `
    --data $queryDocument `
    --id $maliciousQueryDocumentId `
    --approvers $outDir/consumer-malicious-query-csv-approvers.json `
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

if ($useFrontendService) {
    Write-Output "Getting query details for ${maliciousQueryDocumentId} before voting..."
    $queryDetailsJson = curl --fail-with-body -sS -X GET `
        "http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/queries/${maliciousQueryDocumentId}?api-version=2026-03-01-preview" `
        -H "content-type: application/json" `
        -H "Authorization: Bearer $userToken"

    $queryDetails = $queryDetailsJson | ConvertFrom-Json
    Write-Output "Query details for ${maliciousQueryDocumentId} before voting: State=$($queryDetails.state)"

    if ($queryDetails.state -ne "Proposed") {
        throw "Query '$maliciousQueryDocumentId' is not in Proposed state before voting. Current state: $($queryDetails.state)"
    }
}

Write-Output "Accepting the user document proposal for malicious query as consumer"
az cleanroom governance user-document vote `
    --id $maliciousQueryDocumentId `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $consumerProjectName | jq

$queryDocuments["csv_malicious"] = $maliciousQueryDocumentId

if ($useFrontendService) {
    Write-Output "Getting query details for ${maliciousQueryDocumentId} after voting..."
    $queryDetailsJson = curl --fail-with-body -sS -X GET `
        "http://${frontendServiceEndpoint}/collaborations/${consumerProjectName}/analytics/queries/${maliciousQueryDocumentId}?api-version=2026-03-01-preview" `
        -H "content-type: application/json" `
        -H "Authorization: Bearer $userToken"

    $queryDetails = $queryDetailsJson | ConvertFrom-Json
    if ($queryDetails -and $queryDetails.state -eq "Accepted") {
        Write-Host "Successfully verified query '$maliciousQueryDocumentId' is accepted (State: $($queryDetails.state))" -ForegroundColor Green
    }
    else {
        throw "Expected query '$maliciousQueryDocumentId' to be in 'Accepted' state, but got state: $($queryDetails.state)"
    }
}

# Setup OIDC issuer and managed identity access to storage/KV in publisher tenant.
$subject = $contractId + "-" + $publisherUserId
pwsh $PSScriptRoot/../setup-access.ps1 `
    -resourceGroup $publisherResourceGroup `
    -subject $subject `
    -issuerUrl $publisherIssuerUrl `
    -outDir $outDir `
    -kvType akvpremium

# Setup managed identity access to storage/KV in consumer tenant.
$subject = $contractId + "-" + $consumerUserId
pwsh $PSScriptRoot/../setup-access.ps1 `
    -resourceGroup $consumerResourceGroup `
    -subject $subject `
    -issuerUrl $consumerIssuerUrl `
    -outDir $outDir `
    -kvType akvpremium

@"
{
    "contractId": "$contractId",
    "queries": $(ConvertTo-Json $queryDocuments -Compress),
    "publisherCgsClient": "$publisherProjectName",
    "consumerCgsClient": "$consumerProjectName",
    "collaborationConfigFile": "$env:CLEANROOM_COLLABORATION_CONFIG_FILE",
    "consumerOutputS3BucketName": "$consumerOutputS3BucketName",
    "publisherDatasets": $(ConvertTo-Json $publisherDatasets -Compress),
    "consumerDatasets": $(ConvertTo-Json $consumerDatasets -Compress),
    "frontendEndpoint": $(if ($useFrontendService) { "`"http://$frontendServiceEndpoint`"" } else { "null" }),
    "collaborationId": "$consumerProjectName"

}
"@ > $outDir/submitSqlJobConfig.json

if ($useFrontendService) {
    . $PSScriptRoot/test-analytics.ps1

    $expectedDatasetCount = $formats.count * 6
    Test-DatasetCount `
        -frontendEndpoint $frontendServiceEndpoint `
        -collaborationName $consumerProjectName `
        -userToken $userToken `
        -runId $runId `
        -expectedCount $expectedDatasetCount

    $expectedQueryCount = ($formats.count * 2) + 2 # 2 queries per format + lowkmin and s3kmin query
    Test-QueryCount `
        -frontendEndpoint $frontendServiceEndpoint `
        -collaborationName $consumerProjectName `
        -userToken $userToken `
        -runId $runId `
        -expectedCount $expectedQueryCount

    Write-Output "Listing queries by the datasets used..."
    foreach ($format in $formats) {
        $dataset = $publisherDatasets["${format}_input"]
        $expectedQueryIds = @()

        if ($format -eq "csv") {
            $expectedQueryIds = @(
                $queryDocuments["${format}_standard"],
                $queryDocuments["csv_lowkmin"]
            )
            $expectedQueryCountForDataset = 2
        }
        else {
            $expectedQueryIds = @($queryDocuments["${format}_standard"])
            $expectedQueryCountForDataset = 1
        }
        Test-QueriesForDataset `
            -frontendEndpoint $frontendServiceEndpoint `
            -collaborationName $consumerProjectName `
            -userToken $userToken `
            -datasetId $dataset `
            -expectedQueryCount $expectedQueryCountForDataset `
            -expectedQueryIds $expectedQueryIds
    }
    foreach ($format in $formats) {
        $dataset = $consumerDatasets["${format}_output_s3"]
        $expectedQueryIds = @()

        if ($format -eq "csv") {
            $expectedQueryIds = @(
                $queryDocuments["s3_${format}_standard"],
                $queryDocuments["csv_lowkmin"],
                $queryDocuments["s3_csv_kmin"]
            )
            $expectedQueryCountForDataset = 3
        }
        else {
            $expectedQueryIds = @($queryDocuments["s3_${format}_standard"])
            $expectedQueryCountForDataset = 1
        }

        Test-QueriesForDataset `
            -frontendEndpoint $frontendServiceEndpoint `
            -collaborationName $consumerProjectName `
            -userToken $userToken `
            -datasetId $dataset `
            -expectedQueryCount $expectedQueryCountForDataset `
            -expectedQueryIds $expectedQueryIds
        $inputDataset = $consumerDatasets["${format}_input"]
        $expectedInputQueryIds = @()

        if ($format -eq "csv") {
            $expectedInputQueryIds = @(
                $queryDocuments["${format}_standard"],
                $queryDocuments["csv_lowkmin"]
            )
            $expectedInputQueryCount = 2
        }
        else {
            $expectedInputQueryIds = @($queryDocuments["${format}_standard"])
            $expectedInputQueryCount = 1
        }

        Test-QueriesForDataset `
            -frontendEndpoint $frontendServiceEndpoint `
            -collaborationName $consumerProjectName `
            -userToken $userToken `
            -datasetId $inputDataset `
            -expectedQueryCount $expectedInputQueryCount `
            -expectedQueryIds $expectedInputQueryIds

        $outputDataset = $consumerDatasets["${format}_output"]
        Test-QueriesForDataset `
            -frontendEndpoint $frontendServiceEndpoint `
            -collaborationName $consumerProjectName `
            -userToken $userToken `
            -datasetId $outputDataset `
            -expectedQueryCount 1 `
            -expectedQueryIds @($queryDocuments["${format}_standard"])
    }
}

if ($clCluster.infraType -eq "aks") {
    # Do parallel query runs on AKS setup = save on execution time.
    python3 -u $PSScriptRoot/submit-sql-job.py `
        --deployment-config-dir $deploymentConfigDir `
        --out-dir $outDir `
        --parallel `
        --format $formats
}
else {
    # Not doing parallel query runs on virtual setup as we hit pod scheduling limits.
    python3 -u $PSScriptRoot/submit-sql-job.py `
        --deployment-config-dir $deploymentConfigDir `
        --out-dir $outDir
}
