# Clean Room Operator

A declarative Kubernetes API for managing clean room environments.
Define an `Environment` custom resource and controllers reconcile the
desired state — creating a CCF network, governance contract, and
workload cluster automatically.

- [Quick Start (Pre-built Images)](#quick-start-pre-built-images)
- [Developer Quick Start — AKS](#developer-quick-start--aks)
- [Developer Quick Start — Kind](#developer-quick-start--kind)
- [Bootstrap Runtime (self-contained workload clusters)](#bootstrap-runtime-self-contained-workload-clusters)
- [How It Works](#how-it-works)

## Quick Start (Pre-built Images)

This quickstart uses pre-built images from a container registry. No
local build or PowerShell is required — just Docker, `oras`, and a
bash or PowerShell shell.

### Prerequisites

- Docker
- [oras](https://oras.land/docs/installation) CLI
- Access to a container registry that has the cleanroom images

> [!NOTE]
> The install script uses [oras](https://oras.land) to pull
> artifacts. If `oras` is not on your PATH it is downloaded automatically.

### 1. Install the kubectl CLI plugin

Set the registry and tag for all subsequent commands:

<details open>
<summary>PowerShell</summary>

```powershell
$REPO = "cleanroomemuprregistry.azurecr.io"
$TAG = "29330837583"

pwsh src/cleanroom-operator/scripts/install-cli.ps1 -repo $REPO -tag $TAG
```

</details>
<details>
<summary>Bash</summary>

```bash
REPO=cleanroomemuprregistry.azurecr.io
TAG=29330837583

src/cleanroom-operator/scripts/install-cli.sh --repo $REPO --tag $TAG
```

</details>

### 2. Generate the environment file and create management cluster

<details open>
<summary>PowerShell</summary>

```powershell
kubectl cleanroom dev generate-env `
  --repo $REPO --tag $TAG --outdir generated
```
```powershell
kubectl cleanroom dev up `
  --env-file generated/dev-up.env `
  --output generated/mgmt-kubeconfig.yaml
```

</details>
<details>
<summary>Bash</summary>

```bash
kubectl cleanroom dev generate-env \
  --repo $REPO --tag $TAG --outdir generated

kubectl cleanroom dev up \
  --env-file generated/dev-up.env \
  --output generated/mgmt-kubeconfig.yaml
```

</details>

#### 3. Prepare Azure prerequisites

This creates resource groups and a CCF storage account:

<details open>
<summary>PowerShell</summary>

```powershell
kubectl cleanroom environment prepare-prereqs aks-env `
  --location centralindia
```

</details>
<details>
<summary>Bash</summary>

```bash
kubectl cleanroom environment prepare-prereqs aks-env \
  --location centralindia
```

</details>

Resource group names default to `<name>-cluster-<USER>` and
`<name>-ccf-<USER>`. Override with `--resource-group` and
`--ccf-resource-group`.

#### 4. Create an AKS environment

<details open>
<summary>PowerShell</summary>

```powershell
kubectl cleanroom environment create aks-env `
  --infra-type aks `
  --for-inferencing `
  --prereqs-config aks-env
```

</details>
<details>
<summary>Bash</summary>

```bash
kubectl cleanroom environment create aks-env \
  --infra-type aks \
  --for-inferencing \
  --prereqs-config aks-env
```

</details>

#### 5. Verify inferencing connectivity

After the environment is ready, verify that the inferencing endpoint is
reachable from your machine:

```bash
kubectl cleanroom environment check-inferencing-connectivity aks-env
```

> [!WARNING]
> If the TLS handshake fails, ensure you are connected to the
> Microsoft VPN. Corporate firewalls may block outbound TLS to endpoints with
> non-standard CAs.

#### 6. Setup a model for deployment

<details open>
<summary>PowerShell</summary>

```powershell
kubectl cleanroom model-registration create tinyllama-2 `
  --model-id TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF `
  --source-file tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf `
  --env aks-env `
  --location centralindia
```

</details>
<details>
<summary>Bash</summary>

```bash
kubectl cleanroom model-registration create tinyllama-2 \
  --model-id TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF \
  --source-file tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf \
  --env aks-env \
  --location centralindia
```

</details>

#### 7. Deploy a model instance

<details open>
<summary>PowerShell</summary>

```powershell
kubectl cleanroom model-deployment create tinyllama-2 `
  --model-registration tinyllama-2 `
  --predictor-spec src/cleanroom-operator/kubectl-plugin/samples/predictor-spec-tinyllama.json
```

</details>
<details>
<summary>Bash</summary>

```bash
kubectl cleanroom model-deployment create tinyllama-2 \
  --model-registration tinyllama-2 \
  --predictor-spec src/cleanroom-operator/kubectl-plugin/samples/predictor-spec-tinyllama.json
```

</details>

#### 8. Call the deployed model

Once the model deployment instance is Ready, retrieve the endpoint, CA certificate, and an
access token, then send a request:

<details open>
<summary>PowerShell</summary>

```powershell
# Get the service endpoint
$endpoint = kubectl get md tinyllama-2 -o jsonpath='{.status.serviceEndpoint}'

# Save the CA certificate to a file
kubectl get md tinyllama-2 -o jsonpath='{.status.serviceCaCert}' > cleanroomca.crt

# Get an access token from the publisher's governance client
$token = kubectl cleanroom ccf-user get-access-token tinyllama-2-publisher

# Send a chat completion request
curl --cacert cleanroomca.crt `
  -X POST "$endpoint/v1/chat/completions" `
  -H "Content-Type: application/json" `
  -H "x-ms-cleanroom-authorization: Bearer $token" `
  -d '{
    "model": "tinyllama-2",
    "messages": [
      { "role": "user", "content": "What is the capital of France?" }
    ],
    "max_tokens": 50
  }'
```

</details>
<details>
<summary>Bash</summary>

```bash
# Get the service endpoint
endpoint=$(kubectl get md tinyllama-2 -o jsonpath='{.status.serviceEndpoint}')

# Save the CA certificate to a file
kubectl get md tinyllama-2 -o jsonpath='{.status.serviceCaCert}' > cleanroomca.crt

# Get an access token from the publisher's governance client
token=$(kubectl cleanroom ccf-user get-access-token tinyllama-2-publisher)

# Send a chat completion request
curl --cacert cleanroomca.crt \
  -X POST "$endpoint/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -H "x-ms-cleanroom-authorization: Bearer $token" \
  -d '{
    "model": "tinyllama-2",
    "messages": [
      { "role": "user", "content": "What is the capital of France?" }
    ],
    "max_tokens": 50
  }'
```

</details>

## Developer Quick Start — AKS

> [!NOTE]
> These quickstarts build all container images from source and require
> PowerShell (`pwsh`) and the Azure CLI `cleanroom` extension.

#### 1. Build and publish container images to ACR

```powershell
$acrname = "<youracrname>"
$repo = "$acrname.azurecr.io"

az acr login -n $acrname
az acr update -n $acrname --anonymous-pull-enabled true
```

```powershell
build/onebox/build-dev-up-containers.ps1 -repo $repo -outDir generated
```

#### 2. Install CLI plugin and create management cluster

```powershell
az cleanroom operator install-cli --env-file generated/dev-up.env
```

```powershell
kubectl cleanroom dev up --env-file generated/dev-up.env --output generated/mgmt-kubeconfig.yaml
```

#### 3. Prepare Azure prerequisites

This creates resource groups and a CCF storage account:

```powershell
kubectl cleanroom environment prepare-prereqs aks-env --location centralindia
```

Resource group names default to `<name>-cluster-<USER>` and
`<name>-ccf-<USER>`. Override with `--resource-group` and
`--ccf-resource-group`.

#### 4. Create an AKS environment

```powershell
kubectl cleanroom environment create aks-env `
  --infra-type aks `
  --for-inferencing `
  --prereqs-config aks-env
```

#### 5. Verify inferencing connectivity

```bash
kubectl cleanroom environment check-inferencing-connectivity aks-env
```

> [!WARNING]
> If the TLS handshake fails, ensure you are connected to the
> Microsoft VPN. Corporate firewalls may block outbound TLS to endpoints with
> non-standard CAs.

#### 6. Setup a model for deployment

```powershell
kubectl cleanroom model-registration create tinyllama-2 `
  --model-id TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF `
  --source-file tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf `
  --env aks-env `
  --location centralindia
```

#### 7. Deploy a model instance

```powershell
kubectl cleanroom model-deployment create tinyllama-2 `
  --model-registration tinyllama-2 `
  --predictor-spec src/cleanroom-operator/kubectl-plugin/samples/predictor-spec-tinyllama.json
```

#### 8. Call the deployed model

Once the md is Ready, retrieve the endpoint, CA certificate, and an
access token, then send a request:

```powershell
# Get the service endpoint
$endpoint = kubectl get md tinyllama-2 -o jsonpath='{.status.serviceEndpoint}'

# Save the CA certificate to a file
kubectl get md tinyllama-2 -o jsonpath='{.status.serviceCaCert}' > cleanroomca.crt

# Get an access token from the publisher's governance client
$token = kubectl cleanroom ccf-user get-access-token tinyllama-2-publisher

# Send a chat completion request
curl --cacert cleanroomca.crt `
  -X POST "$endpoint/v1/chat/completions" `
  -H "Content-Type: application/json" `
  -H "x-ms-cleanroom-authorization: Bearer $token" `
  -d '{
    "model": "tinyllama-2",
    "messages": [
      { "role": "user", "content": "What is the capital of France?" }
    ],
    "max_tokens": 50
  }'
```

## Developer Quick Start — Kind

### Prerequisites

- Docker
- PowerShell (`pwsh`)
- Azure CLI with the `cleanroom` extension

### 1. Build and bootstrap

Build images and generate env file:
```powershell
build/onebox/build-dev-up-containers.ps1 -outDir generated
```

Install the kubectl plugin:
```powershell
az cleanroom operator install-cli --env-file generated/dev-up.env
```

Create management cluster and deploy operator:
```powershell
kubectl cleanroom dev up --env-file generated/dev-up.env --output generated/mgmt-kubeconfig.yaml
```

### 2. Create an environment

The CLI creates the `Environment` CR and waits for it to reach `Ready`:

```powershell
kubectl cleanroom environment create virtual-env `
  --infra-type virtual `
  --for-inferencing `
  --node-provisioning-mode auto
```

### 3. Setup a model for deployment

Next create the `ModelRegistration` CR and waits for it to reach `Ready`:

```powershell
kubectl cleanroom model-registration create tinyllama-1 `
  --model-id TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF `
  --source-file tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf `
  --env virtual-env `
  --location centralindia
```

### 4. Deploy a model instance (inference endpoint)

Finally deploy an instance of the model by creating a `ModelDeployment`
CR and waits for it to reach `Ready`:

```powershell
kubectl cleanroom model-deployment create tinyllama-1 `
  --model-registration tinyllama-1 `
  --predictor-spec src/cleanroom-operator/kubectl-plugin/samples/predictor-spec-tinyllama.json
```

### 5. Call the deployed model

The inferencing endpoint uses an in-cluster `.svc` address that is not
directly reachable from your machine. Use `kubectl port-forward` to
tunnel traffic to the workload cluster's inferencing service.

```powershell
# Get an access token from the publisher's governance client
$token = kubectl cleanroom ccf-user get-access-token tinyllama-1-publisher

# Export the workload cluster kubeconfig
kubectl cleanroom cluster kubeconfig virtual-env-cluster `
  -f generated/workload-kubeconfig.yaml

# Port-forward to the inferencing service in the workload cluster
kubectl port-forward `
  --kubeconfig generated/workload-kubeconfig.yaml `
  -n kserve-inferencing-agent `
  svc/kserve-inferencing-agent 8443:443 &

# Send a chat completion request (-k skips TLS hostname verification
# since the certificate SAN is the .svc name, not localhost)
curl -k `
  -X POST "https://localhost:8443/ai/v1/chat/completions" `
  -H "Content-Type: application/json" `
  -H "x-ms-cleanroom-authorization: Bearer $token" `
  -d '{
    "model": "tinyllama-1",
    "messages": [
      { "role": "user", "content": "What is the capital of France?" }
    ],
    "max_tokens": 50
  }'
```
```powershell
# Clean up the port-forward.
Get-Process kubectl | Where-Object { $_.CommandLine -match 'port-forward.*kserve-inferencing-agent' } | Stop-Process
```

## Bootstrap Runtime (self-contained workload clusters)

The quick starts above use a long-lived **management cluster** that
provisions and remotely manages one or more **workload clusters**. The
`Environment`, `ModelRegistration`, and `ModelDeployment` resources live
on the management cluster, and the operator reaches into each workload
cluster to deploy inference endpoints.

Some scenarios instead require the cleanroom operator to run **inside the
workload cluster itself**, alongside the workloads it serves. The
motivating case is **AI Runway**: an AI Runway `ModelDeployment` and its
`accr-conf-inferencing` provider controller run on the workload cluster,
and the provider controller creates cleanroom `ModelRegistration` +
`ModelDeployment` resources locally. For that to work, the cleanroom
operator (and its CCF/governance components) must be installed on the
same cluster — there is no separate management cluster to delegate to.

The **bootstrap runtime** supports this topology. It is a
ephemeral cluster whose only job is to provision a workload cluster. Once
the workload cluster exists, you install the operator onto it directly
and the bootstrap runtime can be discarded. The workload cluster is then
fully self-contained.

```
  ┌─ Bootstrap runtime (ephemeral) ─┐        ┌─ Workload cluster (self-contained) ─┐
  │  cluster-provider-client only   │ create │  cleanroom-operator                 │
  │  (no operator, no CCF)          │ ─────► │  CCF + governance + KServe          │
  │                                 │        │  AI Runway + provider controller    │
  └─────────────────────────────────┘        │  Environment / ModelRegistration /  │
            (can be stopped)                 │  ModelDeployment (all local)        │
                                             └─────────────────────────────────────┘
```

Unlike `dev up` — which installs the full operator into the cluster it
creates and expects you to use that cluster directly — `bootstrap`
provisions a *separate* workload cluster, installs nothing but the
cluster-provider-client, and hands back a kubeconfig. The workload
cluster survives after the bootstrap runtime is stopped.

### 1. Build, publish, and install the CLI plugin

Build and publish the container images to an ACR the AKS workload cluster can
pull from (this also generates `generated/dev-up.env`), then install the
`kubectl cleanroom` plugin:

```powershell
$acrname = "<youracrname>"
$repo = "$acrname.azurecr.io"

az acr login -n $acrname
az acr update -n $acrname --anonymous-pull-enabled true

build/onebox/build-dev-up-containers.ps1 -repo $repo -outDir generated
az cleanroom operator install-cli --env-file generated/dev-up.env
```

### 2. Start the bootstrap runtime

```powershell
kubectl cleanroom bootstrap start --env-file generated/dev-up.env
```

This creates a Kind cluster running only the
cluster-provider-client (plus a credentials-proxy for AKS auth) and
stores the image references for later commands.

### 3. Provision a workload cluster

This provisions a **bare** workload cluster (correct networking and node
configuration) and nothing else. Workload profiles such as inferencing
depend on CCF and governance, which are set up later by the operator on
the workload cluster itself (step 6), not by the bootstrap runtime.

`--kind-cluster-name` (virtual) and `--aks-cluster-name` (aks) let you
name the provisioned cluster. The name is an override that the provider
targets idempotently — re-running against the same name is a no-op.

<details>
<summary>Virtual (Kind)</summary>

```powershell
kubectl cleanroom bootstrap cluster create my-workload `
  --type virtual `
  --kind-cluster-name my-workload-kind
```

</details>

<details open>
<summary>AKS</summary>

**Prerequisites.** The AKS provider needs an existing resource group and
a provider-config JSON file that carries the subscription, tenant,
resource group, location, and the target AKS cluster name.

Define the names once — the resource group is derived from your `USER`
so the block can be copy-pasted without edits — then create the resource
group and write `generated/aks-config.json` (the subscription and tenant
are auto-detected from your current `az login`):

```powershell
$RG = "$($env:USER)-rg"
$location = "centralindia"
$aksClusterName = "my-workload-aks"

# Create the resource group up-front (the provider provisions the AKS
# cluster into it, but does not create the group itself).
az group create --name $RG --location $location

# Write the provider-config file. The aksClusterName field names the
# cluster the provider will create (and reuse idempotently on reruns).
@{
  subscriptionId    = "$(az account show --query id -o tsv)"
  tenantId          = "$(az account show --query tenantId -o tsv)"
  resourceGroupName = $RG
  location          = $location
  aksClusterName    = $aksClusterName
} | ConvertTo-Json | Out-File generated/aks-config.json
```

Then provision the cluster:

```powershell
kubectl cleanroom bootstrap cluster create my-workload `
  --type aks `
  --provider-config generated/aks-config.json
```

The bootstrap runtime's credentials-proxy supplies Azure credentials
from your `~/.azure` mount, so no additional login is needed inside the
cluster. The `infraType` and provider-config are stored in the bootstrap
runtime so later commands (e.g. `bootstrap cluster kubeconfig`) can
locate the AKS cluster without re-supplying them.

</details>

### 4. Fetch the workload kubeconfig

```powershell
kubectl cleanroom bootstrap cluster kubeconfig my-workload -f generated/workload.yaml
```

The kubeconfig points at an externally reachable address, so it remains
valid after the bootstrap runtime is stopped.

### 5. Stop the bootstrap runtime (optional)

```powershell
kubectl cleanroom bootstrap stop
```

The workload cluster is an independent sibling of the bootstrap cluster
and continues running.

### 6. Install the cleanroom operator on the workload cluster

Point at the workload cluster and install the operator explicitly. On
AKS this step also prepares the Azure prerequisites and workload
identity (see the AKS tab below).

<details>
<summary>Virtual (Kind)</summary>

```powershell
$env:KUBECONFIG = "generated/workload.yaml"
kubectl cleanroom install --env-file generated/dev-up.env
```

</details>

<details open>
<summary>AKS</summary>

On a self-contained AKS workload cluster the provider clients and
operator run **in-cluster** and have no host `az` credentials, so they
authenticate to Azure via **AKS Workload Identity**. Set this up first
with `prepare-prereqs --setup-workload-identity`, which creates the
resource groups and CCF storage account, creates a user-assigned managed
identity, federates it with the operator and provider-client service
accounts (using the cluster's OIDC issuer), grants it RBAC, and records
the identity's client ID in the prereqs ConfigMap.

The command must target the **same** resource group, location, and
cluster you bootstrapped, so read them straight from
`generated/aks-config.json`.

The governance service uploads its OIDC documents to a storage account,
so the managed identity needs `Storage Blob Data Contributor` on it
(control-plane roles do not grant blob data access). This defaults to the
shared `cleanroomoidc` account (in `azcleanroom-ctest-rg`) used across
runs, so you normally don't need to specify it — override with
`--oidc-storage-account-id <resource-id>` to target a different account:

```powershell
$env:KUBECONFIG = "generated/workload-aks.yaml"
$cfg = Get-Content generated/aks-config.json | ConvertFrom-Json
kubectl cleanroom environment prepare-prereqs my-env-prereqs `
  --resource-group $cfg.resourceGroupName `
  --ccf-resource-group $cfg.resourceGroupName `
  --location $cfg.location `
  --setup-workload-identity `
  --aks-cluster-name $cfg.aksClusterName
```

Then install the operator, passing the prereqs ConfigMap so `install`
reads the federated identity's client ID from it:

```powershell
kubectl cleanroom install --env-file generated/dev-up.env `
  --provider-virtual=false `
  --prereqs-config my-env-prereqs
```

`--provider-virtual=false` is **required** on AKS. It disables the
virtual (Kind) hostPath mounts (`/var/run/docker.sock`, shared dirs) on
the provider clients — those only exist on a local Kind host and would
leave the `cluster-provider-client` / `ccf-provider-client` pods stuck
in `ContainerCreating` on AKS.

> **Note:** `prepare-prereqs --setup-workload-identity` grants the managed identity
> `Contributor` and `User Access Administrator` at **subscription** scope
> by default (the provider creates role assignments for the AKS kubelet
> and cluster identities, which `Contributor` alone cannot do). Use
> `--rbac-scope resource-group` to scope those grants to the cluster and
> CCF resource groups instead; note this does not cover the AKS-managed
> `MC_*` node resource group. RBAC propagation can take a minute.

</details>

### 7. Create an environment on the workload cluster

Target the `Environment` at the **pre-provisioned** workload cluster by
passing the same cluster name override so the operator reuses the
existing cluster instead of provisioning a new one.

<details>
<summary>Virtual (Kind)</summary>

```powershell
kubectl cleanroom environment create my-env `
  --infra-type virtual `
  --for-inferencing `
  --kind-cluster-name my-workload-kind
```

</details>

<details open>
<summary>AKS</summary>

The Azure context (subscription, resource group, tenant, location) the
operator needs to locate the existing cluster comes from the **prereqs**
ConfigMap you created in step 6, merged with the `--aks-cluster-name`
override. Create the environment against it (reading the cluster name
from the same config file so it always matches):

```powershell
$cfg = Get-Content generated/aks-config.json | ConvertFrom-Json
kubectl cleanroom environment create my-env `
  --infra-type aks `
  --for-inferencing `
  --prereqs-config my-env-prereqs `
  --aks-cluster-name $cfg.aksClusterName
```

The operator merges the prereqs `clusterProviderConfig` (as the base)
with `aksClusterName` (as an override), so the resulting Cluster CR
carries all of `subscriptionId`, `resourceGroupName`, `tenantId`,
`location`, and `aksClusterName`. The AKS provider then finds the
existing `my-workload-aks` cluster (matching the name and resource group
from `bootstrap cluster create`) and reuses it — installing the
inferencing workload into it rather than creating a new cluster.

> **Note:** `--for-inferencing` enables a flex node pool. On AKS the operator
> defaults `provisionUsingSSH=true`, so flex node VMs are provisioned
> from a stock Ubuntu CVM image over SSH — no pre-baked
> `cleanroom-image-digests` gallery image is required. SSH keys are
> generated automatically.

> **Important:** Steps 3, 6, and 7 all derive the resource group, location, and cluster
> name from `generated/aks-config.json`, so they stay in sync by
> construction. If you change any of those values, re-run step 3 to
> rewrite the config and re-provision, otherwise the operator may
> provision a **second** AKS cluster instead of reusing the bootstrapped
> one.

</details>

From here the model registration, deployment, and (optionally) AI Runway
flows run entirely on the workload cluster.

To run the end-to-end **AI Runway** scenario on this workload cluster — install
AI Runway, deploy the `accr-conf-inferencing` provider, and deploy and call a
model through an AI Runway `ModelDeployment` — continue with the
[AI Runway provider guide](providers/airunway/README.md).

## How It Works

Each of the three resources above progresses through a sequence of
conditions. The CLI prints `✓` when a condition completes and `·` for
intermediate progress events:

- [Environment](docs/how-it-works/environment.md) — CCF network,
  governance, and cluster provisioning
- [ModelRegistration](docs/how-it-works/model-registration.md) — model
  upload, OIDC setup, and governance document lifecycle
- [ModelDeployment](docs/how-it-works/model-deployment.md) —
  endpoint deployment on the workload cluster

If something fails, see the [Troubleshooting](docs/troubleshooting.md)
guide for using the Aspire dashboard, collecting logs, and retrying.

For background on why this approach replaces the script-based workflow,
see [Why the Operator Approach](docs/why-operator.md).
