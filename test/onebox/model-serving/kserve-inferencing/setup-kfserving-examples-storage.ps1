[CmdletBinding()]
param
(
    [string]$resourceGroup = "azcleanroom-emu-pr-rg",

    [string]$subscriptionName = "AzureCleanRoom-NonProd",

    [string]$storageAccountName = "azcleanroomemusa",

    [string]$containerName = "kfserving-examples",

    [string]$gcsSourcePath = "gs://kfserving-examples/models/sklearn/1.0/model",

    [Parameter(Mandatory = $true)]
    [string]$outDir
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
. $root/samples/common/infra-scripts/aad-helpers.ps1

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
$role = "Storage Blob Data Contributor"
$roleAssignment = (az role assignment list `
        --assignee-object-id $objectId `
        --scope $result.id `
        --role $role `
        --fill-principal-name false `
        --fill-role-definition-name false) | ConvertFrom-Json

if ($roleAssignment.Length -eq 1) {
    Write-Host "'$role' permission on the storage account already exists, skipping assignment."
}
else {
    Write-Host "Assigning '$role' permissions to logged in user..."
    az role assignment create `
        --role $role `
        --scope $result.id `
        --assignee-object-id $objectId `
        --assignee-principal-type $assigneePrincipalType
}

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
function Test-BlobsExist($prefix) {
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

# --- sklearn model (from GCS) ---
$sklearnBlobPrefix = "models/sklearn/1.0/model"
if (Test-BlobsExist $sklearnBlobPrefix) {
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

# --- GPT-2 GGUF model (from HuggingFace) ---
$gpt2BlobPrefix = "models/gpt2-gguf"
if (Test-BlobsExist $gpt2BlobPrefix) {
    Write-Host "GPT-2 GGUF model already exists, skipping download."
}
else {
    $gpt2TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "gpt2-gguf-download"
    if (Test-Path $gpt2TempDir) { Remove-Item -Recurse -Force $gpt2TempDir }
    New-Item -ItemType Directory -Path $gpt2TempDir -Force | Out-Null

    $gpt2Url = "https://huggingface.co/RichardErkhov/openai-community_-_gpt2-gguf/resolve/main/gpt2.Q4_K_M.gguf"
    Write-Host "Downloading GPT-2 GGUF model..."
    curl -fsSL -o "$gpt2TempDir/model.gguf" $gpt2Url

    Upload-ModelToBlob $gpt2TempDir $gpt2BlobPrefix
}

Write-Host "kfserving-examples storage setup complete."

# Write resources.generated.json next to this script.
$resources = @{
    sa = $result
}

$resourcesFile = Join-Path $outDir "sa-resources.generated.json"
$resources | ConvertTo-Json -Depth 100 > $resourcesFile
Write-Host "Resources written to $resourcesFile"
