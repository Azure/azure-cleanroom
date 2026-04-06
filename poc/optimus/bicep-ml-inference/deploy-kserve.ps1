#Requires -Version 7.0

<#
.SYNOPSIS
    Bicep ML Inference Platform Deployment Script
    
.DESCRIPTION
    This script deploys the ML inference platform using pure Bicep templates
    
.PARAMETER ResourceGroup
    Resource group name (required)
    
.PARAMETER SubscriptionId
    Azure subscription ID (optional)
    
.PARAMETER Location
    Azure region (default: westeurope)
    
.PARAMETER EnablePrometheus
    Enable Prometheus monitoring stack - Prometheus and Grafana (default: false)
    
.PARAMETER ForceUpdateTag
    Force update tag to ensure deployment scripts are re-run (optional, defaults to current UTC timestamp)
    
.PARAMETER Help
    Display help message
    
.EXAMPLE
    .\deploy-kserve.ps1 -ResourceGroup "rg-ml-inference"
    
.EXAMPLE
    .\deploy-kserve.ps1 -ResourceGroup "rg-ml-inference" -SubscriptionId "12345678-1234-1234-1234-123456789012" -Location "westeurope"
    
.EXAMPLE
    .\deploy-kserve.ps1 -ResourceGroup "rg-ml-inference"
    
.EXAMPLE
    .\deploy-kserve.ps1 -ResourceGroup "rg-ml-inference" -EnablePrometheus $true
    
.EXAMPLE
    .\deploy-kserve.ps1 -ResourceGroup "rg-ml-inference" -ForceUpdateTag "20241011120000"
    

#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false, HelpMessage = "Resource group name")]
    [Alias("g")]
    [string]$ResourceGroup = "ml-inference-platform-$env:USER",
    
    [Parameter(Mandatory = $false, HelpMessage = "Azure subscription ID")]
    [Alias("s")]
    [string]$SubscriptionId,
    
    [Parameter(Mandatory = $false, HelpMessage = "Azure region")]
    [Alias("l")]
    [string]$Location = "westeurope",
    
    [Parameter(Mandatory = $false, HelpMessage = "Enable Prometheus monitoring stack (Prometheus + Grafana)")]
    [bool]$EnablePrometheus = $false,
    
    [Parameter(Mandatory = $false, HelpMessage = "Force update tag to ensure deployment scripts are re-run")]
    [string]$ForceUpdateTag = (Get-Date -Format "yyyyMMddHHmmss"),
    
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
$PSNativeCommandUseErrorActionPreference = $true

# Generate deployment name with timestamp
$DeploymentName = "ml-inference-platform-$(Get-Date -Format 'yyyyMMdd-HHmmss')"

# Set parameters file for production deployment  
$ParametersFile = "$PSScriptRoot/parameters/deploy-kserve.parameters.json"

Write-Host "===========================================" -ForegroundColor Cyan
Write-Host "Bicep ML Inference Platform Deployment" -ForegroundColor Cyan
Write-Host "===========================================" -ForegroundColor Cyan
Write-Host "Resource Group: $ResourceGroup" -ForegroundColor Green
Write-Host "Location: $Location" -ForegroundColor Green
Write-Host "Parameters File: $ParametersFile" -ForegroundColor Green
Write-Host "Deployment Name: $DeploymentName" -ForegroundColor Green
Write-Host ""

# Check if Azure CLI is installed
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Host "Error: Azure CLI is not installed. Please install it first." -ForegroundColor Red
    Write-Host "Visit: https://docs.microsoft.com/en-us/cli/azure/install-azure-cli" -ForegroundColor Yellow
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
}

# Display current subscription
$CurrentSubscription = az account show --query "name" -o tsv
$CurrentSubscriptionId = az account show --query "id" -o tsv
Write-Host "Current subscription: $CurrentSubscription ($CurrentSubscriptionId)" -ForegroundColor Green
Write-Host ""

# Create resource group if it doesn't exist
Write-Host "Checking resource group..." -ForegroundColor Yellow
try {
    $null = az group show --name $ResourceGroup 2>$null
    Write-Host "Resource group already exists: $ResourceGroup" -ForegroundColor Green
}
catch {
    Write-Host "Creating resource group: $ResourceGroup" -ForegroundColor Yellow
    az group create --name $ResourceGroup --location $Location
}
Write-Host ""

# Check if parameters file exists
if (-not (Test-Path $ParametersFile)) {
    Write-Host "Error: Parameters file not found: $ParametersFile" -ForegroundColor Red
    exit 1
}

# Validate Bicep template
Write-Host "Validating Bicep template..." -ForegroundColor Yellow
az deployment group validate `
    --resource-group $ResourceGroup `
    --template-file $PSScriptRoot/deploy-kserve.bicep `
    --parameters "@$ParametersFile" `
    --parameters location=$Location `
    --parameters enablePrometheus=$EnablePrometheus `
    --parameters forceUpdateTag=$ForceUpdateTag
Write-Host "✓ Template validation successful" -ForegroundColor Green
Write-Host ""

# Ready to deploy
Write-Host "Ready to deploy the ML inference platform." -ForegroundColor Cyan
Write-Host "This will create AKS cluster and deploy multiple Helm charts." -ForegroundColor Yellow
Write-Host "The deployment may take 15-20 minutes to complete." -ForegroundColor Yellow
Write-Host ""

# Deploy the template
Write-Host "Starting deployment..." -ForegroundColor Cyan
Write-Host "Deployment name: $DeploymentName" -ForegroundColor Green
Write-Host ""

& {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
    $PSNativeCommandUseErrorActionPreference = $false
    az deployment group create `
        --resource-group $ResourceGroup `
        --name $DeploymentName `
        --template-file $PSScriptRoot/deploy-kserve.bicep `
        --parameters "@$ParametersFile" `
        --parameters location=$Location `
        --parameters enablePrometheus=$EnablePrometheus `
        --parameters forceUpdateTag=$ForceUpdateTag `
        --no-wait `
        --verbose
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host ""
        Write-Host "=========================================" -ForegroundColor Green
        Write-Host "Deployment started successfully! 🚀" -ForegroundColor Green
        Write-Host "=========================================" -ForegroundColor Green
        Write-Host "Tracking deployment progress..." -ForegroundColor Cyan
        Write-Host ""
        
        # Track deployment progress
        $deploymentComplete = $false
        $lastState = @{}
        
        while (-not $deploymentComplete) {
            try {
                # Get deployment operations
                $operations = az deployment operation group list `
                    --resource-group $ResourceGroup `
                    --name $DeploymentName `
                    --query "[].{Resource:properties.targetResource.resourceName, State:properties.provisioningState, Type:properties.targetResource.resourceType}" `
                    --output json | ConvertFrom-Json
                
                if ($operations) {
                    # Check for state changes and display updates
                    $currentTime = Get-Date -Format "HH:mm:ss"
                    
                    foreach ($op in $operations) {
                        $resourceKey = "$($op.Resource)-$($op.Type)"
                        if (-not $lastState.ContainsKey($resourceKey) -or $lastState[$resourceKey] -ne $op.State) {
                            $lastState[$resourceKey] = $op.State
                            
                            $color = switch ($op.State) {
                                "Running" { "Yellow" }
                                "Succeeded" { "Green" }
                                "Failed" { "Red" }
                                "Canceled" { "Red" }
                                default { "White" }
                            }
                            
                            Write-Host "[$currentTime] $($op.Resource) ($($op.Type)): $($op.State)" -ForegroundColor $color
                        }
                    }
                    
                    # Check if deployment is complete
                    $mainDeployment = az deployment group show `
                        --resource-group $ResourceGroup `
                        --name $DeploymentName `
                        --query "properties.provisioningState" `
                        --output tsv
                    
                    if ($mainDeployment -in @("Succeeded", "Failed", "Canceled")) {
                        $deploymentComplete = $true
                        Write-Host ""
                        if ($mainDeployment -eq "Succeeded") {
                            Write-Host "=========================================" -ForegroundColor Green
                            Write-Host "Deployment completed successfully! 🎉" -ForegroundColor Green
                            Write-Host "=========================================" -ForegroundColor Green
                        }
                        else {
                            Write-Host "=========================================" -ForegroundColor Red
                            Write-Host "Deployment $mainDeployment! ❌" -ForegroundColor Red
                            Write-Host "=========================================" -ForegroundColor Red
                            Write-Host ""
                            Write-Host "⚠️  Quick troubleshooting commands:" -ForegroundColor Yellow
                            Write-Host "   az deployment operation group list --resource-group $ResourceGroup --name $DeploymentName --query `"[?properties.provisioningState=='Failed']`" -o table" -ForegroundColor White
                            Write-Host "   az deployment-scripts list --resource-group $ResourceGroup --query `"[].{Name:name, Status:provisioningState}`" -o table" -ForegroundColor White
                            Write-Host ""
                        }
                    }
                }
                
                if (-not $deploymentComplete) {
                    Start-Sleep -Seconds 10
                }
                
            }
            catch {
                Write-Host "Error tracking deployment: $($_.Exception.Message)" -ForegroundColor Red
                Start-Sleep -Seconds 10
            }
        }
        
        # Final deployment status check
        $finalStatus = az deployment group show `
            --resource-group $ResourceGroup `
            --name $DeploymentName `
            --query "properties.provisioningState" `
            --output tsv
            
        if ($finalStatus -ne "Succeeded") {
            Write-Host ""
            Write-Host "=========================================" -ForegroundColor Red
            Write-Host "Deployment Failed - Troubleshooting Guide" -ForegroundColor Red
            Write-Host "=========================================" -ForegroundColor Red
            Write-Host "Deployment failed with status: $finalStatus" -ForegroundColor Red
            Write-Host ""
            
            Write-Host "🔍 Troubleshooting Steps:" -ForegroundColor Yellow
            Write-Host ""
            
            Write-Host "1. Check overall deployment details:" -ForegroundColor Cyan
            Write-Host "   az deployment group show --resource-group $ResourceGroup --name $DeploymentName" -ForegroundColor White
            Write-Host ""
            
            Write-Host "2. List all deployment operations:" -ForegroundColor Cyan
            Write-Host "   az deployment operation group list --resource-group $ResourceGroup --name $DeploymentName --query `"[?properties.provisioningState=='Failed'].{Resource:properties.targetResource.resourceName, Error:properties.statusMessage.error.message}`" -o table" -ForegroundColor White
            Write-Host ""
            
            Write-Host "3. Check failed deployment scripts logs:" -ForegroundColor Cyan
            Write-Host "   # List deployment scripts:" -ForegroundColor Gray
            Write-Host "   az deployment-scripts list --resource-group $ResourceGroup --query `"[].{Name:name, Status:provisioningState}`" -o table" -ForegroundColor White
            Write-Host "   # View script logs (replace <script-name> with actual name):" -ForegroundColor Gray
            Write-Host "   az deployment-scripts show-log --resource-group $ResourceGroup --name <script-name>" -ForegroundColor White
            Write-Host ""
            
            Write-Host "4. Check AKS cluster status (if created):" -ForegroundColor Cyan
            Write-Host "   az aks list --resource-group $ResourceGroup --query `"[].{Name:name, Status:provisioningState, PowerState:powerState.code}`" -o table" -ForegroundColor White
            Write-Host ""
            
            Write-Host "5. Inspect specific resource failures:" -ForegroundColor Cyan
            Write-Host "   # Get all resources in the resource group:" -ForegroundColor Gray
            Write-Host "   az resource list --resource-group $ResourceGroup --query `"[].{Name:name, Type:type, Status:provisioningState}`" -o table" -ForegroundColor White
            Write-Host ""
            
            $ErrorActionPreference = "Continue"
            exit 1
        }
    
        # Get deployment outputs
        Write-Host "Retrieving deployment outputs..." -ForegroundColor Yellow
        $AksClusterName = az deployment group show --resource-group $ResourceGroup --name $DeploymentName --query "properties.outputs.aksClusterName.value" -o tsv
        $AksClusterFqdn = az deployment group show --resource-group $ResourceGroup --name $DeploymentName --query "properties.outputs.aksClusterFqdn.value" -o tsv
    
        Write-Host ""
        Write-Host "Deployment Summary:" -ForegroundColor Cyan
        Write-Host "- Resource Group: $ResourceGroup" -ForegroundColor Green
        Write-Host "- AKS Cluster: $AksClusterName" -ForegroundColor Green
        Write-Host "- AKS FQDN: $AksClusterFqdn" -ForegroundColor Green
        Write-Host ""
    
        Write-Host "Next steps:" -ForegroundColor Cyan
        Write-Host "1. Get AKS credentials:" -ForegroundColor Yellow
        Write-Host "   az aks get-credentials --resource-group $ResourceGroup --name $AksClusterName" -ForegroundColor White
        Write-Host ""
        Write-Host "2. Test ML model inference:" -ForegroundColor Yellow
        Write-Host "   ./deploy-iris.ps1" -ForegroundColor White
        Write-Host ""
    
    }
    else {
        Write-Host ""
        Write-Host "=========================================" -ForegroundColor Red
        Write-Host "Failed to start deployment! ❌" -ForegroundColor Red
        Write-Host "=========================================" -ForegroundColor Red
        Write-Host ""
        Write-Host "Check the deployment command output above for details." -ForegroundColor Yellow
        exit 1
    }
}