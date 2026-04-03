# Bicep ML Inference Platform

This is a comprehensive Bicep implementation of an ML inference platform that uses standard Azure Bicep modules and Azure deployment scripts to [provision and configure](https://kserve.github.io/archive/0.15/admin/kubernetes_deployment/) a complete machine learning inference solution on Azure Kubernetes Service. The platform provides automated deployment of ML serving infrastructure with built-in monitoring, autoscaling, and security features.

## Architecture

The platform consists of the following components:

### Infrastructure Components
- **AKS Cluster**: Azure Kubernetes Service cluster with security features
- **Managed Identities**: For secure deployment script execution

### Kubernetes Components (deployed via Helm)
- **cert-manager**: Automatic TLS certificate provisioning
- **KEDA**: Kubernetes Event-driven Autoscaling
- **Envoy Gateway**: Gateway API implementation for traffic management
- **KServe**: Machine learning model serving platform
- **Prometheus Stack**: Monitoring and observability (Prometheus + Grafana)

### ML Inference Workloads
- **InferenceService**: KServe-based model serving
- **Gateway & HTTPRoute**: Traffic routing for model endpoints
- **HorizontalPodAutoscaler**: Automatic scaling based on resource usage

## Key Features

- **Infrastructure as Code**: Complete platform defined in Bicep templates
- **Automated Deployment**: PowerShell scripts for easy deployment and cleanup  
- **Modular Architecture**: Reusable Bicep modules for different components
- **Standard Tooling**: Uses standard Azure CLI and Bicep - no special tools required
- **Comprehensive Monitoring**: Prometheus and Grafana integration
- **Security First**: Managed identities, RBAC, network policies, and workload identity

## Directory Structure

```
bicep-ml-inference/
├── deploy-kserve.bicep                 # Main template that orchestrates all modules
├── deploy-kserve.ps1                  # PowerShell deployment script
├── deploy-iris.ps1                    # PowerShell script to deploy sample ML model
├── cleanup.ps1                        # PowerShell cleanup script
├── modules/
│   ├── aks-cluster.bicep              # AKS cluster with security features
│   ├── managed-identity.bicep         # Managed identity for deployment scripts
│   ├── helm-chart.bicep               # Generic Helm chart deployment module
│   ├── cert-manager.bicep             # cert-manager Helm deployment
│   ├── keda.bicep                     # KEDA Helm deployment
│   ├── envoy-gateway.bicep            # Envoy Gateway Helm deployment
│   ├── gateway-api.bicep              # Gateway API CRDs deployment
│   ├── kserve.bicep                   # KServe Helm deployment
│   ├── prometheus.bicep               # Prometheus stack Helm deployment
│   └── kubectl-wait.bicep             # Kubernetes resource wait utility
├── parameters/
│   └── deploy-kserve.parameters.json  # Production parameters
└── README.md                          # This file
```

## Prerequisites

1. **Azure CLI**: Version 2.75.0 or later
2. **Azure Subscription**: With appropriate permissions to create resources
3. **Resource Group**: Target resource group for deployment
4. **PowerShell**: All steps are written assuming `pwsh` shell for Linux is installed ([installation instructions](https://docs.microsoft.com/en-us/powershell/scripting/install/installing-powershell-core-on-linux))

## Quick Start

### 1. Set Variables

Set the following PowerShell variables for your deployment:

```powershell
# Set your deployment variables
$resourceGroupName = "ml-inference-platform-$env:USER"
$location = "westeurope"  # Change to your preferred Azure region
```

> **Note**: These variables are used in the PowerShell commands throughout this guide. You can customize the resource group name and location as needed. The location is also specified in the parameter file (`parameters/deploy-kserve.parameters.json`).

### 2. Clone or Download

Ensure you have the Bicep files in your local directory.

### 3. Login to Azure

```powershell
az login
```

### 4. Deploy the KServe Platform

```powershell
./deploy-kserve.ps1 -ResourceGroup $resourceGroupName -Location $location
```

### 5. Get AKS Credentials

After deployment, get the AKS credentials:
```powershell
az aks get-credentials --resource-group $resourceGroupName --name aks-ml-cluster
```
Above is required to set the kube context for the next command to deploy the ML model.

### 6. Deploy ML Model

Deploy the sample Iris model using the provided PowerShell script:
```powershell
./deploy-iris.ps1
```

This script will:
- Create the `kserve-test` namespace
- Deploy a scikit-learn Iris classification model as an InferenceService
- Wait for the deployment to be ready
- Verify the deployment by requesting a prediction

## Configuration

### Parameters

The main template accepts the following parameters:

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `platformName` | string | `ml-inference-platform` | Name of the platform deployment |
| `location` | string | `westeurope` | Azure region for deployment |
| `aksClusterName` | string | `aks-ml-cluster` | Name of the AKS cluster |
| `nodeCount` | int | `3` | Number of nodes in the AKS cluster |
| `vmSize` | string | `Standard_D4ds_v5` | VM size for AKS nodes |
| `chartVersions` | object | See deploy-kserve.parameters.json | Helm chart versions |
| `enablePrometheus` | bool | `false` | Enable Prometheus monitoring stack |
| `tags` | object | See deploy-kserve.parameters.json | Resource tags |

## Deployment Process

The deployment follows this sequence:

1. **AKS Cluster**: Creates the Kubernetes cluster
2. **cert-manager**: Deploys certificate management (parallel with KEDA)
3. **KEDA**: Deploys autoscaling capabilities (parallel with cert-manager)
4. **Envoy Gateway**: Deploys Gateway API support (depends on cert-manager)
5. **Gateway API**: Deploys Gateway API CRDs and GatewayClass (depends on Envoy Gateway)
6. **KServe**: Deploys ML serving platform (depends on Gateway API and KEDA)

After infrastructure deployment, run `./deploy-iris.ps1` to deploy the sample ML model.

## Troubleshooting

### Deployment Scripts

If deployment scripts fail, check the logs:

```powershell
az deployment-scripts logs --resource-group $resourceGroupName --name <script-name>
```

### Kubernetes Resources

Check the status of Kubernetes resources:

```powershell
kubectl get pods --all-namespaces
kubectl get inferenceservice -n kserve-test
kubectl get gateway -n kserve-test
```

## Cleanup

When you're done with the platform, you can clean up all resources using the PowerShell cleanup script:

### Using PowerShell Cleanup Script (Recommended)

```powershell
./cleanup.ps1 -ResourceGroup $resourceGroupName
```

With force flag to skip confirmations:
```powershell
./cleanup.ps1 -ResourceGroup $resourceGroupName -Force
```

### Manual Cleanup

Alternatively, you can delete the resource group manually:
```powershell
az group delete --name $resourceGroupName --yes
```

**⚠️ Warning**: This will delete ALL resources in the resource group and cannot be undone.