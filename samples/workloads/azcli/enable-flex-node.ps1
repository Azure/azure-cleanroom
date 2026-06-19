[CmdletBinding()]
param
(
    [string]
    $outDir = "",

    [string]
    $policySigningCertPath = "",

    [int]
    $flexNodeCount = 1,

    [string]
    $flexNodeVmSize = ""
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel

. $root/build/helpers.ps1
Import-Module $root/samples/common/infra-scripts/azure-helpers.psm1 -Force -DisableNameChecking

if ($outDir -eq "") {
    $sandbox_common = "$PSScriptRoot/sandbox_common"
}
else {
    $sandbox_common = $outDir
}

$clCluster = Get-Content $sandbox_common/cl-cluster.json | ConvertFrom-Json
$clusterName = $clCluster.name
$infraType = $clCluster.infraType
$repoConfig = Get-Content $sandbox_common/repoConfig.json | ConvertFrom-Json
$clusterProviderProjectName = $repoConfig.clusterProviderProjectName

if ($policySigningCertPath -eq "") {
    # Generate signing keys and certificate using policy-signing-tool.sh.
    Write-Output "Generating pod policy signing keys..."
    pwsh $PSScriptRoot/generate-signing-keys.ps1 -outDir $sandbox_common
    $signingConfig = Get-Content $sandbox_common/signing-config.json | ConvertFrom-Json
    $policySigningCertPath = $signingConfig.policySigningCertPath
}
else {
    Write-Output "Using provided policy signing cert: $policySigningCertPath"
}

# Generate SSH key pair for flex node VM access if not exists (only for non-virtual infra).
$sshPrivateKeyPath = ""
$sshPublicKeyPath = ""

if ($infraType -ne "virtual") {
    $sshPrivateKeyPath = "$sandbox_common/flex-node-ssh-key.pem"
    $sshPublicKeyPath = "$sandbox_common/flex-node-ssh-key.pub"

    if (-Not (Test-Path $sshPrivateKeyPath)) {
        # Note: SSH key generation logic left as reference.
        # Write-Host "Generating SSH key pair for flex node VM access..."
        # ssh-keygen -t rsa -b 4096 -f "$sandbox_common/flex-node-ssh-key" -N "" -q
        # Move-Item "$sandbox_common/flex-node-ssh-key" $sshPrivateKeyPath
        # chmod 600 $sshPrivateKeyPath
        # Write-Host "SSH key pair generated at: $sshPrivateKeyPath"

        Write-Host "Downloading SSH keys from Key Vault 'azcleanroompublickv'..."
        $privateKeyContent = az keyvault secret show `
            --vault-name "azcleanroompublickv" `
            --name "flex-node-ssh-private-key" `
            --query "value" -o tsv
        $privateKeyContent | Out-File -FilePath $sshPrivateKeyPath
        chmod 600 $sshPrivateKeyPath

        $publicKeyContent = az keyvault secret show `
            --vault-name "azcleanroompublickv" `
            --name "flex-node-ssh-public-key" `
            --query "value" -o tsv
        $publicKeyContent | Out-File -FilePath $sshPublicKeyPath

        Write-Host "SSH keys downloaded from Key Vault."
    }
    else {
        Write-Host "Found existing SSH key pair at: $sshPrivateKeyPath"
    }
}
else {
    Write-Host "Skipping SSH key pair generation for virtual infra type."
}

Write-Output "Enabling flex node on cluster '$clusterName'."

$clusterUpdateArgs = @(
    "--name", $clusterName,
    "--infra-type", $infraType,
    "--enable-flex-node",
    "--flex-node-policy-signing-cert", $policySigningCertPath,
    "--flex-node-count", $flexNodeCount,
    "--provider-config", "$sandbox_common/providerConfig.json",
    "--provider-client", $clusterProviderProjectName
)

if ($flexNodeVmSize -ne "") {
    $clusterUpdateArgs += @("--flex-node-vm-size", $flexNodeVmSize)
}

if ($infraType -ne "virtual") {
    $clusterUpdateArgs += @(
        "--flex-node-ssh-private-key", $sshPrivateKeyPath,
        "--flex-node-ssh-public-key", $sshPublicKeyPath
    )
}

az cleanroom cluster update @clusterUpdateArgs

$response = az cleanroom cluster show `
    --name $clusterName `
    --infra-type $infraType `
    --provider-config $sandbox_common/providerConfig.json `
    --provider-client $clusterProviderProjectName
$clCluster = $response | ConvertFrom-Json

if ($clCluster.flexNodeProfile.enabled -ne $true) {
    throw "Flex node is not enabled on cluster '$clusterName'. flexNodeProfile.enabled is not true."
}

$response | Out-File $sandbox_common/cl-cluster.json

Write-Output "Flex node is enabled."
