[CmdletBinding()]
param
(
    [Parameter(Mandatory = $true)]
    [string]
    $sshPrivateKeyPath,

    [Parameter(Mandatory = $true)]
    [string]
    $sshPublicKeyPath,

    [string]
    $keyVaultName = "azcleanroompublickv"
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

if (-not (Test-Path $sshPrivateKeyPath)) {
    throw "SSH private key file not found: $sshPrivateKeyPath"
}

if (-not (Test-Path $sshPublicKeyPath)) {
    throw "SSH public key file not found: $sshPublicKeyPath"
}

Write-Host "Uploading SSH private key to Key Vault '$keyVaultName'..."
$privateKeyContent = Get-Content -Raw $sshPrivateKeyPath
az keyvault secret set `
    --vault-name $keyVaultName `
    --name "flex-node-ssh-private-key" `
    --value $privateKeyContent `
    --output none

Write-Host "Uploading SSH public key to Key Vault '$keyVaultName'..."
$publicKeyContent = Get-Content -Raw $sshPublicKeyPath
az keyvault secret set `
    --vault-name $keyVaultName `
    --name "flex-node-ssh-public-key" `
    --value $publicKeyContent `
    --output none

Write-Host -ForegroundColor Green "SSH keys uploaded successfully to Key Vault '$keyVaultName'."
Write-Host "  Private key secret: flex-node-ssh-private-key"
Write-Host "  Public key secret: flex-node-ssh-public-key"
