# How It Works: ModelRegistration

A `ModelRegistration` uploads a model to Azure Blob Storage, configures
OIDC-based workload identity, and creates governance documents in CGS
so the clean room workload can access the model. It runs a step
machine — one condition per reconcile loop.

## Conditions

| Condition          | What it means                                                          |
| ------------------ | ---------------------------------------------------------------------- |
| `ModelUploaded`    | Model files copied from HuggingFace to Azure Blob Storage (or skipped) |
| `CcfUserReady`     | Publisher CcfUser created and activated in consortium                  |
| `OidcIssuerReady`  | GovernanceService OIDC issuer URL is available                         |
| `AccessConfigured` | RBAC role assigned, federated credential created                       |
| `DatasetDocReady`  | Dataset governance document created, proposed, and accepted            |
| `ModelDocReady`    | Model governance document created, proposed, and accepted              |

## CLI output

```
$ kubectl cleanroom md create my-model \
    --environment my-env \
    --hf-model-id bartowski/Llama-3.2-1B-Instruct-GGUF \
    --hf-pattern "*Q4_K_M*" \
    --storage-account-id /subscriptions/.../storageAccounts/sa1 \
    --container-name models --encryption-mode CPK
  · Copying file 1/3: config.json
  · Copying file 2/3: Llama-3.2-1B-Instruct-Q4_K_M.gguf
  · Copying file 3/3: tokenizer.json
  ✓ ModelUploaded
  · CcfUser: waiting for activation...
  ✓ CcfUserReady
  · Waiting for OIDC issuer URL...
  ✓ OidcIssuerReady
  · Assigning RBAC role...
  · Creating federated credential...
  ✓ AccessConfigured
  · Dataset document created
  · Dataset document proposed
  · Dataset document accepted
  ✓ DatasetDocReady
  · Model document created
  · Model document proposed
  · Model document accepted
  ✓ ModelDocReady
ModelRegistration my-model is Ready
```

## Sequence diagram

```mermaid
sequenceDiagram
    participant CLI as kubectl-cleanroom
    participant K8s as Kubernetes API
    participant MD as ModelRegistration Controller
    participant HF as HuggingFace API
    participant Blob as Azure Blob Storage
    participant GS as GovernanceService CR
    participant AAD as Azure AD
    participant CGS as CGS Client

    CLI->>K8s: Create ModelRegistration CR
    K8s-->>MD: Reconcile

    Note over MD: Step 1 — Upload model
    MD->>HF: Fetch model file list
    loop Each file
        MD->>Blob: Check if blob exists (skip if so)
        MD->>Blob: Copy file from HF URL
        MD-->>K8s: Event: ModelUploadProgress
    end
    MD->>K8s: Set ModelUploaded = True

    Note over MD: Step 2 — Create CcfUser
    MD->>K8s: Create CcfUser CR (publisher)
    loop Wait for CcfUser Active
        MD->>K8s: Get CcfUser status
    end
    MD->>K8s: Set CcfUserReady = True

    Note over MD: Step 3 — Wait for OIDC issuer
    MD->>K8s: Get GovernanceService status
    alt oidcIssuerUrl populated
        MD->>K8s: Set OidcIssuerReady = True
    else Not yet available
        MD-->>K8s: Requeue (wait)
    end

    Note over MD: Step 4 — Configure access
    MD->>AAD: Get managed identity principal ID
    MD->>AAD: Assign Storage Blob Data Contributor
    MD->>AAD: Create federated credential
    MD->>K8s: Set AccessConfigured = True

    Note over MD: Step 5 — Dataset document
    MD->>CGS: resolveDocId (check existing)
    MD->>CGS: CreateUserDocument (Draft)
    MD-->>K8s: Event: DatasetDocCreated
    MD->>CGS: ProposeUserDocument
    MD-->>K8s: Event: DatasetDocProposed
    MD->>CGS: VoteUserDocument (Accept)
    MD-->>K8s: Event: DatasetDocAccepted
    MD->>K8s: Set DatasetDocReady = True

    Note over MD: Step 6 — Model document
    MD->>CGS: resolveDocId (check existing)
    MD->>CGS: CreateUserDocument (Draft)
    MD-->>K8s: Event: ModelDocCreated
    MD->>CGS: ProposeUserDocument
    MD-->>K8s: Event: ModelDocProposed
    MD->>CGS: VoteUserDocument (Accept)
    MD-->>K8s: Event: ModelDocAccepted
    MD->>K8s: Set ModelDocReady = True

    Note over MD: Phase → Ready
    opt autoDeploy = true
        MD->>K8s: Create ModelDeployment CR
    end
```

## Governance document lifecycle

Steps 5 and 6 each drive a document through the CGS governance
pipeline: `Draft → Proposed → Accepted`. On resume after a failure,
`resolveDocId` fetches the existing document from CGS and determines
which sub-stage to continue from. This provides idempotent retry
without duplicating documents.

## Error handling

Each step calls `mdSetFailed` with a specific reason on error
(e.g. `DatasetDocCreateFailed`, `ModelDocVoteFailed`). The controller
transitions to `Failed` phase and requeues after a delay. On the next
reconcile, it re-enters the failed step and retries from where it
left off.
