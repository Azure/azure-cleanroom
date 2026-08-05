# How It Works: ModelDeployment

A `ModelDeployment` deploys a model inference endpoint on the
workload cluster. It optionally enables flex (SEV-SNP) nodes, waits
for its parent `ModelRegistration` to be Ready, then submits a
deployment request to the inferencing agent and polls until the
service is healthy.

## Conditions

| Condition                 | What it means                                                                 |
| ------------------------- | ----------------------------------------------------------------------------- |
| `FlexNodeReady`           | Environment patched to enable flex nodes (only when NodeProvisioningMode set) |
| `ModelRegistrationReady`    | Parent ModelRegistration has reached Ready phase                                |
| `EndpointSubmitted`       | Deployment request submitted to inferencing agent                             |
| `InferenceServiceCreated` | Inferencing service exists on workload cluster                                |
| `EndpointDeployed`        | Service is ready; ServiceEndpoint and ServiceCaCert populated                 |
| `DeploymentHealthy`       | Set to False when container errors are detected during polling                |

## Status fields

On successful deployment the controller populates:

- `status.serviceEndpoint` — the URL of the deployed inference endpoint
  (cluster KServe endpoint + `/ai`).
- `status.serviceCaCert` — the PEM-encoded CA certificate for TLS
  verification, resolved from the GovernanceContract.
- `status.correlationId` — persisted across reconcile loops to resume
  polling after a controller restart.

## CLI output

```
$ kubectl cleanroom mdi create my-endpoint --model-registration my-model
  ✓ FlexNodeReady
  · Waiting for ModelRegistration to be Ready...
  ✓ ModelRegistrationReady
  · Submitting deployment to inferencing agent...
  ✓ EndpointSubmitted
  · Inferencing service created, waiting for readiness
  ✓ InferenceServiceCreated
  · Waiting for inferencing service to be ready
  ✓ EndpointDeployed
ModelDeployment my-endpoint is Ready
```

## Sequence diagram

```mermaid
sequenceDiagram
    participant CLI as kubectl-cleanroom
    participant K8s as Kubernetes API
    participant MDI as MDI Controller
    participant Cluster as Cluster CR
    participant CcfUser as CcfUser CR
    participant CGS as CGS Client
    participant Agent as Inferencing Agent
    participant GC as GovernanceContract

    CLI->>K8s: Create ModelDeployment CR
    K8s-->>MDI: Reconcile

    Note over MDI: Step 1 — Ensure flex node (conditional)
    MDI->>K8s: Get ModelRegistration → EnvironmentRef
    MDI->>K8s: Patch Environment (flexNode.enabled = true, mode)
    MDI->>K8s: Set FlexNodeReady = True

    Note over MDI: Step 2 — Check parent
    MDI->>K8s: Get ModelRegistration status
    alt Not Ready
        MDI-->>K8s: Requeue (wait)
    else Ready
        MDI->>K8s: Set ModelRegistrationReady = True
    end

    Note over MDI: Step 3 — Deploy endpoint
    MDI->>Cluster: Resolve KServe endpoint from status
    MDI->>CcfUser: Resolve publisher CGS client endpoint
    MDI->>CGS: GET /identity/accessToken
    CGS-->>MDI: Access token

    MDI->>Agent: GET /inferenceServices/{name}/status
    alt 404 — not found
        MDI->>Agent: POST /inferenceServices (submit)
        MDI->>K8s: Set EndpointSubmitted = True
        MDI-->>K8s: Requeue after delay
    else Exists but not ready
        MDI->>K8s: Set InferenceServiceCreated = True
        alt Container errors detected
            MDI->>K8s: Set DeploymentHealthy = False
        end
        alt Deployment timed out
            MDI->>K8s: Phase → Failed
        end
        MDI-->>K8s: Requeue after delay
    else Ready
        MDI->>K8s: Set ServiceEndpoint = endpoint/ai
        MDI->>GC: Resolve CA cert from GovernanceContract status
        MDI->>K8s: Set ServiceCaCert
        MDI->>K8s: Set EndpointDeployed = True
    end

    Note over MDI: Phase → Ready
```

## Error handling

If the parent ModelRegistration is in `Failed` phase, the MDI controller
also transitions to `Failed`. Once the parent is fixed and reaches
`Ready`, the MDI controller retries on the next reconcile.

The deployment polling has a timeout (default 10 minutes). If the
inferencing service does not become ready within this period, the
controller sets the phase to `Failed` with reason
`DeploymentTimeout`. Container-level errors (e.g. image pull failures,
OOM kills) are surfaced via the `DeploymentHealthy` condition and
Kubernetes events.

The `correlationId` is persisted in status before submitting the
deployment request, ensuring that on controller restart the controller
can resume polling rather than creating a duplicate deployment.
