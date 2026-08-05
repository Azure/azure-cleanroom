# Workloads Testing Guide

This directory contains scripts for deploying and testing clean room clusters with
inferencing and flex node support. Two infrastructure types are supported: **virtual** (local Kind
cluster) and **aks** (Azure Kubernetes Service).

## Virtual Setup (Kind cluster)

The virtual setup runs entirely on a local Kind cluster. No Azure VMs are created and
SSH keys are not required.

### 1. Deploy the cluster

```pwsh
pwsh deploy-cluster.ps1 -infraType virtual
```

This will:
- Build all container images locally and push them to a local registry.
- Create a Kind-based clean room cluster.
- Save cluster info to `sandbox_common/cl-cluster.json` and kubeconfig to
  `sandbox_common/k8s-credentials.yaml`.

### 2. Enable KServe Inferencing

```pwsh
pwsh enable-kserve-inferencing-workload.ps1
```

### 3. Enable flex node

```pwsh
pwsh enable-flex-node.ps1
```

This will generate pod policy signing keys via `generate-signing-keys.ps1` and call
`az cleanroom cluster update` with `--flex-node-profile`.

### 4. Test the cluster

```pwsh
pwsh test-cluster.ps1 -testKServeInferencing
```

This will:
- Verify the cluster is healthy and kubeconfig works.
- Deploy an example KServe model to the flex node, sign its policy, and verify
  the deployment succeeds.

## AKS Setup (Azure Kubernetes Service)

The AKS setup creates real Azure resources: an AKS cluster and a Confidential VM
that joins the cluster as a flex node.

### 1. Build and push container images

Set variables for the container registry and tag:

```pwsh
$repo = "<youracrname>.azurecr.io"
$tag = "latest"
```

Build container images and push them to an Azure Container Registry:

```pwsh
az acr login -n <youracrname>

./build/cleanroom-cluster/build-cleanroom-cluster-infra-containers.ps1 `
  -repo $repo -tag $tag -push

./build/workloads/inferencing/build-workload-infra-containers.ps1 `
  -repo $repo -tag $tag -push

./build/k8s-node/build-api-server-proxy.ps1 `
  -repo $repo -tag $tag -push
```

### 2. Deploy the cluster

```pwsh
pwsh deploy-cluster.ps1 `
  -infraType aks `
  -repo $repo `
  -tag $tag
```

### 3. Enable KServe inferencing

```pwsh
pwsh enable-kserve-inferencing-workload.ps1
```

### 4. Enable flex node

```pwsh
pwsh enable-flex-node.ps1
```

This will create an Azure CVM, join that VM as a flex node to the AKS cluster and setup api-server-proxy on it.

### 5. Test the cluster

```pwsh
pwsh test-cluster.ps1 -testKServeInferencing
```

Same as the virtual setup — deploys an example model to the flex node, signs the
pod policy, and verifies the deployment succeeds.