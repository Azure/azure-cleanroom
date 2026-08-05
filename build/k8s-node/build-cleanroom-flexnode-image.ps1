param(
    [parameter(Mandatory = $false)]
    [string]$repo = "localhost:5000",

    [parameter(Mandatory = $false)]
    [string]$tag = "latest",

    [parameter(Mandatory = $false)]
    [string]$location = "germanywestcentral",

    [parameter(Mandatory = $false)]
    [string]$resourceGroup = "",

    [parameter(Mandatory = $false)]
    [string]$galleryResourceGroup = "",

    [parameter(Mandatory = $true)]
    [string]$imageVersion,

    [parameter(Mandatory = $false)]
    [string]$galleryName = "",

    [parameter(Mandatory = $false)]
    [string]$imageName = "",

    [parameter(Mandatory = $false)]
    [string[]]$targetLocations = @(),

    [parameter(Mandatory = $false)]
    [switch]$push,

    [parameter(Mandatory = $false)]
    [switch]$skipCleanup,

    [parameter(Mandatory = $false)]
    [string]$runId = "",

    [parameter(Mandatory = $false)]
    [string]$storageAccount = ""
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$imagePrep = "$root/src/k8s-node/image-prep"

# Derive defaults from the tag/run context.
if ($runId -eq "") {
    $runId = $tag
}
if ($resourceGroup -eq "") {
    $clean = $runId -replace "[^a-zA-Z0-9]", ""
    $resourceGroup = "flexnode-image-ci-$($clean.Substring(0, [Math]::Min($clean.Length, 16)))"
}
if ($galleryResourceGroup -eq "") {
    $galleryResourceGroup = "flexnode-image-gallery"
}

# Derive user-specific defaults for local development.
$_user = ($env:USER ?? $env:USERNAME ?? "dev").ToLower() -replace "[^a-z0-9]", ""

if ($galleryName -eq "") {
    if ($env:GITHUB_ACTIONS) {
        throw "galleryName must be provided in CI (use -galleryName parameter)."
    }
    $galleryName = "${_user}FlexnodeGallery"
}
if ($imageName -eq "") {
    if ($env:GITHUB_ACTIONS) {
        throw "imageName must be provided in CI (use -imageName parameter)."
    }
    $imageName = "${_user}-cleanroom-flexnode"
}
if ($storageAccount -eq "") {
    if ($env:GITHUB_ACTIONS) {
        throw "storageAccount must be provided in CI (use -storageAccount parameter)."
    }
    $storageAccount = "${_user}crimagesa"
}

# Validate imageVersion is Major.Minor.Patch with each component 0..2147483647.
if ($imageVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "imageVersion '$imageVersion' is invalid. Expected Major.Minor.Patch format (e.g. 1.0.0)."
}
$parts = $imageVersion -split '\.'
foreach ($part in $parts) {
    if ([long]$part -gt 2147483647) {
        throw "imageVersion component '$part' exceeds the maximum value of 2,147,483,647."
    }
}

Write-Host "================================================================="
Write-Host "Building Cleanroom Flex Node Image"
Write-Host "  Run ID         : $runId"
Write-Host "  Builder RG     : $resourceGroup"
Write-Host "  Gallery RG     : $galleryResourceGroup"
Write-Host "  Location       : $location"
Write-Host "  Gallery        : $galleryName"
Write-Host "  Image Name     : $imageName"
Write-Host "  Image Version  : $imageVersion"
Write-Host "  Storage Account: $storageAccount"
Write-Host "  OCI Repo       : $repo"
Write-Host "  Tag            : $tag"
Write-Host "================================================================="

# ---------- Configure the image-prep pipeline via env vars ----------
$ociEndpoint = $repo

$env:IMAGE_PREP_RESOURCE_GROUP = $resourceGroup
$env:IMAGE_PREP_GALLERY_RESOURCE_GROUP = $galleryResourceGroup
$env:IMAGE_PREP_LOCATION = $location
$env:IMAGE_PREP_IMAGE_VERSION = $imageVersion
$env:IMAGE_PREP_GALLERY_NAME = $galleryName
$env:IMAGE_PREP_IMAGE_NAME = $imageName

$env:IMAGE_PREP_STORAGE_ACCOUNT = $storageAccount

if ($targetLocations.Length -gt 0) {
    $env:IMAGE_PREP_TARGET_LOCATIONS = $targetLocations -join ","
}

# ---------- Render vars with the OCI URLs for this build ----------
$varsFile = "$imagePrep/vars.yaml"
$generatedVarsFile = "$imagePrep/generated/ci-vars.yaml"
New-Item -ItemType Directory -Path "$imagePrep/generated" -Force | Out-Null

# Read base vars and override the OCI URLs to point to this build's artifacts.
$vars = Get-Content $varsFile -Raw
$vars = $vars -replace 'api_server_proxy_oci_url:.*', "api_server_proxy_oci_url: `"$ociEndpoint/k8s-node/api-server-proxy:$tag`""
$vars = $vars -replace 'kubelet_proxy_oci_url:.*', "kubelet_proxy_oci_url: `"$ociEndpoint/k8s-node/kubelet-proxy:$tag`""
$vars = $vars -replace 'cleanroom_boot_oci_url:.*', "cleanroom_boot_oci_url: `"$ociEndpoint/k8s-node/cleanroom-boot:$tag`""
$vars = $vars -replace 'image_version:.*', "image_version: `"$imageVersion`""
$vars | Out-File -FilePath $generatedVarsFile -Encoding utf8
Write-Host "Generated vars file: $generatedVarsFile"

# ---------- Run the image-prep pipeline ----------
Write-Host "Running image-prep pipeline..."
Push-Location $root
try {
    uv sync --all-packages

    uv run image-prep-pipeline all `
        --vars $generatedVarsFile `
        --resource-group $resourceGroup `
        --location $location `
        --image-version $imageVersion `
        --run-id $runId
}
finally {
    Pop-Location
}

# ---------- Resolve the image version ARM ID ----------
$imageVersionId = az sig image-version show `
    --resource-group $galleryResourceGroup `
    --gallery-name $galleryName `
    --gallery-image-definition $imageName `
    --gallery-image-version $imageVersion `
    --query "id" `
    --output tsv

Write-Host "Image version ID: $imageVersionId"

# ---------- Generate cleanroom-image-digests.yaml ----------
$digestsDir = "$imagePrep/generated"
$digestsFile = "$digestsDir/cleanroom-image-digests.yaml"

@"
flexNodeImage:
  imageId: "$imageVersionId"
  imageVersion: "$imageVersion"
"@ | Out-File -FilePath $digestsFile -Encoding utf8

Write-Host "Digests file: $digestsFile"
Get-Content $digestsFile

# ---------- Push digests to OCI registry ----------
if ($push) {
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) "cleanroom-image-digests-$tag"
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
    New-Item -ItemType Directory -Path $staging | Out-Null

    Copy-Item $digestsFile "$staging/cleanroom-image-digests.yaml"

    Push-Location $staging
    try {
        Write-Host "Pushing digests to $repo/cleanroom-image-digests:$tag ..."
        oras push "$repo/cleanroom-image-digests:$tag" ./cleanroom-image-digests.yaml
        Write-Host "Digests pushed successfully."
    }
    finally {
        Pop-Location
        Remove-Item -Recurse -Force $staging
    }
}

# ---------- Cleanup builder resources ----------
# The builder RG contains transient resources (VM, NIC, public IP, disks,
# storage account). The gallery lives in a separate RG and is preserved.
if (!$skipCleanup) {
    Write-Host "Deleting builder resource group: $resourceGroup ..."
    az group delete `
        --name $resourceGroup `
        --yes `
        --no-wait `
        --output none `
        2>$null
    Write-Host "Builder resource group deletion initiated (--no-wait)."
}

Write-Host "================================================================="
Write-Host "Flex Node Image build complete."
Write-Host "  Image Version ID : $imageVersionId"
Write-Host "  Gallery RG       : $galleryResourceGroup"
Write-Host "  Digests File     : $digestsFile"
Write-Host "================================================================="
