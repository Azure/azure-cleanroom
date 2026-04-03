# Deploy workloads locally for development <!-- omit from toc -->

The intent of this guide is to setup a clean room environment where in the clean room infra. and 
application containers all run locally on a Kind cluster instead of AKS/CACI container 
group in Azure.

This is geared towards local development and learning scenarios where deploying to AKS/CACI as part of 
the dev. loop becomes an overhead. The ability to test the changes locally with a full setup 
would help in speeding up development and also increase familiarity with the underlying architecture.

> [!WARNING]
> The virtual version of cleanroom runs on hardware that does not support SEV-SNP. Virtual mode 
> does not provide any security guarantees and should be used for development purposes only.

## Prerequisites <!-- omit from toc -->
- Kind: see steps [here](https://kind.sigs.k8s.io/docs/user/quick-start/) to install it.

## Setup instructions <!-- omit from toc -->
Follow the below steps to create a local setup.

## 1. Build clean room containers and push to local registry
The below command will build the clean room infrastructure containers and push the images to a 
local registry. These images will get deployed on the kind cluster to create the
virtual clean room environment.
```powershell
$root = git rev-parse --show-toplevel
pwsh $root/build/onebox/build-containers.ps1
```
Unless you are changing the code for the container images you can run the above command once and 
keep re-using the pushed images when running the subsequent steps below.

## 2. Run the workload scenario locally
First setup the environment that creates the CCF and K8s cluster instance:
```powershell
pwsh $root/test/onebox/workloads/setup-env.ps1
```
Then run the desired workload scenario as below:
```powershell
# big-data-query-analytics
pwsh $root/test/onebox/multi-party-collab/big-data-query-analytics/test-big-data-analytics.ps1
```
```powershell
# kserve-inferencing
pwsh $root/test/onebox/model-serving/kserve-inferencing/test-kserve-inferencing.ps1
```
The setup created above is a shared environment and can be used to run any of the scenarios.

## 3. Run scenarios in AKS
Follow the below steps to run the scenario in AKS instead of the Kind cluster. Note that below runs
the scenarios with the same insecure (allow all) CCE policy meant for dev/test:
```powershell
# Build and publish all the container images.
$root = git rev-parse --show-toplevel
$acrname = <youracrname>
$repo = "$acrname.azurecr.io"
$tag = "onebox"
$withCcePolicy = $false # change to true if CCE policy should be computed and enforced.

az acr login -n $acrname

# Enable anonymous pull on the ACR so that CACI containers can pull images.
az acr update -n $acrname --anonymous-pull-enabled true

pwsh $root/build/onebox/build-containers.ps1 `
  -repo $repo `
  -tag $tag `
  -withRegoPolicy:$withCcePolicy
```
```powershell
# Create the environment in Azure.
# Use -location to specify the Azure region (default: westeurope).
pwsh $root/test/onebox/workloads/setup-env.ps1 `
  -infraType aks `
  -registry acr `
  -repo $repo `
  -tag $tag `
  -allowAll `
  -location "westeurope"
```
```powershell
# kserve-inferencing
pwsh $root/test/onebox/model-serving/kserve-inferencing/test-kserve-inferencing.ps1
```
```powershell
# big-data-query-analytics
pwsh $root/test/onebox/multi-party-collab/big-data-query-analytics/test-big-data-analytics.ps1
```

## 4. Testing with a different FlexNode VM SKU

By default, FlexNode VMs use the `Standard_DC4as_v5` SKU (CPU-only confidential VM). To test
with a different VM SKU (e.g. a GPU-enabled confidential VM like `Standard_NCC40ads_H100_v5`),
pass the `-flexNodeVmSize` parameter to the test script:
```powershell
# kserve-inferencing with a custom FlexNode VM SKU
pwsh $root/test/onebox/model-serving/kserve-inferencing/test-kserve-inferencing.ps1 `
  -flexNodeVmSize "Standard_NCC40ads_H100_v5" `
  -models "gpt2"
```

> [!NOTE]
> The FlexNode VM is always provisioned in the same region as the AKS cluster.

> [!NOTE]
> Changing the VM SKU alone does not enable GPU inference. The inference pod resource requests
> (CPU/memory) and the `llamacpp-server` runtime remain unchanged. To actually leverage GPU
> hardware, you would additionally need to:
> 1. Install NVIDIA GPU drivers on the FlexNode VM.
> 2. Deploy the NVIDIA device plugin DaemonSet to the cluster.
> 3. Add `nvidia.com/gpu` resource requests to the inference pod spec.
>
> Without these steps, a GPU-enabled VM will run the model on CPU — useful for verifying that
> the clean room stack functions correctly on GPU VM SKUs even without GPU acceleration.
