# CleanRoom CI/CD Migration (GitHub Actions → Azure DevOps)

> Living document. Captures the knowledge behind the EMU→ADO pipeline migration so
> the setup is understandable and reproducible without tribal knowledge.
> Lives in `.pipelines/` on purpose — it travels with the pipelines it describes.

---

## 1. What this migration is about

**Objective (simple):** move CleanRoom's CI/CD from **GitHub Actions** (running on the
`azure-cleanroom-emu` self-hosted 1ES pool) to **Azure DevOps (ADO) OneBranch
pipelines** (org `msazure`, project `One`), while keeping the same build/test behavior
and improving supply-chain security.

**Why:** align with the org-standard **OneBranch/1ES** platform (governed builds, SBOM,
CodeQL, Container Secure Supply Chain) and remove GitHub-Actions-only constructs.

**Scope:** the workflows under `.github/workflows/*` were re-implemented as ADO YAML
pipelines under `.pipelines/*`, using OneBranch templates. Test/sample scripts under
`test/` and `samples/` were adjusted only where the new environment required it
(no product runtime code changes).

**Pipelines (ADO, project `msazure/One`):**

| Pipeline | OneBranch template | Prod? | Pool | Feed |
|---|---|---|---|---|
| `OneBranch.Official` | `OneBranch.Official.CrossPlat` | ✅ Official (signed) | governed | internal |
| `PR-Validation` | `OneBranch.NonOfficial.CrossPlat` | ❌ | isCustom `cleanroom-emu-ado` | public |
| `ReleaseAndTest` | `OneBranch.NonOfficial.CrossPlat` | ❌ | isCustom `cleanroom-emu-ado` | public |
| `TpcdsStresstest` (spark stress) | `OneBranch.NonOfficial.CrossPlat` | ❌ | isCustom `cleanroom-emu-ado` | public |
| `BigDataLonghaul` | `OneBranch.NonOfficial.CrossPlat` | ❌ | isCustom `cleanroom-emu-ado` | public |
| `BlobfuseNightly` | `OneBranch.NonOfficial.CrossPlat` | ❌ | isCustom `cleanroom-emu-ado` | public |
| `ReleaseVerificationNightly` | `OneBranch.NonOfficial.CrossPlat` | ❌ (disabled, mirrors GitHub) | isCustom `cleanroom-emu-ado` | public |
| `CleanupStalePrResources` | `OneBranch.NonOfficial.CrossPlat` | ❌ | governed (container) | n/a |
| `CleanupStaleBvtResources` | `OneBranch.NonOfficial.CrossPlat` | ❌ | governed (container) | n/a |

Only `OneBranch.Official` uses the **governed pool + internal NuGet feed** (and is the
only CodeQL surface). Everything else runs on the **isCustom `cleanroom-emu-ado`** pool
with **public feeds** (the Dockerfiles self-compile using public nuget.org).

---

## 2. Authentication — end to end

There are **two independent auth hops**, and they use **different identities**:

```
  ┌────────────┐   (A) GitHub App        ┌───────────────────┐   (B) WIF / OIDC        ┌──────────────────┐
  │  GitHub     │ ───────────────────────▶│  Azure DevOps     │ ───────────────────────▶│  Azure resources  │
  │ (source)    │   checkout + triggers   │  (pipelines)      │  federated identity     │  (ACR, AKS, KV…)  │
  └────────────┘   = Azure Pipelines app  └───────────────────┘  = Managed Identities   └──────────────────┘
```

- **Hop A — GitHub → ADO:** how ADO reads the repo and reacts to CI/PR events.
- **Hop B — ADO → Azure:** how a running pipeline authenticates to Azure to deploy,
  push images, create resources, etc.

### 2.0 Service connections & managed identities (quick reference)

Every ADO service connection and Azure managed identity the migrated pipelines use, with
direct links. All service connections live in `msazure/One`; both managed identities live
in RG `cleanroom-emu-actions-rg` (sub `3b9ce031-…`, tenant `72f988bf-…`).

**Service connections** — [all connections in `msazure/One`](https://dev.azure.com/msazure/One/_settings/adminservices):

| Service connection | Type / scheme | Backing identity | Used for | Link |
|---|---|---|---|---|
| `github.com_azure-core (12)` | GitHub App / `InstallationToken` | Azure Pipelines app (`azure-pipelines[bot]`) | **Hop A** — source checkout + CI/PR triggers for **all 9** pipelines | [open](https://dev.azure.com/msazure/One/_settings/adminservices?resourceId=0604803e-efe2-4d67-a583-d2923fc25abb) |
| `cleanroom-pr-oidc` | azurerm / `WorkloadIdentityFederation` | `cleanroom-emu-pr-mi` | **Hop B** — default PR/BVT Azure work + stale-resource cleanup | [open](https://dev.azure.com/msazure/One/_settings/adminservices?resourceId=438432a3-0271-4894-9b3c-3db2a5f11d75) |
| `cleanroom-prod-release-oidc` | azurerm / `WorkloadIdentityFederation` | `cleanroom-emu-bvt-mi` | **Hop B** — release path (image/CLI/helm promotion, ACR push) + nginx-hello BVT | [open](https://dev.azure.com/msazure/One/_settings/adminservices?resourceId=e6b77a5f-7512-4955-8f36-22456ad98b3b) |

**Managed identities** — both in RG [`cleanroom-emu-actions-rg`](https://portal.azure.com/#@72f988bf-86f1-41af-91ab-2d7cd011db47/resource/subscriptions/3b9ce031-3a87-4e70-a8bb-75ec1d90ad22/resourceGroups/cleanroom-emu-actions-rg/overview):

| Managed identity | clientId | principalId (objectId) | Used by SC | Link |
|---|---|---|---|---|
| `cleanroom-emu-pr-mi` | `4d2f1740-9546-4b0f-91fb-bfd528455315` | `ff50ca9a-012f-4739-bc6b-a92d42b08a00` | `cleanroom-pr-oidc` | [open](https://portal.azure.com/#@72f988bf-86f1-41af-91ab-2d7cd011db47/resource/subscriptions/3b9ce031-3a87-4e70-a8bb-75ec1d90ad22/resourcegroups/cleanroom-emu-actions-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/cleanroom-emu-pr-mi/overview) |
| `cleanroom-emu-bvt-mi` | `394518f3-5105-428e-9f54-6d1c9f432703` | `8d6fe26a-4a14-4f34-b0a4-054f157ca75b` | `cleanroom-prod-release-oidc` | [open](https://portal.azure.com/#@72f988bf-86f1-41af-91ab-2d7cd011db47/resource/subscriptions/3b9ce031-3a87-4e70-a8bb-75ec1d90ad22/resourcegroups/cleanroom-emu-actions-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/cleanroom-emu-bvt-mi/overview) |

> All 9 pipeline definitions check out source through the **GitHub App** connection
> (`0604803e…`) — no pipeline uses a personal OAuth/PAT GitHub connection. Details of each
> hop follow below.

### 2A. GitHub → Azure DevOps (source checkout + triggers)

**Mechanism: the Azure Pipelines GitHub App** (the Microsoft-published, "stock" app —
`https://github.com/apps/azure-pipelines`).

- The pipeline **checks out the repo and reacts to triggers as the Azure Pipelines app
  identity** (`azure-pipelines[bot]`), **not** any personal GitHub account.
- The ADO service connection backing it: **`github.com_azure-core (12)`**
  (`id 0604803e-efe2-4d67-a583-d2923fc25abb`, type `GitHub`, scheme `InstallationToken`).
  The `(12)` in the name is the **app installation id** on the `azure-core` org.
- This is the **same mechanism optimus uses** (verified: optimus's repo pipeline
  check-runs are posted by `app.slug=azure-pipelines`).
- All 9 pipeline definitions reference this connection (`0604803e…`); no personal
  credentials are used for source auth.

**Links:**
- App (Microsoft's public app): [github.com/apps/azure-pipelines](https://github.com/apps/azure-pipelines)
- Its installation on the `azure-core` org: [org GitHub App installations](https://github.com/organizations/azure-core/settings/installations)
- The ADO service connection it backs: [github.com_azure-core (12)](https://dev.azure.com/msazure/One/_settings/adminservices?resourceId=0604803e-efe2-4d67-a583-d2923fc25abb)

**Reference doc:**
[Build GitHub repositories — Azure Pipelines § GitHub App authentication](https://learn.microsoft.com/en-us/azure/devops/pipelines/repos/github?view=azure-devops&tabs=yaml#github-app-authentication).
The GitHub App connection is created by installing the app on GitHub (not from the "New
service connection" dialog), and a repo's CI/PR triggers via the app map to **one** ADO
org (ours: `msazure`).

#### How pipeline runs map to GitHub commits / branches

- **PR runs → the PR head commit.** When a PR is opened/updated, the Azure Pipelines
  GitHub App queues `PR-Validation` against the PR's **merge/head commit** and posts the
  result back to that commit as a **GitHub check-run** (author `azure-pipelines[bot]`), so
  the pass/fail shows inline on the PR. The check-run links to the exact ADO build; the ADO
  build's **source version** is that same commit SHA. This is branch-policy driven (`pr:
  none` in the YAML is expected — GitHub PR triggers are configured by the ADO branch
  policy, not the YAML).
- **CI runs → the pushed commit.** `OneBranch.Official` runs on push to `main`/tags; each
  run's source version is the pushed commit and it posts a commit status back to GitHub.
- **Manual / scheduled runs (BVTs) → a chosen branch.** `ReleaseAndTest`, `BigDataLonghaul`,
  `TpcdsStresstest`, `BlobfuseNightly`, and the cleanup pipelines are run **manually or on a
  cron against a specific branch** (default `develop`). A manual BVT run is pinned to the
  branch you queue it on (its source version = that branch's tip at queue time), so a BVT
  validating a feature branch is queued with `sourceBranch=refs/heads/<branch>`. Schedules
  read cron **only** from the YAML on the definition's default branch (`develop`) — so a
  new-in-PR pipeline only starts auto-firing after this PR merges.

### 2B. Azure DevOps → Azure resources (deploy / ACR / AKS / Key Vault)

**Mechanism: Workload Identity Federation (WIF / OIDC).** ADO service connections
federate to **user-assigned Managed Identities (MI)** in Entra ID — **no secrets/passwords
stored in ADO**. At runtime ADO mints a short-lived OIDC token, exchanges it for an Entra
token for the MI, and the MI's Azure RBAC governs what the pipeline can do.

The manual login recipe lives in `.pipelines/templates/steps/login-to-azure.yml`
(a manual WIF token exchange + `az login --federated-token`, because on the isCustom
bare-VM pool `AzureCLI@2` login does not persist across steps).

> **Same pattern as optimus (verified).** `azure-core/optimus`'s
> `.pipelines/templates/e2e-cloudtest-jobs.yml` uses the identical manual WIF exchange —
> `curl "$SYSTEM_OIDCREQUESTURI?...&serviceConnectionId=…"` → `az login --service-principal
> --federated-token` — on its own `optimus-e2e-ado` 1ES Hosted Pool via the
> `optimus-e2e-azure-wif` WIF service connection. Only the identity/pool/SC **names**
> differ; the mechanism is the same for both the GitHub→ADO and ADO→Azure hops.

**Tenant (all identities):** `72f988bf-86f1-41af-91ab-2d7cd011db47` (corp / MSIT).
**Primary subscription:** `fccb68eb-8ccf-49a6-a69a-7ea3c2867e9c` (AzureCleanRoom-NonProd).

#### Identities (Entra managed identities) and service connections

| ADO service connection | SC id | Backing MI (clientId) | MI name | Used by |
|---|---|---|---|---|
| `cleanroom-pr-oidc` | `438432a3-0271-4894-9b3c-3db2a5f11d75` | `4d2f1740-9546-4b0f-91fb-bfd528455315` | `cleanroom-emu-pr-mi` | **default** for PR-Validation + most BVT jobs + the stale-resource cleanup pipelines (checkout-adjacent Azure work) |
| `cleanroom-prod-release-oidc` | `e6b77a5f-7512-4955-8f36-22456ad98b3b` | `394518f3-5105-428e-9f54-6d1c9f432703` | `cleanroom-emu-bvt-mi` | release path (image promotion / ACR push) + the nginx-hello BVT (needs ACR push) |

> Two service connections back two identities. `cleanroom-pr-oidc` (`cleanroom-emu-pr-mi`)
> covers PR/BVT **and** cleanup; the cleanup pipelines were previously on a separate
> `cleanroom-pr-cleanup-oidc` SC that shared the same MI, so it was consolidated into
> `cleanroom-pr-oidc` (no privilege change). `cleanroom-prod-release-oidc`
> (`cleanroom-emu-bvt-mi`) is kept separate on purpose: it pushes to the **release**
> registry (`cleanroomemubvtregistry`), so untrusted PR-triggered jobs can't reach it.

**MI identity resolution:**
- `cleanroom-emu-pr-mi` — clientId `4d2f1740-9546-4b0f-91fb-bfd528455315`,
  principalId (objectId) `ff50ca9a-012f-4739-bc6b-a92d42b08a00`.
- `cleanroom-emu-bvt-mi` — clientId `394518f3-5105-428e-9f54-6d1c9f432703`,
  SP objectId `8d6fe26a-4a14-4f34-b0a4-054f157ca75b`. Resource id:
  `/subscriptions/3b9ce031-3a87-4e70-a8bb-75ec1d90ad22/resourcegroups/cleanroom-emu-actions-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/cleanroom-emu-bvt-mi`.

> Note: `cleanroom-emu-bvt-mi` lives in subscription `3b9ce031-…`
> (AzureCleanRoom-Deployment-Test) even though the service connection's scope
> subscription is `fccb68eb-…` (NonProd). WIF federates by the **MI**, not the
> subscription, so this works — the ACR it pushes to (`cleanroomemubvtregistry`) is in
> `3b9ce031-…` where the MI has AcrPush.

#### Permissions granted (Azure RBAC)

- **`cleanroom-emu-bvt-mi`** (release identity `394518f3` / obj `8d6fe26a`):
  - **AcrPush** on **`cleanroomemubvtregistry`** (release registry; sub `3b9ce031-…`,
    RG `cleanroom-emu-actions-rg`) — pushes/promotes release images + the CLI wheel + helm
    charts + the nginx-hello OPA policy bundle.
  - Deploy roles on the **NonProd** sub (`fccb68eb-…`): Resource Group / Storage Account /
    Key Vault / AKS / Network / Managed Identity / Private DNS Zone / Virtual Machine
    Contributor, plus User Access Administrator + RBAC Administrator + Log Analytics /
    Monitoring Contributor + `CleanRoomCIAppRole`.
- **`cleanroom-emu-pr-mi`** (PR identity `4d2f1740` / obj `ff50ca9a`):
  - Deploy rights on the NonProd sub for PR/BVT scenario resource creation.
  - **Anonymous pull** on the release registry (registry has `anonymousPullEnabled=true`),
    so BVTs can pull release images **without** AcrPull — the `az acr login` steps for
    pull-only BVT jobs are best-effort.

#### How workloads consume the identity on the pool (no UMI → `az login` session)

The isCustom `cleanroom-emu-ado` pool has **no assigned user-assigned managed identity**
(1ES disallows it — `UmiUsageNoLongerAllowed`), so there is **no IMDS/MSI identity** on the
agent VM. Everything authenticates through the **WIF `az login` service-principal session**
that `steps/login-to-azure.yml` establishes at the top of each job. Two consequences worth
calling out:
- **blobfuse BVT** — the mount configs `test/blobfuse/testdata/config/block_cache.yaml` and
  `block_cache_cpk.yaml` changed `azstorage` `mode: msi` → **`mode: azcli`**, so blobfuse
  authenticates to storage via that same `az login` SP (which holds **Storage Blob Data
  Contributor**) instead of IMDS. Same identity resolves on both GitHub and ADO.
- **`aad-helpers.ps1`** — `GetLoggedInEntityObjectId` reads the SP objectId from the access
  token's `oid` claim rather than `az ad sp show` (Graph), because the release identity has
  Azure RBAC but not Graph directory-read (see §4.4).

#### Federated identity credentials (Entra)

Each WIF service connection has a matching **federated credential** on its MI:
- `cleanroom-prod-release-oidc` FIC on `cleanroom-emu-bvt-mi`
  (RG `cleanroom-emu-actions-rg`, sub `3b9ce031-…`):
  - issuer `https://login.microsoftonline.com/72f988bf-86f1-41af-91ab-2d7cd011db47/v2.0`
  - subject `/eid1/c/pub/t/v4j5cvGGr0GRqy180BHbRw/a/rISbSSETf0KqFyZ8ppdXmA/sc/41bf5486-7392-4b7a-a7e3-a735c767e3b3/e6b77a5f-7512-4955-8f36-22456ad98b3b`
  - audience `api://AzureADTokenExchange`
  - Recreate: `az identity federated-credential create --name cleanroom-prod-release-oidc --identity-name cleanroom-emu-bvt-mi --resource-group cleanroom-emu-actions-rg --issuer <issuer> --subject <subject> --audiences api://AzureADTokenExchange`

#### How these were created (cmdlets & APIs)

For reproducibility, the exact cmdlets / REST APIs used to create each object. ADO REST
calls use a bearer token for resource `499b84ac-1321-427f-aa17-267ca6975798` (Azure DevOps):
`$tok = az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 --query accessToken -o tsv`.
Org `msazure`, project `One` (id `b32aa71e-8ed2-41b2-9d77-5bc261222004`), org instanceId
`41bf5486-7392-4b7a-a7e3-a735c767e3b3`.

**1. WIF Azure RM service connections** (`cleanroom-pr-oidc`, `cleanroom-prod-release-oidc`,
and the now-consolidated `cleanroom-pr-cleanup-oidc`) — created via ADO REST (the CLI has no
WIF-manual create), then a matching Entra federated credential added on the MI:

```powershell
# a) Create the WIF service connection (scheme=WorkloadIdentityFederation, creationMode=Manual)
POST https://dev.azure.com/msazure/One/_apis/serviceendpoint/endpoints?api-version=7.1
{
  "name": "cleanroom-pr-oidc",
  "type": "azurerm",
  "authorization": { "scheme": "WorkloadIdentityFederation",
    "parameters": { "serviceprincipalid": "<MI clientId>", "tenantid": "72f988bf-…" } },
  "data": { "creationMode": "Manual", "subscriptionId": "fccb68eb-…", "subscriptionName": "AzureCleanRoom-NonProd" },
  "serviceEndpointProjectReferences": [ { "projectReference": { "id": "b32aa71e-…", "name": "One" }, "name": "cleanroom-pr-oidc" } ]
}

# b) Add the matching federated credential on the MI (subject = the SC's issuer/subject)
az identity federated-credential create `
  --name ADO-One-cleanroom-pr-oidc `
  --identity-name cleanroom-emu-pr-mi --resource-group cleanroom-emu-actions-rg `
  --issuer  https://login.microsoftonline.com/72f988bf-86f1-41af-91ab-2d7cd011db47/v2.0 `
  --subject /eid1/c/pub/t/v4j5cvGGr0GRqy180BHbRw/a/rISbSSETf0KqFyZ8ppdXmA/sc/41bf5486-…/<scId> `
  --audiences api://AzureADTokenExchange
```

**2. GitHub App service connection** (`github.com_azure-core (12)`, scheme
`InstallationToken`) — **not** created by any cmdlet/REST call. It is materialized by
**installing the Azure Pipelines GitHub App** on the `azure-core` org (the "New service
connection" dialog only offers OAuth/PAT). See §2A.

**3. Authorizing a connection for a pipeline** (needed before a def can use an SC) — ADO REST
`pipelinePermissions` PATCH:

```powershell
PATCH https://dev.azure.com/msazure/One/_apis/pipelines/pipelinePermissions/endpoint/<scId>?api-version=7.1-preview.1
{ "pipelines": [ { "id": <defId>, "authorized": true } ] }
```

**4. Adding owners (Administrator role) on a connection** — ADO REST `securityroles`:

```powershell
PUT https://dev.azure.com/msazure/_apis/securityroles/scopes/distributedtask.serviceendpointrole/roleassignments/resources/<projectId>_<scId>?api-version=7.1-preview.1
[ { "roleName": "Administrator", "userId": "<ado identity id>" } ]
```

**5. Repointing a definition's source-checkout connection** — GET the definition, set
`repository.properties.connectedServiceId`, then PUT it back:

```powershell
GET  https://dev.azure.com/msazure/One/_apis/build/definitions/<defId>?api-version=7.1
#   $def.repository.properties.connectedServiceId = '<App SC id>'
PUT  https://dev.azure.com/msazure/One/_apis/build/definitions/<defId>?api-version=7.1  (body = $def)
```

#### Variable groups (non-secret wiring)

| Variable group | id | Contents |
|---|---|---|
| `cleanroom-release-vars` | 7673 | `RELEASE_ACR_NAME=cleanroomemubvtregistry`, `AZURE_SUBSCRIPTION_ID=fccb68eb-…`, `AZURE_TENANT_ID=72f988bf-…`, `AZURE_CLIENT_ID=394518f3-…` |
| `cleanroom-pr-vars` | 7648 | PR-side equivalents (client `4d2f1740-…`) |

### 2C. Registries (reference)

| Registry | Sub | Purpose |
|---|---|---|
| `cleanroomemubvtregistry` | `3b9ce031-…` | **release** registry (`/internal/azurecleanroom/…`); `anonymousPullEnabled=true`; `cleanroom-emu-bvt-mi` has AcrPush |
| `cleanroomemuprregistry` | `fccb68eb-…` | **PR** build registry (`ACR_URL`) |
| `cleanroombuild.azurecr.io` | — | Docker Hub **mirror** used by Dockerfiles (CSSC) |

---

## 3. Auth summary (one screen)

- **GitHub → ADO:** stock **Azure Pipelines GitHub App** (`azure-pipelines[bot]`), SC
  `github.com_azure-core (12)` (`0604803e…`). No personal creds. Same as optimus.
- **ADO → Azure:** **WIF/OIDC**, no stored secrets. Two managed identities:
  - `cleanroom-emu-pr-mi` (`4d2f1740` / `ff50ca9a`) via `cleanroom-pr-oidc` — PR/BVT work.
  - `cleanroom-emu-bvt-mi` (`394518f3` / `8d6fe26a`) via `cleanroom-prod-release-oidc` —
    release/ACR-push + nginx-hello BVT.
- **Tenant** `72f988bf-…`; **NonProd sub** `fccb68eb-…`; **MI/ACR sub** `3b9ce031-…`.

---

## 4. PR changes (what actually changed vs `develop`)

This branch (`test/pipeline-pilot-validation`, PR #928) has two kinds of change:
**(a)** the pipeline definitions themselves (the migration), and **(b)** small
environment-driven adjustments to test/sample harness code and Dockerfiles. **No product
runtime behavior changes** — the one `src/` edit is a CI wait-timeout bump (see below).

### 4.1 Container Secure Supply Chain (CSSC) — registry mirroring

**What:** every public Docker Hub base image referenced by a `FROM` in the repo's
Dockerfiles was **re-pointed to an approved internal mirror**. Only the registry prefix
changed — **image names and tags are identical**, so the built artifacts are unchanged.

**Why:** OneBranch/1ES governance (CSSC) forbids pulling base images directly from public
registries like `docker.io`. Builds must pull from an approved mirror so the supply chain
is auditable and reproducible. This applies to **both** the Official and NonOfficial
OneBranch pipelines (the Dockerfiles are shared).

**Mirror mapping:**

| Original (`FROM`) | Rewritten to |
|---|---|
| `golang:<tag>` (1.25, 1.26, 1.26-alpine, 1.26.3) | `cleanroombuild.azurecr.io/mirror/docker/library/golang:<tag>` |
| `python:3.12`, `python:3.12-slim` | `cleanroombuild.azurecr.io/mirror/docker/library/python:<tag>` |
| `python:3.10-slim` | `mcr.microsoft.com/mirror/docker/library/python:3.10-slim` |
| `ubuntu:24.04` | `mcr.microsoft.com/mirror/docker/library/ubuntu:24.04` |
| `otel/opentelemetry-collector-contrib:0.103.0` | `cleanroombuild.azurecr.io/otel/opentelemetry-collector-contrib:0.103.0` |

**Why two mirror registries (each image goes to the mirror that actually stocks it):**
- `mcr.microsoft.com/mirror/docker/library/…` — MCR's public Docker Hub mirror. Verified
  to carry **`ubuntu`** and **`python` up to 3.11** (incl. `3.10-slim`), so those use MCR.
- `cleanroombuild.azurecr.io/mirror/docker/library/…` — the CleanRoom build ACR's Docker
  Hub mirror (anonymous-pull enabled). Used for **`golang`** and **`python:3.12`** because
  **MCR does not mirror them**: `mcr.microsoft.com/mirror/docker/library/golang` returns
  404 (MCR has no Docker-Hub-`library/golang` — its `oss/go/microsoft/golang` is a
  *different* MS Go distribution, not a byte-for-byte mirror of upstream `golang`), and
  MCR's python mirror tops out at 3.11 (no 3.12). `cleanroombuild` also hosts the
  `otel/opentelemetry-collector-contrib` mirror (under `/otel/`, not `/mirror/docker/…`).
  All rewrites keep the **same upstream image + tag** — a pure prefix swap, not a distro
  change.

**Dockerfiles updated (11):**
`build/docker/Dockerfile.api-server-proxy`, `Dockerfile.cleanroom-boot`,
`Dockerfile.cvm-attestation-agent`, `Dockerfile.cvm-attestation-verifier`,
`Dockerfile.karpenter-provider-accr`, `Dockerfile.kubelet-proxy`;
`poc/csi-driver/Dockerfile`; `src/tools/python-linter/Dockerfile.python-linter`;
`src/tools/telemetryviewer/docker/Dockerfile.otelcollector-local`;
`src/workloads/inferencing/ohttp/tests/Dockerfile.mock`;
`test/onebox/multi-party-collab/ml-training/consumer/application/Dockerfile.train`.

> **Not a mirror change:** `src/workloads/frontend/helmchart/values.yaml` is also in this
> PR but is **unrelated to CSSC** — it blanks the hardcoded default image names
> (`frontendImage: "frontend-service"` → `""`, `image: "cgs-client"` → `""`) so they are
> filled from `{registry}/{name}:{tag}` at deploy time instead of a bare name.

> See §2C for the registry table. `cleanroombuild.azurecr.io` is the Docker Hub **mirror**
> (distinct from the PR build registry `cleanroomemuprregistry` and the release registry
> `cleanroomemubvtregistry`).

### 4.2 Pipeline definitions (the migration itself)

- **New pipelines / templates:** `ReleaseAndTest.yml`, `TpcdsStresstest.yml`,
  `release-build-jobs.yml`, `steps/build-container-image-custom.yml`, `nuget.config`,
  and this `MIGRATION.md`.
- **Removed:** `Release/OneBranch.Release.Prod.yml` (superseded by the new release flow).
- **Updated OneBranch pipelines:** `OneBranch.Official.yml`, `PR-Validation.yml`,
  `BlobfuseNightly.yml`, `CleanupStalePrResources.yml`, `CleanupStaleBvtResources.yml`,
  `BigDataLonghaul.yml`, `ReleaseVerificationNightly.yml`, plus the shared job/step
  templates under `.pipelines/templates/**` (build, ccf, cgs, cluster, flex-node,
  multi-party-collab, workload-samples, release, login, pick-location, variables).
- **Auth repoint (source):** all 6 pipelines moved GitHub source auth from the personal
  **OAuth** connection to the stock **Azure Pipelines GitHub App** connection — see §2A.

### 4.3 Region / capacity handling (BVT)

- **`steps/pick-location.yml` + `release-and-test-jobs.yml`:** the random BVT region set
  was trimmed to **`westeurope`, `northeurope`, `centralindia`** — **`eastasia`/
  `southeastasia` removed** after confidential-ACI capacity exhaustion there caused the
  big-data Spark executor pod to hang (`FailedCreatePodSandBox`, "insufficient capacity
  in eastasia").
- **Big-data pinned to `centralindia`:** `bvt_setup_env_aks` and
  `bvt_big_data_query_analytics_aks` are pinned to **`centralindia`** (not the random
  pick) because only that region is capacity-validated for the big-data confidential-ACI
  executors (5 CPU / 27.9 GB). This matches the hard-pins already used by
  `BigDataLonghaul.yml`, `TpcdsStresstest.yml` and the PR workload samples. Lightweight
  BVTs keep the random pick.

### 4.4 Robustness / harness fixes (test & sample scripts)

These are **CI-environment robustness fixes only** (test/sample harness), not product code:

| File | Change | Why |
|---|---|---|
| `src/cleanroom-cluster/…/Kubectl.cs` | Spark-operator readiness wait **6 → 12 min** | operator occasionally slow to become ready on fresh AKS; the only `src/` edit, a CI wait bump (no behavior change) |
| `test/…/big-data-query-analytics/submit-sql-job.py` | `MAX_PARALLEL_SQL_JOBS` **4 → 1** (serial) | on the fixed-capacity confidential cluster (`scaleSku=small` caps executors at 5) 4 concurrent Spark apps starve for executor slots and time out (verified: parallel=4 timed a query out at 30 min, build 176373058); serial conserves executor-seconds so adds no real wall-clock |
| `samples/common/infra-scripts/aad-helpers.ps1` | `GetLoggedInEntityObjectId` derives objectId from the access-token `oid` claim (JWT decode) instead of `az ad sp show` (Graph); Graph fallback retained | the WIF MI has no Graph read; avoids a Graph dependency in BVT |
| `samples/ccf/azcli/recover-ccf.ps1` | added `Invoke-CcfNodeProbe` retry (10× / 6 s backoff) | cold-TLS handshake flakes (curl exit 35) on CACI |
| `test/…/get-telemetry-utils.ps1` | Loki readiness wait **1 → 5 min** | Loki slow to become ready on fresh cluster |
| `test/blobfuse/testdata/config/block_cache.yaml` + `block_cache_cpk.yaml` | `azstorage` auth **`mode: msi` → `mode: azcli`** | the 1ES custom pool has **no assigned UMI** (`UmiUsageNoLongerAllowed`), so IMDS/`msi` has no identity; `azcli` reuses the WIF `az login` SP session, which holds Storage Blob Data Contributor — see §2B |
| `test/…/big-data-query-analytics/delete-bucket.ps1` | reset `$LASTEXITCODE = 0` at script end | ADO runs the script **in-session** (`./delete-bucket.ps1`) whereas GitHub ran it in a **child** process (`pwsh ./delete-bucket.ps1`); in-session, the `head-bucket` probe's non-zero exit (a valid “delete-if-exists” no-op) leaks to the task's `$LASTEXITCODE` and ADO fails the step on a non-zero end exit |
| `test/…/nginx-hello/run-collab-aci.ps1` | endpoint warmup wait **5 → 10 min** | confidential (CACI) nginx behind ccr-proxy needs SEV-SNP attestation + TLS + LB warmup after the app reports started |

### 4.5 Release publishing additions

`steps/release-groups.yml` now also publishes, alongside the container images:
- the **CLI wheel** (`cli/cleanroom-whl:<tag>`),
- the **workload helm charts** (frontend-service; spark analytics-agent/frontend;
  kserve agent/frontend), and
- the **release-metadata catalog** (`oci://<acr>/internal/azurecleanroom/release-metadata`),
  published **last** (after all images are promoted, since it digest-pins them) and only
  for the `internal` (release-to-test) environment.

Each of these was a **dropped-release-artefact gap** — GitHub Actions published them via
dedicated release steps that the initial ADO migration didn't carry over, so BVTs failed
with `<artefact>:<tag>: not found`:
- CLI wheel → `Install az cleanroom extension` failed (`cli/cleanroom-whl:<tag>: not found`).
- Workload charts → `setup-env-aks` failed (`workloads/helm/frontend-service:<ver>: not found`).
- release-metadata → every **CCF-based** BVT (nginx-hello, ml-training, encrypted-storage,
  and setup-env-aks's CCF-Governance) failed at `az cleanroom ccf network up`
  (`release-metadata:<ver>: not found`).

The prod MCR / GitHub-pages release-metadata catalog is a separate GitHub-specific flow
(`.github/scripts/release-metadata.ps1`, `gh release` + chart-releaser) and is not part of
this ADO path. All of the above are pushed by `cleanroom-emu-bvt-mi` (AcrPush) — see §2B.

---

## 5. Agent pool & the Prod / non-Prod split

### 5.1 Three execution models

OneBranch jobs run in one of **three** places, chosen per job by the `pool:` block:

| Model | `pool:` block | Where it actually runs | Used for |
|---|---|---|---|
| **Governed native** | `type: linux` (Official templates) | 1ES **governed** hosted pool | `OneBranch.Official` native C#/Go builds (CodeQL surface) |
| **Governed image-build** | `type: docker` | 1ES governed **image-build** pool | `OneBranch.Official` container image builds (SBOM/provenance/signing via `onebranch.pipeline.imagebuildinfo@1`) |
| **Governed container** | `type: linux` (no custom pool) + `LinuxContainerImage` | job runs **inside** `mcr.microsoft.com/onebranch/azurelinux/build:3.0` on the governed pool | the two **cleanup** pipelines (just run `az` cleanup, no self-hosted infra needed) |
| **Self-hosted (isCustom)** | `type: linux` + `isCustom: true` + `name: cleanroom-emu-ado` | our **`cleanroom-emu-ado`** pool | everything that needs Docker-in-VM, AKS/CACI deploys, and BVTs |

> **Why two governed sub-pools for Official?** The governed **image-build** (`type: docker`)
> pool is image-build-**only** — it silently drops `Bash@3`/`PowerShell@2`/`AzureCLI@2`.
> So Official splits into `build_solutions` (`type: linux`, native + tag derivation +
> CodeQL) → `build_images` (`type: docker`, governed image build), the latter reusing the
> former's derived tag.

### 5.2 Prod vs non-Prod, per pipeline

**"Prod" = the signed, governed Official pipeline** (OneBranch `Official.CrossPlat`
template, governed pools, internal NuGet feed, CodeQL). Everything else is **non-Prod**
(OneBranch `NonOfficial.CrossPlat`), on public feeds — most of it on our self-hosted
`cleanroom-emu-ado` pool.

| Pipeline | Def | OneBranch template | Prod? | Pool model | Trigger | Last ✓ build |
|---|---|---|---|---|---|---|
| `OneBranch.Official` | 472078 | `Official.CrossPlat` | ✅ **Prod** | governed native + governed image-build | push to `main` + tags; weekly cron (Tue 04:17 UTC) | [176373086](https://dev.azure.com/msazure/One/_build/results?buildId=176373086) (2026-08-13) |
| `PR-Validation` | 472072 | `NonOfficial.CrossPlat` | ❌ | **isCustom `cleanroom-emu-ado`** | PR (branch policy build validation; `pr: none` in YAML is expected for GitHub repos) | none yet — latest [176373058](https://dev.azure.com/msazure/One/_build/results?buildId=176373058) failed on OSS infra issues (disk-space + ccf-upgrade); BDA serial fix re-run pending |
| `ReleaseAndTest` | 472636 | `NonOfficial.CrossPlat` | ❌ | **isCustom `cleanroom-emu-ado`** (8 BVT jobs) | nightly cron (00:00 UTC) + manual (tag/releaseType) | [175969297](https://dev.azure.com/msazure/One/_build/results?buildId=175969297) (2026-08-11) |
| `BlobfuseNightly` | 472020 | `NonOfficial.CrossPlat` | ❌ | **isCustom `cleanroom-emu-ado`** | nightly cron (03:00 UTC) + manual | [176373108](https://dev.azure.com/msazure/One/_build/results?buildId=176373108) (2026-08-13) |
| `BigDataLonghaul` | 472070 | `NonOfficial.CrossPlat` | ❌ | **isCustom `cleanroom-emu-ado`** | nightly cron (00:00 UTC) + manual (duration/pause/chaos) | none yet — latest [176410959](https://dev.azure.com/msazure/One/_build/results?buildId=176410959) failed (`cmake not found` build-step, unrelated to migration auth/pool) |
| `TpcdsStresstest` | 472245 | `NonOfficial.CrossPlat` | ❌ | **isCustom `cleanroom-emu-ado`** | weekday cron (06:00 UTC Mon–Fri, scale per weekday) + manual | [176373161](https://dev.azure.com/msazure/One/_build/results?buildId=176373161) (2026-08-13, SF10 293m) |
| `ReleaseVerificationNightly` | 472031 | `NonOfficial.CrossPlat` | ❌ | **isCustom `cleanroom-emu-ado`** | nightly cron (02:00 UTC) + manual (currently disabled, mirrors GitHub) | n/a — definition disabled (mirrors GitHub) |
| `CleanupStalePrResources` | 471410 | `NonOfficial.CrossPlat` | ❌ | **governed container** | daily cron (01:00 UTC) + manual | [176373141](https://dev.azure.com/msazure/One/_build/results?buildId=176373141) (2026-08-13) |
| `CleanupStaleBvtResources` | 471954 | `NonOfficial.CrossPlat` | ❌ | **governed container** | daily cron (02:00 UTC) + manual | [176373150](https://dev.azure.com/msazure/One/_build/results?buildId=176373150) (2026-08-13) |

> **Last-successful-build snapshot** taken 2026-08-13 from `dev.azure.com/msazure/One`.
> The two pipelines without a green build (`PR-Validation`, `BigDataLonghaul`) are blocked
> only by **infra-side OSS-repo issues** Anant is fixing (disk-space — already fixed on
> `develop`; ccf-upgrade tests) plus a `cmake not found` build-step — none are caused by the
> EMU→ADO migration (auth, pools, triggers all validated green on the other 6 pipelines).
> `ReleaseVerificationNightly` is intentionally disabled to mirror the GitHub state.

> **Trigger notes.** All schedules are declared with `always: true` on branch `develop`
> and only register once the pipeline YAML is on the definition's **default branch**
> (`develop`) — so a new-in-PR pipeline (e.g. ReleaseAndTest, TpcdsStresstest) starts
> firing its cron only after this PR merges. `OneBranch.Release.Prod` (def 472251) was the
> prod-release pipeline; its YAML is **removed** in this PR (the GitHub `release.yml` never
> runs on azure-core — no `production` environment), and its ADO definition is disabled.

Only `OneBranch.Official` uses the governed pool + internal feed + CodeQL. The isCustom
pipelines run on `cleanroom-emu-ado` with **public feeds** (Dockerfiles self-compile from
public nuget.org / the CSSC mirrors in §4.1).

**Why the cleanup pipelines use the governed container, not `cleanroom-emu-ado`.** They
*could* run on the custom pool, but they **shouldn't**: `cleanup.ps1` is **pure
`az`/PowerShell (no docker)** — it does ~2000 sequential `az ad sp show` existence checks +
RG deletes over ~2 hours. Running it on the self-hosted pool would (a) **occupy one of the
pool's scarce ephemeral VMs** (`maxPoolSize 4`) for ~2 h, starving the BVT/build jobs that
genuinely need Docker-in-VM, and (b) drag it onto the isCustom pool (which carries the 1ES
PT custom-pool warning, see §5.3) for no benefit. The stock OneBranch **governed
container** (`mcr.microsoft.com/onebranch/azurelinux/build:3.0`) is right-sized (no docker
needed), fully governed, and doesn't consume custom-pool capacity — so it's the correct
home for a job that just runs `az`.

### 5.3 The `cleanroom-emu-ado` self-hosted pool — how it was created

The BVT/deploy work needs **Docker-in-VM + AKS/CACI deployment**, which the governed
hosted pools don't provide — so we run those jobs on a **self-hosted 1ES Hosted Pool**
we own (resource type `Microsoft.CloudTest/hostedpools`).

**Origin:** created **2026-08-03** by **cloning the existing GitHub-Actions CloudTest pool
`azure-cleanroom-emu`** and switching its **organization profile from GitHub → Azure
DevOps** (a GitHub-type CloudTest pool cannot attach to an ADO org — the org-type is the
blocker, so a clone + retarget was required rather than an in-place edit).

**Resource:** `Microsoft.CloudTest/hostedpools` named **`cleanroom-emu-ado`**. ARM template
kept at `scripts-local/ado-pool.template.json`. Key properties:

| Property | Value |
|---|---|
| ADO org | `https://dev.azure.com/msazure` (pool `cleanroom-emu-ado`, parallelism 4) |
| SKU | `Standard_D4ds_v4` (Standard tier) |
| Agents | **Stateless**, **ephemeral** (`ubuntu2204-image`, `isEphemeral: true`) — fresh VM per job |
| Image sub | `3b9ce031-3a87-4e70-a8bb-75ec1d90ad22` (Deployment-Test) |
| `maxPoolSize` | 4 |
| Data disk | 300 GiB, mounted at `/mnt/storage` (`Demand.WorkFolder`) — Docker data disk for image builds |
| Networking | accelerated networking + encryption on; no GPU |
| Stamp | `eus2-default` |

**How pipelines target it:** every self-hosted job uses
```yaml
pool:
  type: linux
  isCustom: true          # opt out of the governed hosted pool
  name: cleanroom-emu-ado  # our CloudTest pool
```

**1ES PT image compliance (resolved).** Because these are **custom** pools, every isCustom
job initially emitted a **non-blocking** OneBranch error in the *"Validate Hosted Pool
Information (1ES PT)"* step — *"since you use custom pools, 1ES PT requires an additional
artifact that needs to be added to your image"* plus *"Prerequisites file is not a valid
JSON file."* These are cosmetic today but become **warnings by 2026-03-01 and blocking by
2026-03-31** as OneBranch extends 1ES Pipeline Templates.

Root cause: our pool is already a **1ES Hosted Pool** (`Microsoft.CloudTest/hostedpools`),
so the pool itself was fine — but its image was not 1ES PT-compliant. `imageName:
ubuntu2204-image` is a separate **`Microsoft.CloudTest/images`** resource (RG
`cleanroom-build-infra-rg`) whose base pointed at **`MMSUbuntu22.04-Secure`** (Gen1 —
*"Azure Pipelines - Ubuntu 22.04"*), which lacks the 1ES PT prerequisite artifact.

Fix (applied, infra-only — no repo YAML, no pool redeploy): swap the image resource's
`properties.resourceId` to the **Gen2 1ESPT-compliant** equivalent from the 1ES PT
[create-agent-image](https://eng.ms/docs/coreai/devdiv/one-engineering-system-1es/1es-docs/1es-pipeline-templates/onboardingesteams/create-agent-image#option-1-use-1es-pt-compliant-image)
Option-1 table — **`MMSUbuntu22.04-g2-Secure`** (same subscription `723b64f0…` / gallery
`CloudTestGallery`), which *bundles the `linux-1es-pt-prerequisites-v2` artifact*:

```
az resource update \
  --ids .../resourceGroups/cleanroom-build-infra-rg/providers/Microsoft.CloudTest/images/ubuntu2204-image \
  --api-version 2024-07-05-preview \
  --set properties.resourceId=.../CloudTestGallery/images/MMSUbuntu22.04-g2-Secure/versions/latest
```

Notes: the pool SKU `Standard_D4ds_v4` (Ddsv4) supports Gen2, so no SKU change was needed;
this single image resource backs **all four** cleanroom CloudTest pools, so one swap fixes
them all; and because the pool is Stateless/ephemeral, the next job's fresh VM picks up the
new image immediately. Rollback: set `resourceId` back to `.../MMSUbuntu22.04-Secure/versions/latest`.

---

## 6. Still to document

*Sections still to be added as the review progresses: build/feed decoupling detail and the
OneBranch internal-feed / `nuget.config` wiring.*
