[CmdletBinding()]
param
(
    [string]$resourceGroup = "azcleanroom-public-pr-rg",

    [string]$subscriptionName = "AzureCleanRoom-NonProd",

    [string]$storageAccountName = "azcleanroompublicsa",

    [string]$containerName = "kfserving-examples",

    [string]$gcsSourcePath = "gs://kfserving-examples/models/sklearn/1.0/model",

    [Parameter(Mandatory = $true)]
    [string]$outDir,

    [string]$models = "default"
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
. $root/samples/common/infra-scripts/aad-helpers.ps1
. $PSScriptRoot/helpers.ps1

Write-Host "Setting subscription to '$subscriptionName'..."
az account set --subscription $subscriptionName

# Check if the storage account already exists; create only if it does not.
& {
    $PSNativeCommandUseErrorActionPreference = $false
    $script:result = (az storage account show `
            --name $storageAccountName `
            --resource-group $resourceGroup 2>$null) | ConvertFrom-Json
}

if ($null -ne $result) {
    Write-Host "Storage account '$storageAccountName' already exists, skipping creation."
}
else {
    Write-Host "Creating storage account '$storageAccountName' in resource group '$resourceGroup'..."
    $result = (az storage account create `
            --name $storageAccountName `
            --resource-group $resourceGroup `
            --min-tls-version TLS1_2 `
            --allow-shared-key-access false `
            --kind StorageV2) | ConvertFrom-Json
}

$objectId = GetLoggedInEntityObjectId
$assigneePrincipalType = Get-Assignee-Principal-Type
Ensure-RoleAssignment `
    -assigneeObjectId $objectId `
    -scope $result.id `
    -role "Storage Blob Data Contributor" `
    -principalType $assigneePrincipalType

# Check if the container already exists; create only if it does not.
$containerExists = $false
& {
    $PSNativeCommandUseErrorActionPreference = $false
    $existsResult = (az storage container exists `
            --name $containerName `
            --account-name $storageAccountName `
            --auth-mode login 2>$null) | ConvertFrom-Json
    if ($null -ne $existsResult -and $existsResult.exists -eq $true) {
        $script:containerExists = $true
    }
}

if ($containerExists) {
    Write-Host "Container '$containerName' already exists, skipping creation."
}
else {
    Write-Host "Creating container '$containerName'..."
    & {
        $PSNativeCommandUseErrorActionPreference = $false
        $timeout = New-TimeSpan -Seconds 120
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $created = $false
        while (!$created) {
            az storage container create `
                --name $containerName `
                --account-name $storageAccountName `
                --auth-mode login 1>$null 2>$null
            if ($LASTEXITCODE -eq 0) {
                $created = $true
            }
            else {
                if ($stopwatch.elapsed -gt $timeout) {
                    throw "Hit timeout waiting for RBAC permissions to be applied on the storage account."
                }
                $sleepTime = 10
                Write-Host "Waiting for $sleepTime seconds before retrying container creation..."
                Start-Sleep -Seconds $sleepTime
            }
        }
    }
}

# Helper: check if blobs exist under a prefix.
# Helper: check if ANY blobs exist under a prefix (for models with unknown file lists).
function Test-BlobPrefixExists($prefix) {
    $PSNativeCommandUseErrorActionPreference = $false
    $blobs = (az storage blob list `
            --container-name $containerName `
            --prefix $prefix `
            --account-name $storageAccountName `
            --auth-mode login `
            --num-results 1 2>$null) | ConvertFrom-Json
    $PSNativeCommandUseErrorActionPreference = $true
    return ($null -ne $blobs -and $blobs.Count -gt 0)
}

# Helper: check if ALL expected blobs exist with non-zero size.
function Test-AllBlobsExist($expectedBlobs) {
    $PSNativeCommandUseErrorActionPreference = $false
    foreach ($blob in $expectedBlobs) {
        $size = (az storage blob show `
                --name $blob `
                --container-name $containerName `
                --account-name $storageAccountName `
                --auth-mode login `
                --query "properties.contentLength" `
                --output tsv 2>$null)
        if (-not $size -or [long]$size -eq 0) {
            $PSNativeCommandUseErrorActionPreference = $true
            return $false
        }
    }
    $PSNativeCommandUseErrorActionPreference = $true
    return $true
}

# Helper: upload a local directory to Azure Blob Storage.
function Upload-ModelToBlob($sourceDir, $blobPrefix) {
    Write-Host "Uploading to container '$containerName' under '$blobPrefix'..."
    az storage blob upload-batch `
        --source $sourceDir `
        --destination $containerName `
        --destination-path $blobPrefix `
        --account-name $storageAccountName `
        --auth-mode login `
        --overwrite
}

# Helper: start a server-side copy from a public URL to Azure Blob Storage.
# Resolves HTTP redirects first since Azure's copy service cannot follow them.
# If a pending copy exists from a previous run, aborts it first.
function Start-UrlToBlob($sourceUrl, $blobName) {
    $resolvedUrl = curl -LsI -o /dev/null -w '%{url_effective}' $sourceUrl 2>$null
    if (-not $resolvedUrl) { $resolvedUrl = $sourceUrl }

    # Abort any pending copy from a previous failed run.
    & {
        $PSNativeCommandUseErrorActionPreference = $false
        $copyId = az storage blob show `
            --name $blobName `
            --container-name $containerName `
            --account-name $storageAccountName `
            --auth-mode login `
            --query "properties.copy.id" `
            --output tsv 2>$null
        if ($copyId) {
            Write-Host "  Aborting pending copy on $blobName (copyId: $copyId)..."
            az storage blob copy cancel `
                --copy-id $copyId `
                --destination-blob $blobName `
                --destination-container $containerName `
                --account-name $storageAccountName `
                --auth-mode login 2>$null
        }
    }

    az storage blob copy start `
        --source-uri $resolvedUrl `
        --destination-blob $blobName `
        --destination-container $containerName `
        --account-name $storageAccountName `
        --auth-mode login
}

# Helper: poll until a server-side blob copy completes, showing progress.
function Wait-BlobCopy($blobName) {
    $timeout = New-TimeSpan -Minutes 30
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $copyInfo = (az storage blob show `
                --name $blobName `
                --container-name $containerName `
                --account-name $storageAccountName `
                --auth-mode login `
                --query "properties.copy.{status:status, progress:progress}" `
                --output json 2>$null) | ConvertFrom-Json
        $status = $copyInfo.status
        if ($status -eq "success") { break }
        if ($status -eq "failed" -or $status -eq "aborted") {
            throw "Blob copy failed for $blobName (status: $status)"
        }
        if ($stopwatch.elapsed -gt $timeout) {
            throw "Timed out waiting for blob copy of $blobName"
        }
        # Show progress percentage if available (format: "bytesCopied/totalBytes").
        $progress = $copyInfo.progress
        if ($progress -and $progress -match "^(\d+)/(\d+)$") {
            $pct = [math]::Round(([long]$Matches[1] / [long]$Matches[2]) * 100, 1)
            Write-Host "    $(Split-Path $blobName -Leaf): ${pct}% ($progress bytes)"
        }
        Start-Sleep -Seconds 5
    }
}

# Helper: copy a file from URL to blob and wait for completion.
function Copy-UrlToBlob($sourceUrl, $blobName) {
    Start-UrlToBlob $sourceUrl $blobName
    Wait-BlobCopy $blobName
}

# Helper: copy a HuggingFace model to blob storage (config files + shards in parallel).
# $configFiles: array of small file names (copied sequentially).
# $shardCount/$shardTotal: shard numbering (e.g. 2 shards of 00002 → model-00001-of-00002.safetensors).
function Copy-HuggingFaceModelToBlob($displayName, $hfBaseUrl, $blobPrefix, $configFiles, $shardCount, $shardTotal) {
    # Build expected blob list for completeness check.
    $expectedBlobs = @()
    foreach ($f in $configFiles) { $expectedBlobs += "$blobPrefix/$f" }
    for ($i = 1; $i -le $shardCount; $i++) {
        $expectedBlobs += "$blobPrefix/model-{0:D5}-of-{1:D5}.safetensors" -f $i, $shardTotal
    }

    if (Test-AllBlobsExist $expectedBlobs) {
        Write-Host "$displayName model already exists, skipping."
        return
    }

    Write-Host "Copying $displayName model to blob storage..."

    # Config and tokenizer files (small, sequential)
    foreach ($f in $configFiles) {
        Write-Host "  Copying $f..."
        Copy-UrlToBlob "$hfBaseUrl/$f" "$blobPrefix/$f"
    }

    # Model weight shards (start all copies in parallel, then wait for all)
    $shardBlobs = @()
    for ($i = 1; $i -le $shardCount; $i++) {
        $shard = "model-{0:D5}-of-{1:D5}.safetensors" -f $i, $shardTotal
        $blobName = "$blobPrefix/$shard"
        Write-Host "  Starting copy of $shard..."
        Start-UrlToBlob "$hfBaseUrl/$shard" $blobName
        $shardBlobs += $blobName
    }
    foreach ($blobName in $shardBlobs) {
        $leaf = Split-Path $blobName -Leaf
        Write-Host "  Waiting for $leaf..."
        Wait-BlobCopy $blobName
        Write-Host "  $leaf copy completed."
    }
}

# Determine which models to download based on the --models parameter.
$enabledModels = $models -split ","
$runDefault = $enabledModels -contains "default"
$runIris = $runDefault -or $enabledModels -contains "iris"
$runTinyLlama = $runDefault -or $enabledModels -contains "tinyllama" -or $enabledModels -contains "tinyllama-gpu"
$runGemma4 = $enabledModels -contains "gemma4-gpu"
$runPhi4 = $enabledModels -contains "phi4-gpu"

# --- sklearn model (from GCS) ---
if ($runIris) {
    $sklearnBlobPrefix = "models/sklearn/1.0/model"
    if (Test-BlobPrefixExists $sklearnBlobPrefix) {
        Write-Host "sklearn model already exists, skipping download."
    }
    else {
        $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "kserve-model-download"
        if (Test-Path $tempDir) { Remove-Item -Recurse -Force $tempDir }
        New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

        Write-Host "Downloading sklearn model from '$gcsSourcePath'..."
        docker run --rm -v "${tempDir}:/data" `
            gcr.io/google.com/cloudsdktool/google-cloud-cli:latest `
            gsutil cp -r "$gcsSourcePath" /data/

        $localModelDir = Join-Path $tempDir "model"
        if (-not (Test-Path $localModelDir)) {
            throw "Expected model directory not found at '$localModelDir'."
        }

        Upload-ModelToBlob $localModelDir $sklearnBlobPrefix
    }
}

# --- TinyLlama-1.1B-Chat GGUF model (from HuggingFace) ---
if ($runTinyLlama) {
    $tinyLlamaBlobPrefix = "models/tinyllama-chat-gguf"
    $tinyLlamaExpected = @("$tinyLlamaBlobPrefix/model.gguf")
    if (Test-AllBlobsExist $tinyLlamaExpected) {
        Write-Host "TinyLlama-1.1B-Chat GGUF model already exists, skipping download."
    }
    else {
        $tinyLlamaUrl = "https://huggingface.co/TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF/resolve/main/tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf"
        Write-Host "Copying TinyLlama-1.1B-Chat GGUF model directly to blob storage..."
        Copy-UrlToBlob $tinyLlamaUrl "$tinyLlamaBlobPrefix/model.gguf"
    }
}

# --- Gemma 4 31B-IT model (from HuggingFace) ---
if ($runGemma4) {
    Copy-HuggingFaceModelToBlob `
        -displayName "Gemma 4 31B-IT" `
        -hfBaseUrl "https://huggingface.co/google/gemma-4-31B-it/resolve/main" `
        -blobPrefix "models/gemma4-31b-it" `
        -configFiles @("config.json", "generation_config.json", "tokenizer.json", "tokenizer_config.json", "processor_config.json", "chat_template.jinja", "model.safetensors.index.json") `
        -shardCount 2 `
        -shardTotal 2
}

# --- Phi-4 14B model (from HuggingFace) ---
if ($runPhi4) {
    Copy-HuggingFaceModelToBlob `
        -displayName "Phi-4 14B" `
        -hfBaseUrl "https://huggingface.co/microsoft/phi-4/resolve/main" `
        -blobPrefix "models/phi-4-14b" `
        -configFiles @("config.json", "generation_config.json", "tokenizer.json", "tokenizer_config.json", "special_tokens_map.json", "model.safetensors.index.json") `
        -shardCount 6 `
        -shardTotal 6
}

Write-Host "kfserving-examples storage setup complete."

# Write resources.generated.json next to this script.
$resources = @{
    sa = $result
}

$resourcesFile = Join-Path $outDir "sa-resources.generated.json"
$resources | ConvertTo-Json -Depth 100 > $resourcesFile
Write-Host "Resources written to $resourcesFile"
