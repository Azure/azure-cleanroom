#Requires -Version 7.0

<#
.SYNOPSIS
    Bicep ML Inference Platform Cleanup Script
    
.DESCRIPTION
    This script removes all resources created by the ML inference platform
    
.PARAMETER ResourceGroup
    Resource group name (required)
    
.PARAMETER SubscriptionId
    Azure subscription ID (optional)
    
.PARAMETER Force
    Skip confirmation prompts
    
.PARAMETER Help
    Display help message
    
.EXAMPLE
    .\cleanup.ps1 -ResourceGroup "rg-ml-inference"
    
.EXAMPLE
    .\cleanup.ps1 -ResourceGroup "rg-ml-inference-dev" -Force
    
.EXAMPLE
    .\cleanup.ps1 -ResourceGroup "rg-ml-inference" -SubscriptionId "12345678-1234-1234-1234-123456789012"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, HelpMessage = "Resource group name")]
    [Alias("g")]
    [string]$ResourceGroup,
    
    [Parameter(Mandatory = $false, HelpMessage = "Azure subscription ID")]
    [Alias("s")]
    [string]$SubscriptionId,
    
    [Parameter(Mandatory = $false, HelpMessage = "Skip confirmation prompts")]
    [Alias("f")]
    [switch]$Force,
    
    [Parameter(Mandatory = $false, HelpMessage = "Display help message")]
    [Alias("h")]
    [switch]$Help
)

# Display help if requested
if ($Help) {
    Get-Help $MyInvocation.MyCommand.Definition -Detailed
    exit 0
}

# Set error action preference to stop on errors
$ErrorActionPreference = "Stop"

Write-Host "===========================================" -ForegroundColor Cyan
Write-Host "Bicep ML Inference Platform Cleanup" -ForegroundColor Cyan
Write-Host "===========================================" -ForegroundColor Cyan
Write-Host "Resource Group: $ResourceGroup" -ForegroundColor Green
Write-Host ""

# Check if Azure CLI is installed
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Host "Error: Azure CLI is not installed. Please install it first." -ForegroundColor Red
    exit 1
}

# Check if logged in to Azure
try {
    $null = az account show 2>$null
}
catch {
    Write-Host "Error: Not logged in to Azure. Please run 'az login' first." -ForegroundColor Red
    exit 1
}

# Set subscription if provided
if ($SubscriptionId) {
    Write-Host "Setting subscription to: $SubscriptionId" -ForegroundColor Yellow
    az account set --subscription $SubscriptionId
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Error: Failed to set subscription" -ForegroundColor Red
        exit 1
    }
}

# Display current subscription
$CurrentSubscription = az account show --query "name" -o tsv
$CurrentSubscriptionId = az account show --query "id" -o tsv
Write-Host "Current subscription: $CurrentSubscription ($CurrentSubscriptionId)" -ForegroundColor Green
Write-Host ""

# Check if resource group exists
try {
    $null = az group show --name $ResourceGroup 2>$null
}
catch {
    Write-Host "Resource group '$ResourceGroup' does not exist." -ForegroundColor Yellow
    exit 0
}

# List resources in the resource group
Write-Host "Resources in resource group '$ResourceGroup':" -ForegroundColor Yellow
az resource list --resource-group $ResourceGroup --query "[].{Name:name, Type:type, Location:location}" --output table
Write-Host ""

# Get AKS cluster name if it exists
try {
    $AksClusterName = az aks list --resource-group $ResourceGroup --query "[0].name" -o tsv 2>$null
    if ($AksClusterName -and $AksClusterName -ne "null" -and $AksClusterName.Trim() -ne "") {
        Write-Host "Found AKS cluster: $AksClusterName" -ForegroundColor Green
        Write-Host ""
        
        # Offer to show Kubernetes resources before deletion
        if (-not $Force) {
            $showK8sResources = Read-Host "Do you want to see Kubernetes resources before deletion? (y/N)"
            if ($showK8sResources -match '^[Yy]$') {
                Write-Host "Getting AKS credentials..." -ForegroundColor Yellow
                az aks get-credentials --resource-group $ResourceGroup --name $AksClusterName --overwrite-existing
                if ($LASTEXITCODE -eq 0) {
                    Write-Host ""
                    Write-Host "Kubernetes namespaces:" -ForegroundColor Cyan
                    kubectl get namespaces
                    Write-Host ""
                    Write-Host "All pods:" -ForegroundColor Cyan
                    kubectl get pods --all-namespaces
                    Write-Host ""
                }
            }
        }
    }
}
catch {
    # No AKS cluster found or error occurred - continue with cleanup
}

# Confirmation prompt
if (-not $Force) {
    Write-Host "⚠️  WARNING: This will DELETE ALL RESOURCES in the resource group!" -ForegroundColor Red
    Write-Host "This action cannot be undone." -ForegroundColor Red
    Write-Host ""
    
    $confirmation = Read-Host "Are you sure you want to delete resource group '$ResourceGroup'? (y/N)"
    if ($confirmation -notmatch '^[Yy]$') {
        Write-Host "Cleanup cancelled." -ForegroundColor Yellow
        exit 0
    }
    Write-Host ""
}

# Delete the resource group
Write-Host "Deleting resource group '$ResourceGroup'..." -ForegroundColor Yellow
Write-Host "This may take several minutes..." -ForegroundColor Yellow

az group delete --name $ResourceGroup --yes

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "=========================================" -ForegroundColor Green
    Write-Host "Cleanup initiated successfully! 🗑️" -ForegroundColor Green
    Write-Host "=========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "Resource group '$ResourceGroup' is being deleted in the background." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "To check the deletion status:" -ForegroundColor Cyan
    Write-Host "az group show --name $ResourceGroup" -ForegroundColor White
    Write-Host ""
    Write-Host "Note: The deletion process may take 10-15 minutes to complete." -ForegroundColor Yellow
    Write-Host "You can continue with other tasks while the deletion runs in the background." -ForegroundColor Yellow
}
else {
    Write-Host ""
    Write-Host "=========================================" -ForegroundColor Red
    Write-Host "Cleanup failed! ❌" -ForegroundColor Red
    Write-Host "=========================================" -ForegroundColor Red
    Write-Host ""
    Write-Host "Please check your permissions and try again." -ForegroundColor Yellow
    Write-Host "You can also delete the resource group manually from the Azure portal." -ForegroundColor Yellow
    Write-Host ""
    exit 1
}