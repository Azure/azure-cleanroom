# Deploy clean rooms locally for development <!-- omit from toc -->

The intent of this guide is to setup a clean room environment where in the clean room infra. and 
application containers all run locally on a Kind cluster instead of a Confidential ACI container 
group in Azure.

This is geared towards local development and learning scenarios where deploying to CACI as part of 
the dev. loop can become an overhead. The ability to test the changes locally with a full setup 
would help in speeding up development and also increase familiarity with the underlying architecture.

> [!WARNING]
> The virtual version of cleanroom runs on hardware that does not support SEV-SNP. Virtual mode 
> does not provide any security guarantees and should be used for development purposes only.

## Prerequisites <!-- omit from toc -->
- Kind: see steps [here](https://kind.sigs.k8s.io/docs/user/quick-start/) to install it.

## Differences compared to CACI deployment <!-- omit from toc -->
- Runs in non SEV-SNP environment.
- Uses an allow all cce policy enforcement for SKR.
- The SKR setup is not locked down and open to releasing keys to any clean room enviornment and 
  meant for development purposes only.
- The setup creates and uses Azure Key Vault and Azure Storage accounts. The interactions with these
  services are not emulated/mocked.

## Setup instructions <!-- omit from toc -->
Follow the below steps to create a local setup.

## 1. Create Kind cluster and a local registry
Below creates a kind cluster named `kind-cleanroom` and also starts a local registry container named `ccr-registry`. The cluster is configured so that it can reach the registry endpoint at `localhost:5000` from within the cluster.
```powershell
$root = git rev-parse --show-toplevel
bash $root/test/onebox/kind-up.sh
```
## 2. Build clean room containers and push to local registry
The below command will build the clean room infrastructure containers and push the images to the 
local registry that was started above. These images get deployed on the kind cluster to create the 
virtual clean room environment.
```powershell
pwsh $root/build/onebox/build-local-cleanroom-containers.ps1
```
Unless you are changing the code for the container images you can run the above command once and 
keep re-using the pushed images when running the subsequent steps below.

## 3. Run the collab scenario locally
> [!NOTE]
> The steps below run with `scenario=encrypted-storage` but the same flow can be used with
>  `db-access` or `ml-training` scenarios also.

With the kind cluster setup execute the below command to run the scenario:
```powershell
$scenario = "encrypted-storage"
pwsh $root/test/onebox/multi-party-collab/$scenario/run-collab.ps1
```

## 4. Delete the clean room from the local cluster
To remove the clean room instance run the following:
```powershell
pwsh $root/test/onebox/multi-party-collab/remove-virtual-cleanroom.ps1
```

## 5. Run scenarios in CACI
Follow the below steps to run the scenario in CACI instead of the Kind cluster. Note that below runs the scenarios in with the same insecure (allow all) CCE policy meant for dev/test:
```powershell
# Build and publish all the container images.
$root = git rev-parse --show-toplevel
$acrname = <youracrname>
$repo = "$acrname.azurecr.io"
$tag = "onebox"
$withCcePolicy = $false # change to true if CCE policy should be computed and enforced.

az acr login -n $acrname

pwsh $root/build/onebox/build-local-cleanroom-containers.ps1 `
  -repo $repo `
  -tag $tag `
  -withRegoPolicy:$withCcePolicy

pwsh $root/build/ccf/build-ccf-infra-containers.ps1 `
  -repo $repo `
  -tag $tag `
  -push `
  -pushPolicy:$withCcePolicy

# Launch the scenario.
$scenario = "nginx-hello"
pwsh $root/test/onebox/multi-party-collab/$scenario/run-collab-aci.ps1 `
  -registry acr `
  -repo $repo `
  -tag $tag `
  -allowAll:(!$withCcePolicy)
```
