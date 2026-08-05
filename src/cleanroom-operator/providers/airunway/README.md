# AI Runway with Azure Clean Room (`accr-conf-inferencing` provider)

This guide walks through deploying a model with
[AI Runway](https://github.com/kaito-project/airunway) onto a **self‑contained
confidential AKS clean‑room** using the `accr-conf-inferencing` provider shim.

An AI Runway `ModelDeployment` selected for this provider is reconciled by the
provider controller into cleanroom `ModelRegistration` + `ModelDeployment`
resources, which the cleanroom operator deploys as a confidential KServe
inference endpoint on a SEV‑SNP flex node.

```
 AIRunway ModelDeployment (airunway.ai)
        │  provider.name = accr-conf-inferencing
        ▼
 accr-conf-inferencing provider  ──creates──►  cleanroom ModelRegistration
        │                                              │ (model upload + governance)
        │                                              ▼
        └───────────────────────────►  cleanroom ModelDeployment
                                                       │
                                                       ▼
                          Confidential KServe endpoint on a flex CVM node
```

The provider runs **in‑cluster** alongside the cleanroom operator on the workload
cluster; it only talks to the Kubernetes API (all Azure work is done by the
operator/provider‑clients), so it needs no Azure credentials of its own.

---

## Prerequisites

- A workstation with `pwsh`, `kubectl`, `docker`, `helm`, and the Azure CLI
  (`az`), logged in to a subscription where you can create AKS + storage +
  managed identities and **assign roles** (`Owner` or `User Access
  Administrator`).
- The `kubectl cleanroom` plugin installed (installed in Part 1).
- Access to a container registry the AKS cluster can pull from (this guide uses
  `gsinhadev.azurecr.io` with anonymous pull enabled — replace with your own).

Set a few variables reused throughout (PowerShell):

```powershell
$REPO = "gsinhadev.azurecr.io"     # your ACR (anonymous pull enabled)
$env:USER ??= $env:USERNAME        # ensure USER is set
```

---

## Part 1 — Provision the workload cluster + environment

Follow the **Bootstrap Runtime (self‑contained workload clusters)** section of
the [cleanroom‑operator README](../../README.md#bootstrap-runtime-self-contained-workload-clusters)
end to end. Those steps run **as‑is** for AI Runway — nothing needs to change.
When finished you have a confidential AKS workload cluster with the cleanroom
operator installed and a `Ready`, inferencing‑enabled `Environment`.

This guide reuses the values from that flow (adjust if you named things
differently):

| Value                     | Assumed here                     |
| ------------------------- | -------------------------------- |
| `KUBECONFIG`              | `generated/workload-aks.yaml`    |
| Environment name          | `clean-env`                      |
| Resource group / location | from `generated/aks-config.json` |

```powershell
$env:KUBECONFIG = "generated/workload-aks.yaml"
$cfg = Get-Content generated/aks-config.json | ConvertFrom-Json
$RG = $cfg.resourceGroupName ; $location = $cfg.location
```

---

## Part 2 — Create model storage + managed identity

The AI Runway provider builds a cleanroom `ModelRegistration`, which requires a
storage account (for model upload) and a user‑assigned managed identity (for
inference‑time model access). The provider runs in‑cluster and cannot create
these, so create them once and point the provider at them.

The cleanroom operator handles the rest automatically: it creates the blob
container, grants its own workload identity blob‑data access for the upload, and
federates/authorizes the model identity for inference.

```powershell
$modelSa = "$($env:USER)airunwaymodelsa"   # 3-24 lowercase alphanumeric, globally unique
$modelMi = "airunway-model-mi"

az storage account create --name $modelSa --resource-group $RG `
  --location $location --sku Standard_LRS --kind StorageV2 `
  --min-tls-version TLS1_2 --allow-shared-key-access false

az identity create --name $modelMi --resource-group $RG

$SA_ID = az storage account show -n $modelSa -g $RG --query id -o tsv
$MI_ID = az identity show -n $modelMi -g $RG --query id -o tsv
Write-Host "SA_ID=$SA_ID"
Write-Host "MI_ID=$MI_ID"
```

---

## Part 3 — Build and push the provider image

The provider is built from this repo combined with the AI Runway controller API
(referenced via a local `go.mod` replace). The build script pushes to the local
registry; retag and push to your ACR so the AKS cluster can pull it:

```powershell
pwsh build/cleanroom-operator/build-airunway-provider.ps1
az acr login -n ($REPO -replace '\.azurecr\.io$','')
docker tag accr-conf-inferencing-provider:latest $REPO/accr-conf-inferencing-provider:latest
docker push $REPO/accr-conf-inferencing-provider:latest
```

---

## Part 4 — Install AI Runway (Option B)

Install the AI Runway CRDs + core controller on the workload cluster
(the core controller performs engine/provider selection and sets
`status.provider`). See the
[AI Runway deploy docs](https://github.com/kaito-project/airunway/tree/main/deploy).

```powershell
kubectl apply -f https://raw.githubusercontent.com/kaito-project/airunway/main/deploy/controller.yaml
# optional dashboard:
# kubectl apply -f https://raw.githubusercontent.com/kaito-project/airunway/main/deploy/dashboard.yaml
```

Verify:

```powershell
kubectl get pods -n airunway-system
kubectl get crd modeldeployments.airunway.ai inferenceproviderconfigs.airunway.ai
```

---

## Part 5 — Deploy the `accr-conf-inferencing` provider

Edit [`deploy/provider.yaml`](deploy/provider.yaml) and set the image and the
two default env vars to the storage account + managed identity from Part 2
(`$SA_ID` / `$MI_ID`):

```yaml
        image: gsinhadev.azurecr.io/accr-conf-inferencing-provider:latest
        ...
        env:
          - name: AIRUNWAY_DEFAULT_STORAGE_ACCOUNT_ID
            value: <SA_ID>
          - name: AIRUNWAY_DEFAULT_MANAGED_IDENTITY_ID
            value: <MI_ID>
```

These become cluster‑wide defaults, so individual `ModelDeployment`s only need to
reference the environment. Apply it:

```powershell
kubectl apply -f src/cleanroom-operator/providers/airunway/deploy/provider.yaml
```

Verify the provider registered itself with AI Runway:

```powershell
kubectl get pods -n cleanroom-system -l app=accr-conf-inferencing-provider
kubectl get inferenceproviderconfig accr-conf-inferencing
```

`inferenceproviderconfig` should show `READY=true`.

---

## Part 6 — Deploy a model via AI Runway

Create an AI Runway `ModelDeployment` in the **same namespace as the
environment** (`default`). Because the `accr-conf-inferencing` provider is
explicit‑only, set `spec.provider.name` explicitly. For a multi‑variant GGUF
repo, select the file via `overrides.sourceFile`:

```powershell
@'
apiVersion: airunway.ai/v1alpha1
kind: ModelDeployment
metadata:
  name: airunway-tinyllama
  namespace: default
spec:
  model:
    id: TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF
  provider:
    name: accr-conf-inferencing
    overrides:
      environmentRef: clean-env
      sourceFile: tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf
'@ | kubectl apply -f -
```

Watch the reconcile chain:

```powershell
# AI Runway MD (provider selection + overall status)
kubectl get modeldeployment.airunway.ai airunway-tinyllama -o wide

# cleanroom resources created by the provider
kubectl get modelregistration airunway-tinyllama
kubectl get md airunway-tinyllama
```

The chain progresses: AI Runway core sets `status.provider.name =
accr-conf-inferencing` → the provider creates the cleanroom `ModelRegistration`
(model upload + governance) → then the cleanroom `ModelDeployment` → the
predictor pod starts on the flex CVM node → `md/airunway-tinyllama` reaches
`Ready`.

---

## Part 7 — Call the deployed model

Same pattern as the operator README's *call the deployed model* step, using the
AI Runway deployment's resources (`md/airunway-tinyllama`, publisher
`airunway-tinyllama-publisher`):

```powershell
$endpoint = kubectl get md airunway-tinyllama -o jsonpath='{.status.serviceEndpoint}'
kubectl get md airunway-tinyllama -o jsonpath='{.status.serviceCaCert}' > airunwayca.crt
$token = kubectl cleanroom ccf-user get-access-token airunway-tinyllama-publisher

curl --cacert airunwayca.crt `
  -X POST "$endpoint/v1/chat/completions" `
  -H "Content-Type: application/json" `
  -H "x-ms-cleanroom-authorization: Bearer $token" `
  -d '{
    "model": "airunway-tinyllama",
    "messages": [ { "role": "user", "content": "Name three primary colors." } ],
    "max_tokens": 50
  }'
```

Expected: an OpenAI‑style chat completion, e.g. *"1. Red 2. Yellow 3. Blue"*.

---

## Cleanup

```powershell
kubectl delete modeldeployment.airunway.ai airunway-tinyllama -n default
# the owner-referenced cleanroom ModelRegistration + ModelDeployment are GC'd
```

To tear down everything, delete the environment and the AKS resource group:

```powershell
kubectl cleanroom environment delete clean-env
az group delete --name $RG --yes --no-wait
```
