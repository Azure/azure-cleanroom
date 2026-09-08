# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
.SYNOPSIS
    End-to-end governance scenario setup for TPC-DS stress testing on a Cleanroom
    analytics cluster.
#>

[CmdletBinding()]
param
(
    [string]
    $outDir = "$PSScriptRoot/generated/run",

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
    $deploymentConfigDir = "$PSScriptRoot/../../../workloads/generated",

    [string]
    $datastoreOutdir = "",

    # Default to a fresh per-run id so re-runs against the same CCF don't
    # collide with an already-Accepted contract (HTTP 405
    # ContractAlreadyAccepted). Mirrors the pattern in
    # big-data-query-analytics/test-big-data-analytics.py. Override only
    # when you intend to operate on a specific existing contract.
    [string]
    $contractId = "tpcds-analytics-$((New-Guid).ToString().Substring(0, 8))",

    [ValidateSet('mcr', 'local', 'acr')]
    [string]$registry = "local",

    [string]$repo = "localhost:5000",

    [string]$tag = "latest",

    [int]$scaleFactor = 700,

    [string]$dataFormats = "csv,parquet",

    [switch]
    $withSecurityPolicy,

    [string]$location = "centralindia",

    [string[]]$queryIds = @("query1", "query14", "query24", "query64", "query72"),

    [ValidateSet('SSE')]
    [string]$encryptionMode = "SSE",

    [string]$publisherResourceGroup = "cl-ob-publisher-tpcds-analytics",

    [string]$consumerResourceGroup = "cl-ob-consumer-tpcds-analytics",

    [string]$publisherStorageAccount = "avwgndilajulqsa",

    [string]$consumerStorageAccount = "nldjeffcxauamsa",

    # Suffix used for the per-run identity resource groups
    # ("<data-rg>-id-<identitySuffix>"). CI passes the GitHub run id so the
    # workflow can derive (and delete) the exact resource-group names without a
    # manifest. Defaults to the contractId hash for standalone/local runs.
    [string]$identitySuffix = "",

    # Minimum size (bytes) of the largest real (non-placeholder) blob in a
    # pre-loaded datastore container for it to be treated as TPC-DS data. The
    # check ignores the 10-byte "ghaction-b" write-access marker blob that every
    # container carries, so the floor only needs to exceed that marker and any
    # empty/truncated file. Small dimension tables (e.g. warehouse) are only a
    # few hundred bytes and are legitimately smaller than 1 KiB, so keep it low.
    [long]$minDataBlobBytes = 64
)

# https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# Normalize queryIds when passed as a single comma- or whitespace-joined string
# (e.g. CI passes `-queryIds "query1 query14 query24"` as one quoted arg).
if ($queryIds.Count -eq 1 -and $queryIds[0] -match '[,\s]') {
    $queryIds = $queryIds[0] -split '[,\s]+' | ForEach-Object { $_.Trim() } |
    Where-Object { $_ -ne '' }
}

$root = git rev-parse --show-toplevel
$stressTestDir = (Get-Item $PSScriptRoot).Parent.FullName
mkdir -p $outDir

$ccfOutDir = "$deploymentConfigDir/ccf"
$clClusterOutDir = "$deploymentConfigDir/cl-cluster"

$serviceCert = $ccfOutDir + "/service_cert.pem"
if (-not (Test-Path -Path $serviceCert)) {
    throw "serviceCert at $serviceCert does not exist."
}

$formats = $dataFormats -split "," |
ForEach-Object { $_.Trim().Trim('"', "'") } |
Where-Object { $_ -ne "" }
$generatedRoot = Join-Path $stressTestDir "generated"

$tableConfigPath = Join-Path $stressTestDir "fixtures/table-partition-config.json"
$tableConfig = Get-Content -Raw -Path $tableConfigPath | ConvertFrom-Json
$publisherAllowedFields = $tableConfig.publisher.allowed_fields -join ","
$consumerAllowedFields = $tableConfig.consumer.allowed_fields -join ","
$outputAllowedFields = $tableConfig.output.allowed_fields -join ","
$outputSchemaFields = $tableConfig.output.schema_fields
$publisherTables = $tableConfig.publisher.tables
$consumerTables = $tableConfig.consumer.tables

# Restrict publisher/consumer table lists to tables actually referenced by the
# selected queries. Avoids requiring blobs for unused tables (e.g. a
# broadcast_join run doesn't need store_sales; a TPC-DS run doesn't need
# publisher_view). Mirrors the per-query input-dataset filtering below.
if ($queryIds.Count -gt 0) {
    $fixtureQueryDir = Join-Path $stressTestDir "fixtures/queries"
    $selectedSqlText = ""
    foreach ($qid in $queryIds) {
        foreach ($suffix in @("", "a", "b")) {
            $p = Join-Path $fixtureQueryDir "${qid}${suffix}.sql"
            if (Test-Path $p) {
                $selectedSqlText += (Get-Content -Raw $p).ToLower() + "`n"
            }
        }
    }
    if ($selectedSqlText -ne "") {
        $publisherTables = @($publisherTables | Where-Object {
                $selectedSqlText -match "\b$([regex]::Escape($_))\b"
            })
        $consumerTables = @($consumerTables | Where-Object {
                $selectedSqlText -match "\b$([regex]::Escape($_))\b"
            })
        Write-Output ("Filtered tables to those referenced by selected " +
            "queries: publisher=[$($publisherTables -join ',')] " +
            "consumer=[$($consumerTables -join ',')]")
    }
}

$tableSchemasPath = Join-Path $generatedRoot "table-schemas.json"
$fixtureSchemasPath = Join-Path $stressTestDir "fixtures/table-schemas.json"
New-Item -ItemType Directory -Force -Path $generatedRoot `
    -ErrorAction SilentlyContinue | Out-Null
if (-not (Test-Path -Path $fixtureSchemasPath)) {
    throw "table-schemas.json fixture missing at $fixtureSchemasPath."
}
if (-not (Test-Path -Path $tableSchemasPath)) {
    Copy-Item -Path $fixtureSchemasPath -Destination $tableSchemasPath -Force
}
$fixtureSchemas = Get-Content -Raw -Path $fixtureSchemasPath | ConvertFrom-Json
$tableSchemas = Get-Content -Raw -Path $tableSchemasPath | ConvertFrom-Json

# Keep generated schema cache backward-compatible by auto-merging any newly
# added fixture tables (e.g. publisher_view/consumer_view for repro queries).
$mergedMissingSchemaKeys = 0
foreach ($prop in $fixtureSchemas.PSObject.Properties) {
    if ($null -eq $tableSchemas.PSObject.Properties[$prop.Name]) {
        $tableSchemas | Add-Member -NotePropertyName $prop.Name -NotePropertyValue $prop.Value
        $mergedMissingSchemaKeys++
    }
}
if ($mergedMissingSchemaKeys -gt 0) {
    $tableSchemas | ConvertTo-Json -Depth 50 | Set-Content -Path $tableSchemasPath -Encoding ascii
    Write-Output "Merged $mergedMissingSchemaKeys missing table schema entries into $tableSchemasPath."
}

if ($datastoreOutdir -eq "") {
    $datastoreOutdir = "$outDir/datastores"
}

rm -rf "$datastoreOutdir"
mkdir -p "$datastoreOutdir"
$publisherDatastoreConfig = "$datastoreOutdir/tpcds-publisher-datastore-config"

rm -rf "$datastoreOutdir/secrets"
mkdir -p "$datastoreOutdir/secrets"
$publisherSecretStoreConfig = "$datastoreOutdir/secrets/tpcds-publisher-secretstore-config"
$publisherLocalSecretStore = "$datastoreOutdir/secrets/tpcds-publisher-secretstore-local"

$consumerDatastoreConfig = "$datastoreOutdir/tpcds-consumer-datastore-config"
$consumerSecretStoreConfig = "$datastoreOutdir/secrets/tpcds-consumer-secretstore-config"
$consumerLocalSecretStore = "$datastoreOutdir/secrets/tpcds-consumer-secretstore-local"

Write-Output "=== Step 2: Setting up local IDP and users ==="
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
    $localIdpEndpoint = "http://172.17.0.1:$idpPort"
}

# Place the managed identity + its federated credentials in a per-run, unlocked
# resource group that is separate from the (delete-locked) storage/key-vault
# data resource group. The identity resource group is torn down at the end of
# the run (see the FIC/identity cleanup), so federated credentials never leak on
# a shared, locked managed identity. The suffix is the GitHub run id in CI (so
# the workflow can derive the exact names for cleanup) and falls back to the
# contractId hash for standalone/local runs; either way it is unique per run so
# concurrent runs never collide.
$runSuffix = if (-not [string]::IsNullOrWhiteSpace($identitySuffix)) { $identitySuffix } else { ($contractId -split '-')[-1] }
$publisherIdentityResourceGroup = "$publisherResourceGroup-id-$runSuffix"
$consumerIdentityResourceGroup = "$consumerResourceGroup-id-$runSuffix"

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
az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $ownerClient

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
az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $ownerClient

Write-Output "=== Step 3: Deploying CGS clients ==="

Remove-Item -Path "$outDir/collaboration-config-*.yaml" -Force -ErrorAction SilentlyContinue
Remove-Item -Path "$outDir/submitSqlJobConfig.json" -Force -ErrorAction SilentlyContinue
Remove-Item -Path "$outDir/queries/tpcds-*.yaml" -Force -ErrorAction SilentlyContinue

$runId = (New-Guid).ToString().Substring(0, 8)
$env:CLEANROOM_COLLABORATION_CONFIG_FILE = "$outDir/collaboration-config-$runId.yaml"

$publisherProjectName = "ob-cr-tpcds-publisher-client"
$envFilePath = "$ccfOutDir/governance-client.env"
az cleanroom governance client remove --name $publisherProjectName
az cleanroom governance client deploy `
    --ccf-endpoint $ccfEndpoint `
    --use-local-identity `
    --local-identity-endpoint "$localIdpEndpoint/oauth/token?oid=$publisherUserId&tid=$publisherTenantId&preferred_username=publisher@example.com" `
    --service-cert $serviceCert `
    --name $publisherProjectName `
    --env-file $envFilePath

az cleanroom governance user-identity show `
    --identity-id $publisherUserId `
    --governance-client $publisherProjectName

az cleanroom governance client get-access-token `
    --query accessToken -o tsv --name $publisherProjectName

az cleanroom collaboration context add `
    --collaboration-name $publisherProjectName `
    --collaborator-id $publisherUserId `
    --governance-client $publisherProjectName

$consumerProjectName = "ob-cr-tpcds-consumer-client"
az cleanroom governance client remove --name $consumerProjectName
az cleanroom governance client deploy `
    --ccf-endpoint $ccfEndpoint `
    --use-local-identity `
    --local-identity-endpoint "$localIdpEndpoint/oauth/token?oid=$consumerUserId&tid=$consumerTenantId&preferred_username=consumer@example.com" `
    --service-cert $serviceCert `
    --name $consumerProjectName `
    --env-file $envFilePath

az cleanroom governance user-identity show `
    --identity-id $consumerUserId `
    --governance-client $consumerProjectName

az cleanroom governance client get-access-token `
    --query accessToken -o tsv --name $consumerProjectName

az cleanroom collaboration context add `
    --collaboration-name $consumerProjectName `
    --collaborator-id $consumerUserId `
    --governance-client $consumerProjectName

Write-Output "=== Step 4: Setting up datastores ==="

$publisherOverrides = "$outDir/$publisherResourceGroup/overrides"
New-Item -ItemType Directory -Force -Path (Split-Path $publisherOverrides) | Out-Null
$publisherOverrideLines = @()
if (-not [string]::IsNullOrWhiteSpace($publisherStorageAccount)) {
    $publisherOverrideLines += "`$STORAGE_ACCOUNT_NAME = `"$publisherStorageAccount`""
}
$publisherOverrideLines += "`$MANAGED_IDENTITY_RESOURCE_GROUP = `"$publisherIdentityResourceGroup`""
$publisherOverrideLines | Set-Content -Path $publisherOverrides

pwsh $PSScriptRoot/../../../prepare-resources.ps1 `
    -resourceGroup $publisherResourceGroup `
    -resourceGroupTags "" `
    -kvType akvpremium `
    -storageType blob `
    -overridesFilePath $publisherOverrides `
    -outDir $outDir `
    -location $location

# Re-apply SkipCleanup=true (prepare-resources.ps1 replaces tags on PUT).
$publisherRgId = az group show --name $publisherResourceGroup --query id -o tsv
az tag update --resource-id $publisherRgId --operation merge `
    --tags SkipCleanup=true --output none

$publisherResult = Get-Content "$outDir/$publisherResourceGroup/resources.generated.json" |
ConvertFrom-Json

az cleanroom secretstore add `
    --name publisher-local-store `
    --config $publisherSecretStoreConfig `
    --backingstore-type Local_File `
    --backingstore-path $publisherLocalSecretStore

foreach ($format in $formats) {
    foreach ($table in $publisherTables) {
        $tableSafe = $table.Replace("_", "-")
        $datastoreName = "tpcds-pub-${tableSafe}-sf${scaleFactor}-${format}"
        $schemaFields = $tableSchemas.$table
        if ([string]::IsNullOrWhiteSpace($schemaFields)) {
            throw ("Missing schema_fields for publisher table '$table' in " +
                "$tableSchemasPath. Sync it from fixtures/table-schemas.json.")
        }

        az cleanroom datastore add `
            --name $datastoreName `
            --config $publisherDatastoreConfig `
            --secretstore publisher-local-store `
            --secretstore-config $publisherSecretStoreConfig `
            --encryption-mode $encryptionMode `
            --backingstore-type Azure_BlobStorage `
            --backingstore-id $publisherResult.sa.id `
            --schema-format $format `
            --schema-fields $schemaFields

        pwsh $root/test/onebox/multi-party-collab/wait-for-container-access.ps1 `
            --containerName $datastoreName `
            --storageAccountId $publisherResult.sa.id
        $maxBlobBytes = az storage blob list `
            --account-name $publisherResult.sa.name `
            --container-name $datastoreName `
            --auth-mode login `
            --only-show-errors `
            --query "max([?name != 'ghaction-b'].properties.contentLength)" -o tsv
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to list blobs in publisher datastore '$datastoreName' (exit $LASTEXITCODE). Check RBAC/auth."
        }
        if (-not $maxBlobBytes -or [long]$maxBlobBytes -lt $minDataBlobBytes) {
            throw ("Publisher datastore '$datastoreName' has no real data in SA " +
                "'$($publisherResult.sa.name)' (largest blob '$maxBlobBytes' bytes < " +
                "$minDataBlobBytes-byte minimum; looks like an empty or placeholder " +
                "container). Pre-load actual TPC-DS data via " +
                "generate-tpcds-on-azure-vm.ps1 against this SA, or pass " +
                "-publisherStorageAccount <name> to use a pre-populated SA.")
        }
    }
}

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

$consumerOverrides = "$outDir/$consumerResourceGroup/overrides"
New-Item -ItemType Directory -Force -Path (Split-Path $consumerOverrides) | Out-Null
$consumerOverrideLines = @()
if (-not [string]::IsNullOrWhiteSpace($consumerStorageAccount)) {
    $consumerOverrideLines += "`$STORAGE_ACCOUNT_NAME = `"$consumerStorageAccount`""
}
$consumerOverrideLines += "`$MANAGED_IDENTITY_RESOURCE_GROUP = `"$consumerIdentityResourceGroup`""
$consumerOverrideLines | Set-Content -Path $consumerOverrides

pwsh $PSScriptRoot/../../../prepare-resources.ps1 `
    -resourceGroup $consumerResourceGroup `
    -resourceGroupTags "" `
    -kvType akvpremium `
    -storageType blob `
    -overridesFilePath $consumerOverrides `
    -outDir $outDir `
    -location $location

# Re-apply SkipCleanup=true.
$consumerRgId = az group show --name $consumerResourceGroup --query id -o tsv
az tag update --resource-id $consumerRgId --operation merge `
    --tags SkipCleanup=true --output none

$consumerResult = Get-Content "$outDir/$consumerResourceGroup/resources.generated.json" |
ConvertFrom-Json

az cleanroom secretstore add `
    --name consumer-local-store `
    --config $consumerSecretStoreConfig `
    --backingstore-type Local_File `
    --backingstore-path $consumerLocalSecretStore

foreach ($format in $formats) {
    foreach ($table in $consumerTables) {
        $tableSafe = $table.Replace("_", "-")
        $datastoreName = "tpcds-con-${tableSafe}-sf${scaleFactor}-${format}"
        $schemaFields = $tableSchemas.$table
        if ([string]::IsNullOrWhiteSpace($schemaFields)) {
            throw ("Missing schema_fields for consumer table '$table' in " +
                "$tableSchemasPath. Sync it from fixtures/table-schemas.json.")
        }

        az cleanroom datastore add `
            --name $datastoreName `
            --config $consumerDatastoreConfig `
            --secretstore consumer-local-store `
            --secretstore-config $consumerSecretStoreConfig `
            --encryption-mode $encryptionMode `
            --backingstore-type Azure_BlobStorage `
            --backingstore-id $consumerResult.sa.id `
            --schema-format $format `
            --schema-fields $schemaFields

        pwsh $root/test/onebox/multi-party-collab/wait-for-container-access.ps1 `
            --containerName $datastoreName `
            --storageAccountId $consumerResult.sa.id

        $maxBlobBytes = az storage blob list `
            --account-name $consumerResult.sa.name `
            --container-name $datastoreName `
            --auth-mode login `
            --only-show-errors `
            --query "max([?name != 'ghaction-b'].properties.contentLength)" -o tsv
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to list blobs in consumer datastore '$datastoreName' (exit $LASTEXITCODE). Check RBAC/auth."
        }
        if (-not $maxBlobBytes -or [long]$maxBlobBytes -lt $minDataBlobBytes) {
            throw ("Consumer datastore '$datastoreName' has no real data in SA " +
                "'$($consumerResult.sa.name)' (largest blob '$maxBlobBytes' bytes < " +
                "$minDataBlobBytes-byte minimum; looks like an empty or placeholder " +
                "container). Pre-load actual TPC-DS data via " +
                "generate-tpcds-on-azure-vm.ps1 against this SA, or pass " +
                "-consumerStorageAccount <name> to use a pre-populated SA.")
        }
    }

    $outputDatastoreName = "tpcds-consumer-output-sf${scaleFactor}-$format"
    az cleanroom datastore add `
        --name $outputDatastoreName `
        --config $consumerDatastoreConfig `
        --secretstore consumer-local-store `
        --secretstore-config $consumerSecretStoreConfig `
        --encryption-mode $encryptionMode `
        --backingstore-type Azure_BlobStorage `
        --backingstore-id $consumerResult.sa.id `
        --schema-format $format `
        --schema-fields $outputSchemaFields

    pwsh $root/test/onebox/multi-party-collab/wait-for-container-access.ps1 `
        --containerName $outputDatastoreName `
        --storageAccountId $consumerResult.sa.id
}

$outputTtlDays = 2
Write-Output "=== Applying $outputTtlDays-day TTL to tpcds-consumer-output-* blobs in $($consumerResult.sa.name) ==="
$lifecyclePolicy = @{
    rules = @(
        @{
            enabled    = $true
            name       = "tpcds-consumer-output-ttl-${outputTtlDays}d"
            type       = "Lifecycle"
            definition = @{
                actions = @{
                    baseBlob = @{
                        delete = @{ daysAfterModificationGreaterThan = $outputTtlDays }
                    }
                    snapshot = @{
                        delete = @{ daysAfterCreationGreaterThan = $outputTtlDays }
                    }
                    version  = @{
                        delete = @{ daysAfterCreationGreaterThan = $outputTtlDays }
                    }
                }
                filters = @{
                    blobTypes   = @("blockBlob")
                    prefixMatch = @("tpcds-consumer-output-")
                }
            }
        }
    )
} | ConvertTo-Json -Depth 10 -Compress
$lifecyclePolicyFile = Join-Path $outDir "consumer-output-lifecycle-policy.json"
$lifecyclePolicy | Out-File -FilePath $lifecyclePolicyFile -Encoding ascii
$consumerSaRg = ($consumerResult.sa.id -split "/")[4]
az storage account management-policy create `
    --account-name $consumerResult.sa.name `
    --resource-group $consumerSaRg `
    --policy "@$lifecyclePolicyFile" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Failed to apply lifecycle TTL policy on $($consumerResult.sa.name) (exit $LASTEXITCODE)."
}
Write-Output "  applied: tpcds-consumer-output-* blobs delete after $outputTtlDays days from last modification."

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

Write-Output "=== Step 5: Creating and approving contract ==="

$agent = Get-Content $ccfOutDir/ccf.recovery-agent.json | ConvertFrom-Json
$agentEndpoint = $agent.endpoint
$agentNetworkReport = curl --fail-with-body -k -s -S $agentEndpoint/network/report |
ConvertFrom-Json
$reportDataContent = $agentNetworkReport.reportDataPayload | base64 -d | ConvertFrom-Json

$recoveryMembers = az cleanroom governance member show `
    --governance-client $ownerClient | jq '[.value[] | select(.publicEncryptionKey != null) | .memberId]' -c
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

az cleanroom governance contract vote `
    --id $contractId `
    --proposal-id $contract.proposalId `
    --action accept `
    --governance-client $ownerClient

Write-Output "Enabling CA..."
az cleanroom governance ca propose-enable `
    --contract-id $contractId `
    --governance-client $ownerClient

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

Write-Output "=== Step 6: Deployment template and policy ==="

mkdir -p $outDir/deployments
$repoConfig = Get-Content $clClusterOutDir/repoConfig.json | ConvertFrom-Json
$clusterProviderProjectName = $repoConfig.clusterProviderProjectName

# TODO: Switch back to security-policy mode when the pod annotation size
# issue is fixed and generated CCE policy no longer exceeds the Kubernetes
# 256 KiB annotation cap.
$option = "allow-all"
if ($withSecurityPolicy) {
    Write-Warning (
        "-withSecurityPolicy was passed but is ignored for TPC-DS stress " +
        "runs: the per-pod CCE policy in cached-debug mode exceeds the " +
        "kube 256 KiB annotation cap. Forcing allow-all."
    )
}

$clCluster = Get-Content $clClusterOutDir/cl-cluster.json | ConvertFrom-Json

Write-Output "Generating deployment template/policy with $option creation option..."
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

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $ownerClient

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
# Route through local kubectl proxy so the stress harness can reach the
# analytics agent without an external ingress endpoint.
$analyticsEndpoint = "http://localhost:8181/api/v1/namespaces/cleanroom-spark-analytics-agent/services/https:cleanroom-spark-analytics-agent:443/proxy"
Write-Output "Using analytics endpoint: $analyticsEndpoint"
$deploymentInformation = @{
    url = $analyticsEndpoint
} | ConvertTo-Json

az cleanroom governance deployment information propose `
    --deployment-information $deploymentInformation `
    --contract-id $contractId `
    --governance-client $ownerClient

$proposalId = az cleanroom governance deployment information show `
    --contract-id $contractId `
    --governance-client $ownerClient `
    --query "proposalIds[0]" `
    --output tsv

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $ownerClient

Write-Output "=== Step 7: OIDC setup and dataset publishing ==="

$identity = $(az resource show --ids $publisherResult.mi.id --query "properties") |
ConvertFrom-Json

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

pwsh $PSScriptRoot/../../../setup-oidc-issuer.ps1 `
    -resourceGroup $publisherResourceGroup `
    -outDir $outDir `
    -oidcIssuerLevel "member-tenant" `
    -governanceClient $ownerClient

$issuerUrl = Get-Content $outDir/$publisherResourceGroup/issuer-url.txt

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

$publisherDatasets = @{}
foreach ($format in $formats) {
    foreach ($table in $publisherTables) {
        $tableSafe = $table.Replace("_", "-")
        $datasetName = "pub-${tableSafe}-${format}-${runId}"
        $datastoreName = "tpcds-pub-${tableSafe}-sf${scaleFactor}-${format}"
        $publisherDatasets["${table}_${format}"] = $datasetName

        az cleanroom collaboration dataset publish `
            --contract-id $contractId `
            --dataset-name $datasetName `
            --datastore-name $datastoreName `
            --identity-name publisher-identity `
            --dek-secret-store-name publisher-dek-store `
            --kek-secret-store-name publisher-kek-store `
            --policy-access-mode read `
            --policy-allowed-fields $publisherAllowedFields `
            --datastore-config-file $publisherDatastoreConfig `
            --secretstore-config-file $publisherSecretStoreConfig

        $datasetState = (az cleanroom governance user-document show `
                --id $datasetName `
                --governance-client $publisherProjectName `
                --query "state" `
                --output tsv)
        if ($datasetState -ne "Accepted") {
            throw "Expected dataset '$datasetName' to be 'Accepted', got: $datasetState"
        }
    }
}

$identity = $(az resource show --ids $consumerResult.mi.id --query "properties") |
ConvertFrom-Json
if ($identity.tenantId -ne $ownerTenantId) {
    throw "Consumer's access identity tenant Id $($identity.tenantId) does not match owner's tenant Id $ownerTenantId."
}

# Store the same issuer under the consumer user.
az cleanroom governance oidc-issuer set-issuer-url `
    --governance-client $consumerProjectName `
    --url $issuerUrl

az cleanroom collaboration context set `
    --collaboration-name $consumerProjectName

az cleanroom collaboration identity add az-federated `
    --identity-name consumer-identity `
    --client-id $identity.clientId `
    --tenant-id $identity.tenantId `
    --backing-identity cleanroom_cgs_oidc

$consumerDatasets = @{}
foreach ($format in $formats) {
    foreach ($table in $consumerTables) {
        $tableSafe = $table.Replace("_", "-")
        $datasetName = "con-${tableSafe}-${format}-${runId}"
        $datastoreName = "tpcds-con-${tableSafe}-sf${scaleFactor}-${format}"
        $consumerDatasets["${table}_${format}"] = $datasetName

        az cleanroom collaboration dataset publish `
            --contract-id $contractId `
            --dataset-name $datasetName `
            --datastore-name $datastoreName `
            --identity-name consumer-identity `
            --dek-secret-store-name consumer-dek-store `
            --kek-secret-store-name consumer-kek-store `
            --policy-access-mode read `
            --policy-allowed-fields $consumerAllowedFields `
            --datastore-config-file $consumerDatastoreConfig `
            --secretstore-config-file $consumerSecretStoreConfig

        $datasetState = (az cleanroom governance user-document show `
                --id $datasetName `
                --governance-client $consumerProjectName `
                --query "state" `
                --output tsv)
        if ($datasetState -ne "Accepted") {
            throw "Expected dataset '$datasetName' to be 'Accepted', got: $datasetState"
        }
    }

    # Consumer output dataset (one per format, not per table).
    $consumerOutputDatasetName = "tpcds-consumer-output-$format-$runId"
    $consumerDatasets["${format}_output"] = $consumerOutputDatasetName

    az cleanroom collaboration dataset publish `
        --contract-id $contractId `
        --dataset-name $consumerOutputDatasetName `
        --datastore-name "tpcds-consumer-output-sf${scaleFactor}-$format" `
        --identity-name consumer-identity `
        --dek-secret-store-name consumer-dek-store `
        --kek-secret-store-name consumer-kek-store `
        --policy-access-mode write `
        --policy-allowed-fields $outputAllowedFields `
        --datastore-config-file $consumerDatastoreConfig `
        --secretstore-config-file $consumerSecretStoreConfig

    $datasetState = (az cleanroom governance user-document show `
            --id $consumerOutputDatasetName `
            --governance-client $consumerProjectName `
            --query "state" `
            --output tsv)
    if ($datasetState -ne "Accepted") {
        throw "Expected dataset '$consumerOutputDatasetName' to be 'Accepted', got: $datasetState"
    }
}

Write-Output "=== Step 8: Publishing and approving queries ==="

$queryDir = Join-Path $generatedRoot "queries"
$fixtureQueryDir = Join-Path $stressTestDir "fixtures/queries"

if (-not (Test-Path -Path $fixtureQueryDir)) {
    throw "Query fixtures missing at $fixtureQueryDir. Commit .sql files there (generate via dsqgen locally if needed)."
}
$fixtureSqls = @(Get-ChildItem -Path $fixtureQueryDir -Filter "*.sql" |
    Where-Object { $_.Length -gt 0 })
if ($fixtureSqls.Count -eq 0) {
    throw "No non-empty .sql fixtures found in $fixtureQueryDir."
}
New-Item -ItemType Directory -Force -Path $queryDir | Out-Null

# Sync NEW fixtures into generated/ without overwriting locally-modified copies.
# This ensures freshly committed .sql files (e.g. repro_oom_shuffle.sql) are
# picked up even when generated/queries/ already has stale files from a
# previous run.
$copied = 0
foreach ($f in $fixtureSqls) {
    $dest = Join-Path $queryDir $f.Name
    if (-not (Test-Path -Path $dest)) {
        Copy-Item -Path $f.FullName -Destination $dest -Force
        $copied++
    }
}
Write-Output "Query fixtures: $($fixtureSqls.Count) committed, $copied newly copied into $queryDir."

$queryDocuments = @{}
$queryFiles = Get-ChildItem -Path $queryDir -Filter "*.sql" | Sort-Object Name

$skipParameterizedB = @('query14b', 'query23b', 'query24b', 'query39b')
$queryFiles = $queryFiles | Where-Object { $skipParameterizedB -notcontains $_.BaseName }

if ($queryIds.Count -gt 0) {
    $queryFiles = $queryFiles | Where-Object {
        $base = $_.BaseName
        foreach ($qid in $queryIds) {
            if ($base -eq $qid -or $base -match "^${qid}[a-z]$") { return $true }
        }
        return $false
    }
    Write-Output "Filtered to $($queryFiles.Count) queries: $($queryFiles.BaseName -join ', ')"
}

foreach ($queryFile in $queryFiles) {
    $queryTemplate = $queryFile.BaseName
    $queryContent = (Get-Content -Path $queryFile.FullName -Raw).Trim()

    foreach ($format in $formats) {
        $queryDocumentId = "tpcds-${queryTemplate}-${format}-${runId}"
        $queryConfigDir = "$outDir/queries"
        mkdir -p $queryConfigDir
        $consumerQueryConfigFile = "$queryConfigDir/$queryDocumentId.yaml"

        az cleanroom collaboration spark-sql query segment add `
            --config-file $consumerQueryConfigFile `
            --query-content "$queryContent" `
            --execution-sequence 1

        az cleanroom collaboration context set `
            --collaboration-name $consumerProjectName

        # Only include tables referenced by the query SQL.
        $sqlLower = $queryContent.ToLower()
        $inputParts = @()
        foreach ($t in $publisherTables) {
            if ($sqlLower -match "\b$([regex]::Escape($t))\b") {
                $inputParts += "${t}:$($publisherDatasets["${t}_${format}"])"
            }
        }
        foreach ($t in $consumerTables) {
            if ($sqlLower -match "\b$([regex]::Escape($t))\b") {
                $inputParts += "${t}:$($consumerDatasets["${t}_${format}"])"
            }
        }
        $inputDatasetStr = $inputParts -join ", "

        az cleanroom collaboration spark-sql publish `
            --application-name $queryDocumentId `
            --application-query $consumerQueryConfigFile `
            --application-input-dataset $inputDatasetStr `
            --application-output-dataset "output:$($consumerDatasets["${format}_output"])" `
            --contract-id $contractId

        $queryState = (az cleanroom governance user-document show `
                --id $queryDocumentId `
                --governance-client $consumerProjectName `
                --query "state" `
                --output tsv)
        if ($queryState -ne "Proposed") {
            throw "Query '$queryDocumentId' is not in Proposed state before voting. Current: $queryState"
        }

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

        $queryDocuments["${queryTemplate}_${format}"] = $queryDocumentId

        $queryState = (az cleanroom governance user-document show `
                --id $queryDocumentId `
                --governance-client $consumerProjectName `
                --query "state" `
                --output tsv)
        if ($queryState -ne "Accepted") {
            throw "Query '$queryDocumentId' not in Accepted state after voting. Current: $queryState"
        }
    }
}

Write-Output "=== Step 9: Setting up RBAC access ==="

$subject = $contractId + "-" + $publisherUserId
pwsh $PSScriptRoot/../../../setup-access.ps1 `
    -resourceGroup $publisherResourceGroup `
    -subject $subject `
    -issuerUrl $issuerUrl `
    -outDir $outDir `
    -kvType akvpremium

$subject = $contractId + "-" + $consumerUserId
pwsh $PSScriptRoot/../../../setup-access.ps1 `
    -resourceGroup $consumerResourceGroup `
    -subject $subject `
    -issuerUrl $issuerUrl `
    -outDir $outDir `
    -kvType akvpremium

Write-Output "=== Step 9b: Validating published dataset and query counts ==="

$expectedDatasetCount = (($publisherTables.Count + $consumerTables.Count) * $formats.Count) + $formats.Count
$actualDatasetCount = ($publisherDatasets.Count + $consumerDatasets.Count)
if ($actualDatasetCount -ne $expectedDatasetCount) {
    throw "Dataset count mismatch. Expected: $expectedDatasetCount, Actual: $actualDatasetCount"
}

$expectedQueryCount = ($queryFiles.Count * $formats.Count)
$actualQueryCount = $queryDocuments.Count
if ($actualQueryCount -ne $expectedQueryCount) {
    throw "Query count mismatch. Expected: $expectedQueryCount, Actual: $actualQueryCount"
}

Write-Output "=== Step 10: Writing submitSqlJobConfig.json ==="

@"
{
    "contractId": "$contractId",
    "queries": $(ConvertTo-Json $queryDocuments -Compress),
    "scaleFactor": $scaleFactor,
    "dataFormats": $(ConvertTo-Json $formats -Compress),
    "publisherCgsClient": "$publisherProjectName",
    "consumerCgsClient": "$consumerProjectName",
    "consumerProjectName": "$consumerProjectName",
    "collaborationConfigFile": "$env:CLEANROOM_COLLABORATION_CONFIG_FILE",
    "publisherDatasets": $(ConvertTo-Json $publisherDatasets -Compress),
    "consumerDatasets": $(ConvertTo-Json $consumerDatasets -Compress),
    "consumerDatastoreConfig": "$consumerDatastoreConfig",
    "consumerStorageAccount": "$($consumerResult.sa.name)",
    "ccfEndpoint": "$ccfEndpoint",
    "collaborationId": "$consumerProjectName"
}
"@ > $outDir/submitSqlJobConfig.json
