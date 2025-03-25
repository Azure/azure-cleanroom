function Create-Hsm {
    param(
        [string]$resourceGroup,

        [string]$hsmName,

        [string]$adminObjectId,

        [string]$outDir
    )

    Write-Host "Creating MHSM $hsmName in resource group $resourceGroup with $adminObjectId as administrator"
    $mhsmResult = (az keyvault create --resource-group $resourceGroup --hsm-name $hsmName --retention-days 90 --administrators $adminObjectId) | ConvertFrom-Json

    if ($mhsmResult.properties.securityDomainProperties.activationStatus -ne "Active") {
        openssl req -newkey rsa:2048 -nodes -keyout $outDir/cert_0.key -x509 -days 365 -out $outDir/cert_0.cer -subj "/C=US/CN=Microsoft"
        openssl req -newkey rsa:2048 -nodes -keyout $outDir/cert_1.key -x509 -days 365 -out $outDir/cert_1.cer -subj "/C=US/CN=Microsoft"
        openssl req -newkey rsa:2048 -nodes -keyout $outDir/cert_2.key -x509 -days 365 -out $outDir/cert_2.cer -subj "/C=US/CN=Microsoft"

        Write-Host "Activating HSM"
        $activationResult = az keyvault security-domain download --hsm-name $hsmName --sd-wrapping-keys $outDir/cert_0.cer $outDir/cert_1.cer $outDir/cert_2.cer --sd-quorum 2 --security-domain-file "securitydomain$hsmName.json"

        Write-Host "Assigning permissions to object ID $adminObjectId"
        $roleAssignment = az keyvault role assignment create --role "Managed HSM Crypto Officer" --scope "/" --assignee-object-id $adminObjectId --hsm-name $hsmName
        $roleAssignment = az keyvault role assignment create --role "Managed HSM Crypto User" --scope "/" --assignee-object-id $adminObjectId --hsm-name $hsmName
    }
    else {
        Write-Host "HSM is already active"
    }

    return $mhsmResult
}

function Create-KeyVault {
    param(
        [string]$resourceGroup,
        [string]$keyVaultName,
        [string]$adminObjectId,
        [string]$sku = "standard"
    )

    Write-Host "Creating $sku Key Vault $keyVaultName in resource group $resourceGroup"
    $keyVaultResult = $null
    & {
        # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
        $PSNativeCommandUseErrorActionPreference = $false
        $keyVaultResult = (az keyvault create --resource-group $resourceGroup --name $keyVaultName --sku $sku --enable-rbac-authorization true --enable-purge-protection true) | ConvertFrom-Json
    }

    # When the Key Vault already exists, $keyVaultResult will be null. In such cases, we try to pick the pre-existing Key Vault
    if ($null -eq $keyVaultResult) {
        $keyVaultResult = (az keyvault show --name $keyVaultName --resource-group $resourceGroup) | ConvertFrom-Json
    }

    Write-Host "Assigning 'Key Vault Administrator' permissions to $adminObjectId on Key Vault $($keyVaultResult.id)"
    $role = az role assignment create --role "Key Vault Administrator" --scope $keyVaultResult.id --assignee-object-id $adminObjectId --assignee-principal-type $(Get-Assignee-Principal-Type)

    return $keyVaultResult
}

function Get-Assignee-Principal-Type {
    if ($env:GITHUB_ACTIONS -eq "true") {
        return "ServicePrincipal"
    }
    else {
        return "User"
    }
}