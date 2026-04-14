# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
.SYNOPSIS
    Deploy an Azure Confidential VM (CVM) for attestation testing.
.DESCRIPTION
    Creates an Azure CVM with vTPM enabled for SNP attestation testing.
    VM name and resource group are auto-generated: from JOB_ID / RUN_ID in
    GitHub Actions, or from the current user name otherwise. SSH keys are
    downloaded from the azcleanroompublickv Key Vault. Deployment info is written
    to generated/cvm-deploy.json for use by the test script.
.EXAMPLE
    ./src/cvm/tests/deploy-cvm.ps1
.EXAMPLE
    ./src/cvm/tests/deploy-cvm.ps1 -Location eastus2
#>
[CmdletBinding()]
param(
    [string]$Location = "westeurope",

    [string]$OutDir
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

$AdminUser = "azureuser"
$KeyVault = "azcleanroompublickv"
$GeneratedDir = $OutDir ? $OutDir : (Join-Path $PSScriptRoot "generated")

# --- Auto-generate VM and RG names ---
if ($env:GITHUB_ACTIONS -eq "true") {
    # CI mode: derive names from JOB_ID / RUN_ID.
    $jobId = $env:JOB_ID
    $runId = $env:RUN_ID

    $VmName = "cvm-$jobId-$runId"
    # Azure VM names must be <= 64 chars; truncate if needed.
    if ($VmName.Length -gt 64) {
        $VmName = $VmName.Substring(0, 64)
    }
    $ResourceGroup = "rg-cvm-$jobId-$runId"
}
else {
    # Local / Codespaces mode: derive names from the current user.
    if ($env:CODESPACES -eq "true") {
        $user = $env:GITHUB_USER
    }
    else {
        $user = $env:USER ?? "unknown"
    }

    $VmName = "cvm-$user"
    $ResourceGroup = "rg-cvm-$user"
}

Write-Host "=== Deploy Azure Confidential VM ==="
Write-Host "  VM Name:        $VmName"
Write-Host "  Resource Group: $ResourceGroup"
Write-Host "  Location:       $Location"
Write-Host "  Admin User:     $AdminUser"
Write-Host ""

# 1. Create resource group (skip if it already exists).
$rgExists = az group exists --name $ResourceGroup --output tsv
if ($rgExists -eq "true") {
    Write-Host "--- Resource group '$ResourceGroup' already exists, skipping creation ---"
}
else {
    Write-Host "--- Creating resource group ---"
    $rgArgs = @(
        "--name", $ResourceGroup,
        "--location", $Location,
        "--output", "table"
    )
    # When running in GitHub Actions, tag the RG for automated cleanup.
    if ($env:GITHUB_ACTIONS -eq "true") {
        $rgArgs += @("--tags", "github_actions=cvm-$($env:JOB_ID)-$($env:RUN_ID)")
    }
    az group create @rgArgs
}
Write-Host ""

# 2. Download SSH keys from Key Vault.
if (-not (Test-Path $GeneratedDir)) {
    New-Item -ItemType Directory -Path $GeneratedDir -Force | Out-Null
}

$sshPrivateKey = Join-Path $GeneratedDir "cvm-ssh-key.pem"
$sshPublicKey = Join-Path $GeneratedDir "cvm-ssh-key.pub"

if (Test-Path $sshPrivateKey) {
    Write-Host "--- SSH private key already exists at '$sshPrivateKey', skipping download ---"
}
else {
    Write-Host "--- Downloading SSH keys from Key Vault '$KeyVault' ---"
    az keyvault secret show `
        --vault-name $KeyVault `
        --name "flex-node-ssh-private-key" `
        --query "value" -o tsv > $sshPrivateKey
    chmod 600 $sshPrivateKey

    az keyvault secret show `
        --vault-name $KeyVault `
        --name "flex-node-ssh-public-key" `
        --query "value" -o tsv > $sshPublicKey
    Write-Host "  SSH keys downloaded."
}
Write-Host ""

# 3. Create the CVM (skip if it already exists).
$PSNativeCommandUseErrorActionPreference = $false
$vmExists = az vm show `
    --resource-group $ResourceGroup `
    --name $VmName `
    --query "name" -o tsv 2>$null
$PSNativeCommandUseErrorActionPreference = $true
if ($vmExists -eq $VmName) {
    Write-Host "--- VM '$VmName' already exists, skipping creation ---"
}
else {
    Write-Host "--- Creating Confidential VM ---"
    az vm create `
        --resource-group $ResourceGroup `
        --name $VmName `
        --admin-username $AdminUser `
        --size Standard_DC4as_v5 `
        --enable-vtpm true `
        --image "Canonical:0001-com-ubuntu-confidential-vm-jammy:22_04-lts-cvm:22.04.202601280" `
        --public-ip-sku Standard `
        --security-type ConfidentialVM `
        --os-disk-security-encryption-type VMGuestStateOnly `
        --enable-secure-boot true `
        --ssh-key-values $sshPublicKey `
        --output table
}
Write-Host ""

# 4. Get public IP.
Write-Host "--- VM Details ---"
$publicIp = az vm show `
    --resource-group $ResourceGroup `
    --name $VmName `
    --show-details `
    --query publicIps `
    --output tsv

$vmHost = "$AdminUser@$publicIp"
Write-Host "  Public IP: $publicIp"
Write-Host "  SSH:       ssh $vmHost"
Write-Host ""

# 5. Write deployment info to JSON for downstream scripts.
$deployJson = Join-Path $GeneratedDir "cvm-deploy.json"
@{
    vm_host        = $vmHost
    resource_group = $ResourceGroup
    ssh_key        = $sshPrivateKey
} | ConvertTo-Json | Set-Content -Path $deployJson

Write-Host "  Deploy info written to $deployJson"

# 6. Wait for SSH to be ready.
Write-Host "--- Waiting for SSH to be ready ---"
$sshArgs = "-i $sshPrivateKey -o StrictHostKeyChecking=no -o ConnectTimeout=5"
for ($i = 1; $i -le 30; $i++) {
    $result = bash -c "ssh $sshArgs $vmHost 'echo ok' 2>/dev/null"
    if ($result -eq "ok") {
        Write-Host "  SSH is ready."
        break
    }
    if ($i -eq 30) {
        throw "ERROR: SSH not available after 300s"
    }
    Write-Host "  Attempt $i/30 ..."
    Start-Sleep -Seconds 10
}

Write-Host "Done."
