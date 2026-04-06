# Radius ML Inference Platform
Deploy KServe (standard mode) + Envoy Gateway + KEDA + Prometheus
using [Radius](https://radapp.io).

> ⚠️ **Work in Progress** - This implementation is currently under development and may not be fully functional. For a stable version, please use the [bicep-ml-inference](../bicep-ml-inference/) implementation instead.

## Installing Radius

### Prerequisites
- `kubectl` CLI installed and configured
- Azure CLI (`az`) installed and logged in
- All steps are written assuming `pwsh` shell for Linux is installed ([installation instructions](https://docs.microsoft.com/en-us/powershell/scripting/install/installing-powershell-core-on-linux))

### Install Radius CLI

```powershell
wget -q "https://raw.githubusercontent.com/radius-project/radius/main/deploy/install.sh" -O - | /bin/bash
```

**Verify installation:**
```powershell
rad version
```

### Initialize Radius Control Plane
The radius control plane needs to be installed on a Kubernetes cluster. For this we'd setup an AKS cluster and then install the control plane components on it.

**For AKS with Workload Identity:**
```powershell
az login
```

```powershell
# Set variables for resource naming and location
$AZURE_LOCATION = "westeurope"  # Change to your preferred Azure region
$CONTROL_PLANE_RG = "radius-control-plane-$env:USER"
$CONTROL_PLANE_CLUSTER = "radius-control-plane"

# Create AKS cluster with workload identity and OIDC issuer
az group create --name $CONTROL_PLANE_RG --location $AZURE_LOCATION
az aks create `
  --resource-group $CONTROL_PLANE_RG `
  --name $CONTROL_PLANE_CLUSTER `
  --node-vm-size Standard_D4ds_v5 `
  --tier Standard `
  --enable-oidc-issuer `
  --enable-workload-identity

# Get credentials and install Radius
az aks get-credentials --resource-group $CONTROL_PLANE_RG --name $CONTROL_PLANE_CLUSTER
rad install kubernetes --set global.azureWorkloadIdentity.enabled=true
```

## Azure Workload Identity Setup

For AKS deployments, this setup uses Azure Workload Identity to authenticate Radius with Azure without storing secrets. The Radius controller runs with a Kubernetes service account that is federated with an Azure managed identity.

### Key Components:
- **AKS Cluster**: Must have OIDC issuer and workload identity enabled
- **Managed Identity**: Azure identity with Contributor permissions on target subscription
- **Federated Identity Credential**: Links the Kubernetes service account to the managed identity
- **Service Account Annotations**: Enables workload identity for the Radius controller
- **Environment Configuration**: Radius environment must be configured with target subscription and resource group

### Azure Provider Configuration:
Radius needs to know:
- **Subscription ID**: Which Azure subscription to deploy resources into
- **Resource Group**: Which resource group to use for the target AKS cluster
- **Region**: Azure region for resource deployment (parameterized in environment file)
- **Credentials**: Managed identity for authentication (configured via workload identity)

### Location Configuration:
The Azure region is parameterized in both the README commands and the `aks-prod` environment. Change the `$AZURE_LOCATION` variable to deploy to your preferred region (e.g., `eastus`, `westus2`, `northeurope`, etc.).

## Setup

### Setup WI for radius on AKS
```powershell
# 1. Set variables for resource naming and location
$AZURE_LOCATION = "westeurope"  # Change to your preferred Azure region
$CONTROL_PLANE_RG = "radius-control-plane-$env:USER"
$CONTROL_PLANE_CLUSTER = "radius-control-plane"
$WORKLOAD_RG = "ml-inference-$env:USER"
$MANAGED_IDENTITY = "radius-azure-identity"
```
```powershell
# 2. Setup Azure Workload Identity for Radius
./setup-radius-wi.ps1 -ControlPlaneResourceGroup $CONTROL_PLANE_RG -ControlPlaneCluster $CONTROL_PLANE_CLUSTER -ManagedIdentity $MANAGED_IDENTITY

# 3. Get the ARM resource ID of the managed identity
$MANAGED_IDENTITY_ID = az identity show --name $MANAGED_IDENTITY --resource-group $CONTROL_PLANE_RG --query id -o tsv

# 4. Create resource group for target AKS deployment
az group create --name $WORKLOAD_RG --location $AZURE_LOCATION

# 5. Configure Radius environment with Azure subscription and resource group
$SUBSCRIPTION_ID = az account show --query id -o tsv
rad group create default
rad group switch default
rad env create aks-prod
rad env update aks-prod --azure-subscription-id $SUBSCRIPTION_ID --azure-resource-group $WORKLOAD_RG

# 6. Verify environment configuration
rad env show aks-prod
```

### Publish radius recipes
```powershell
$RECIPE_VERSION = "latest"  # Version tag for recipe artifacts

# 1. Create Azure Container Registry for recipes
$ACR_NAME = "radiusrecipes$env:USER"
az acr create --name $ACR_NAME --resource-group $CONTROL_PLANE_RG --sku Standard

# 2. Enable anonymous pull
az acr update --name $ACR_NAME --anonymous-pull-enabled

# 3. Get ACR login server and login
$ACR_LOGIN_SERVER = az acr show --name $ACR_NAME --resource-group $CONTROL_PLANE_RG --query loginServer -o tsv

# Login to ACR (using Azure CLI credentials)
az acr login --name $ACR_NAME
```

```powershell
# 4. Publish recipes and register with Radius environment
./publish-and-register-recipes.ps1 -AcrLoginServer $ACR_LOGIN_SERVER -RecipeVersion $RECIPE_VERSION -Environment aks-prod
```

## Deploy the KServe platform
This step will deploy KServe as a radius application. This will create an AKS cluster on which KServe will be setup.

```powershell
$MANAGED_IDENTITY_ID = az identity show --name $MANAGED_IDENTITY --resource-group $CONTROL_PLANE_RG --query id -o tsv
rad deploy apps/ml-inference-platform.bicep `
  --application ml-inference-platform `
  --environment aks-prod `
  --parameters managedIdentityId=$MANAGED_IDENTITY_ID
```