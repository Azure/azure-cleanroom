#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publishes Bicep recipes to Azure Container Registry and registers them with Radius.

.DESCRIPTION
    This script publishes all Radius recipes to an Azure Container Registry as OCI artifacts
    and then registers them with the Radius environment for use in deployments.

.PARAMETER AcrLoginServer
    The login server URL for the Azure Container Registry (e.g., myregistry.azurecr.io)

.PARAMETER RecipeVersion
    The version tag to use for the recipe artifacts (default: v1.0.0)

.PARAMETER Environment
    The Radius environment name to register recipes with (default: aks-prod)

.EXAMPLE
    ./publish-and-register-recipes.ps1 -AcrLoginServer "myregistry.azurecr.io" -RecipeVersion "v1.1.0"
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$AcrLoginServer,
    
    [Parameter(Mandatory = $false)]
    [string]$RecipeVersion = "1.0.0",
    
    [Parameter(Mandatory = $false)]
    [string]$Environment = "aks-prod"
)

# Set error action preference
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

# Define recipe names
$recipeNames = @(
    "aks-cluster-recipe",
    "managed-identity-recipe",
    "helm-chart-recipe",
    "kubectl-wait-recipe",
    "cert-manager-recipe", 
    "keda-recipe",
    "envoy-gateway-recipe",
    "gateway-api-recipe",
    "kserve-recipe"
)

Write-Host "Starting recipe publishing and registration process..." -ForegroundColor Green
Write-Host "ACR Login Server: $AcrLoginServer" -ForegroundColor Cyan
Write-Host "Recipe Version: $RecipeVersion" -ForegroundColor Cyan
Write-Host "Target Environment: $Environment" -ForegroundColor Cyan

# Step 1: Publish recipes to ACR
Write-Host "`n=== Publishing Recipes to ACR ===" -ForegroundColor Yellow

foreach ($recipeName in $recipeNames) {
    Write-Host "Publishing $recipeName to ACR..." -ForegroundColor Blue
    
    # Check if the recipe bicep file exists directly in the recipes folder
    $bicepFile = "./recipes/$recipeName.bicep"
    if (!(Test-Path $bicepFile)) {
        Write-Error "Bicep file not found: $bicepFile"
        continue
    }
    
    # Publish the recipe bicep file as a Bicep module to ACR
    try {
        rad bicep publish --file $bicepFile --target "br:$AcrLoginServer/recipes/$recipeName`:$RecipeVersion"
        Write-Host "✓ Successfully published $recipeName" -ForegroundColor Green
    }
    catch {
        Write-Error "Failed to publish $recipeName`: $_"
    }
}

Write-Host "`nAll recipes published to ACR: $AcrLoginServer" -ForegroundColor Green

# Step 2: Register recipes with Radius environment
Write-Host "`n=== Registering Recipes with Radius Environment ===" -ForegroundColor Yellow

# Recipe registration mapping (recipe name -> resource type)
$recipeRegistrations = @{
    "aks-cluster-recipe"      = "Applications.Core/extenders"
    "managed-identity-recipe" = "Applications.Core/extenders"
    "helm-chart-recipe"       = "Applications.Core/extenders"
    "kubectl-wait-recipe"     = "Applications.Core/extenders"
    "cert-manager-recipe"     = "Applications.Core/extenders"
    "keda-recipe"             = "Applications.Core/extenders"
    "envoy-gateway-recipe"    = "Applications.Core/extenders"
    "gateway-api-recipe"      = "Applications.Core/extenders"
    "kserve-recipe"           = "Applications.Core/extenders"
}

# Register individual recipes with proper names
$registrationCommands = @{
    "aks-cluster-recipe"      = "aks-cluster-recipe"
    "managed-identity-recipe" = "managed-identity-recipe"
    "helm-chart-recipe"       = "helm-chart-recipe"
    "kubectl-wait-recipe"     = "kubectl-wait-recipe"
    "cert-manager-recipe"     = "cert-manager-recipe"
    "keda-recipe"             = "keda-recipe"
    "envoy-gateway-recipe"    = "envoy-gateway-recipe"
    "gateway-api-recipe"      = "gateway-api-recipe"
    "kserve-recipe"           = "kserve-recipe"
}

foreach ($recipeName in $recipeNames) {
    $registrationName = $registrationCommands[$recipeName]
    $resourceType = $recipeRegistrations[$recipeName]
    $templatePath = "$AcrLoginServer/recipes/$recipeName`:$RecipeVersion"
    
    Write-Host "Registering $registrationName recipe..." -ForegroundColor Blue
    
    rad recipe register $registrationName --template-kind bicep --template-path $templatePath --resource-type $resourceType --environment $Environment
    Write-Host "✓ Successfully registered $registrationName" -ForegroundColor Green
}

Write-Host "`n=== Recipe Publishing and Registration Complete ===" -ForegroundColor Green
Write-Host "All recipes have been published to ACR and registered with Radius environment '$Environment'." -ForegroundColor Cyan
Write-Host "You can now deploy applications using these recipes." -ForegroundColor Cyan