param(
    [Parameter(Mandatory = $true)]
    [string]$managedIdentityResourceGroup,

    [Parameter(Mandatory = $true)]
    [string]$managedIdentityName,

    [Parameter(Mandatory = $true)]
    [string]$storageAccountName,

    [Parameter(Mandatory = $true)]
    [string]$storageAccountResourceGroup,

    [Parameter(Mandatory = $true)]
    [string]$governanceClient,

    [Parameter(Mandatory = $true)]
    [string]$subject,

    [Parameter(Mandatory = $true)]
    [string]$issuerUrl,

    [string]$outDir = "$PSScriptRoot/generated"
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# Look up the managed identity's principal ID.
$mi = (az identity show `
        --name $managedIdentityName `
        --resource-group $managedIdentityResourceGroup) | ConvertFrom-Json
$principalId = $mi.principalId

# Assign Storage Blob Data Contributor on the storage account if not already assigned.
$storageAccount = (az storage account show `
        --name $storageAccountName `
        --resource-group $storageAccountResourceGroup) | ConvertFrom-Json

$role = "Storage Blob Data Contributor"
$roleAssignment = (az role assignment list `
        --assignee-object-id $principalId `
        --scope $storageAccount.id `
        --role $role `
        --fill-principal-name false `
        --fill-role-definition-name false) | ConvertFrom-Json

if ($roleAssignment.Length -eq 1) {
    Write-Host "'$role' permission on the storage account already exists, skipping assignment."
}
else {
    Write-Host "Assigning '$role' on the storage account..."
    az role assignment create `
        --role $role `
        --scope $storageAccount.id `
        --assignee-object-id $principalId `
        --assignee-principal-type ServicePrincipal
}

# Set up federated credential on the managed identity.
Write-Host "Setting up federation on managed identity '$managedIdentityName' with issuerUrl '$issuerUrl' and subject '$subject'..."
$fedCred = (az identity federated-credential create `
        --name "$subject-federation" `
        --identity-name $managedIdentityName `
        --resource-group $managedIdentityResourceGroup `
        --issuer $issuerUrl `
        --subject $subject `
        --audiences "api://AzureADTokenExchange") | ConvertFrom-Json

if ($env:GITHUB_ACTIONS -eq "true") {
    $sleepTime = 30
    # See Note at https://learn.microsoft.com/en-us/azure/aks/workload-identity-deploy-cluster#create-the-federated-identity-credential
    Write-Host "Waiting for $sleepTime seconds for federated identity credential to propagate after it is added."
    Start-Sleep -Seconds $sleepTime
}
