#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Sets up Azure Workload Identity for Radius on AKS.

.DESCRIPTION
    This script configures Azure Workload Identity integration for Radius, including:
    - Creating a managed identity for Azure provider
    - Assigning necessary Azure RBAC permissions  
    - Creating federated identity credentials for Radius service accounts
    - Registering the Azure provider with Radius

.PARAMETER ControlPlaneResourceGroup
    The resource group name where the control plane AKS cluster is deployed

.PARAMETER ControlPlaneCluster
    The AKS cluster name for the Radius control plane

.PARAMETER ManagedIdentity
    The name for the managed identity to create (default: radius-azure-identity)

.EXAMPLE
    ./setup-radius-wi.ps1 -ControlPlaneResourceGroup "radius-control-plane-myuser" -ControlPlaneCluster "radius-control-plane"
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$ControlPlaneResourceGroup,
    
    [Parameter(Mandatory = $true)]
    [string]$ControlPlaneCluster,
    
    [Parameter(Mandatory = $false)]
    [string]$ManagedIdentity = "radius-azure-identity"
)

# Set error action preference
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

Write-Host "Setting up Azure Workload Identity for Radius..." -ForegroundColor Green
Write-Host "Control Plane Resource Group: $ControlPlaneResourceGroup" -ForegroundColor Cyan
Write-Host "Control Plane Cluster: $ControlPlaneCluster" -ForegroundColor Cyan
Write-Host "Managed Identity Name: $ManagedIdentity" -ForegroundColor Cyan

# Step 1: Create managed identity for Azure provider
Write-Host "`n=== Creating Managed Identity ===" -ForegroundColor Yellow
Write-Host "Creating managed identity '$ManagedIdentity'..." -ForegroundColor Blue

az identity create --name $ManagedIdentity --resource-group $ControlPlaneResourceGroup
Write-Host "✓ Successfully created managed identity" -ForegroundColor Green

# Step 2: Get the managed identity details
Write-Host "`n=== Getting Identity Details ===" -ForegroundColor Yellow
Write-Host "Retrieving managed identity details..." -ForegroundColor Blue

$IDENTITY_CLIENT_ID = az identity show --name $ManagedIdentity --resource-group $ControlPlaneResourceGroup --query clientId -o tsv
$IDENTITY_OBJECT_ID = az identity show --name $ManagedIdentity --resource-group $ControlPlaneResourceGroup --query principalId -o tsv
    
Write-Host "✓ Client ID: $IDENTITY_CLIENT_ID" -ForegroundColor Green
Write-Host "✓ Object ID: $IDENTITY_OBJECT_ID" -ForegroundColor Green

# Step 3: Assign Contributor role to the managed identity for target subscription
Write-Host "`n=== Assigning Azure RBAC Permissions ===" -ForegroundColor Yellow
Write-Host "Assigning Contributor role to managed identity..." -ForegroundColor Blue

$SUBSCRIPTION_ID = az account show --query id -o tsv
$maxRetries = 5
$retryCount = 0
$roleAssignmentSuccess = $false

while ($retryCount -lt $maxRetries -and -not $roleAssignmentSuccess) {
    $retryCount++
    
    try {
        Write-Host "Attempt $retryCount of $maxRetries..." -ForegroundColor Cyan
        
        az role assignment create `
            --assignee $IDENTITY_OBJECT_ID `
            --role Contributor `
            --scope /subscriptions/$SUBSCRIPTION_ID
        
        $roleAssignmentSuccess = $true
        Write-Host "✓ Successfully assigned Contributor role" -ForegroundColor Green
        Write-Host "✓ Subscription: $SUBSCRIPTION_ID" -ForegroundColor Green
    }
    catch {
        Write-Host "Role assignment attempt $retryCount failed: $_" -ForegroundColor Yellow
        
        if ($retryCount -lt $maxRetries) {
            Write-Host "Waiting 10 seconds before retry..." -ForegroundColor Yellow
            Start-Sleep -Seconds 10
        }
        else {
            throw "Failed to assign RBAC permissions after $maxRetries attempts: $_"
        }
    }
}

# Step 4: Get OIDC issuer URL from the AKS cluster
Write-Host "`n=== Getting OIDC Issuer URL ===" -ForegroundColor Yellow
Write-Host "Retrieving OIDC issuer URL from AKS cluster..." -ForegroundColor Blue

$OIDC_ISSUER_URL = az aks show --resource-group $ControlPlaneResourceGroup --name $ControlPlaneCluster --query oidcIssuerProfile.issuerUrl -o tsv
Write-Host "✓ OIDC Issuer URL: $OIDC_ISSUER_URL" -ForegroundColor Green

# Step 5: Create federated identity credentials for Radius service accounts
Write-Host "`n=== Creating Federated Identity Credentials ===" -ForegroundColor Yellow

$serviceAccounts = @(
    @{ Name = "radius-applications-rp"; Subject = "system:serviceaccount:radius-system:applications-rp" },
    @{ Name = "radius-bicep-de"; Subject = "system:serviceaccount:radius-system:bicep-de" },
    @{ Name = "radius-ucp"; Subject = "system:serviceaccount:radius-system:ucp" }
)

foreach ($sa in $serviceAccounts) {
    Write-Host "Creating federated credential for $($sa.Name)..." -ForegroundColor Blue
    
    az identity federated-credential create `
        --name $sa.Name `
        --identity-name $ManagedIdentity `
        --resource-group $ControlPlaneResourceGroup `
        --issuer $OIDC_ISSUER_URL `
        --subject $sa.Subject `
        --audience api://AzureADTokenExchange
        
    Write-Host "✓ Successfully created federated credential for $($sa.Name)" -ForegroundColor Green
}

# Step 6: Register Azure provider with managed identity
Write-Host "`n=== Registering Azure Provider with Radius ===" -ForegroundColor Yellow
Write-Host "Registering Azure provider with Workload Identity..." -ForegroundColor Blue

$TENANT_ID = az account show --query tenantId -o tsv
rad credential register azure wi --client-id $IDENTITY_CLIENT_ID --tenant-id $TENANT_ID
    
Write-Host "✓ Successfully registered Azure provider" -ForegroundColor Green
Write-Host "✓ Tenant ID: $TENANT_ID" -ForegroundColor Green

# Step 7: Verify Azure provider is registered
Write-Host "`n=== Verifying Configuration ===" -ForegroundColor Yellow
Write-Host "Verifying Azure provider registration..." -ForegroundColor Blue

Write-Host "Current Radius credentials:" -ForegroundColor Cyan
rad credential list
Write-Host "✓ Configuration verification complete" -ForegroundColor Green

Write-Host "`n=== Azure Workload Identity Setup Complete ===" -ForegroundColor Green
Write-Host "Managed Identity '$ManagedIdentity' has been configured for Radius." -ForegroundColor Cyan
Write-Host "Azure provider is registered and ready to use." -ForegroundColor Cyan