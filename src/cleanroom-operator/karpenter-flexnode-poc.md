# Karpenter-Driven Dynamic Flex Node Provisioning PoC

## Objective

Demonstrate Karpenter-driven **on-demand flex node provisioning** in the cleanroom operator, adding a new `mode: karpenter` to the existing flex node profile that enables reactive, pod-demand-driven node provisioning alongside the existing `mode: explicit` (fixed node count) approach.

The PoC validates:

* MDI pods remain unschedulable until flex nodes exist
* Karpenter responds to unschedulable pods by creating NodeClaims
* A custom NodeClaim reconciler in the cleanroom operator watches NodeClaims and provisions flex nodes via KindScaler
* Provisioned flex nodes receive proper taints/labels/proxies and the MDI pods schedule successfully
* Node scale-down: Karpenter disruption removes idle NodeClaims, and the reconciler drains + deletes the corresponding kind worker

---

## Current Architecture (What Changes)

### Today's Flow (Explicit Mode — Unchanged)

```text
ModelRegistration created
        ↓
stepEnsureFlexNode patches Environment.FlexNode.Enabled = true
        ↓
Environment controller patches Cluster.FlexNodeProfile
        ↓
Cluster controller calls POST /clusters/{name}/update
        ↓
VirtualClusterProvider.EnableFlexNodeAsync:
  - Computes delta (desired NodeCount - existing flex nodes)
  - KindScaler: add-worker-node.sh × N
  - ConfigureFlexNodeWorkerAsync × N (taint, label, install proxies)
        ↓
Flex nodes ready → MDI pods schedule
```

This flow is preserved as `mode: explicit` for users who want a fixed, pre-provisioned set of flex nodes.

**Limitations of explicit mode:**

1. Node count is statically configured up-front (cannot react to pod demand)
2. No scale-down path exists (no `remove-worker-node.sh`, no `DisableFlexNode`)
3. No per-node lifecycle management (cannot drain/cordon/remove individual nodes)

### Proposed Flow (Karpenter Mode — New)

```text
FlexNodeProfile configured with mode: karpenter, enabled: true
        ↓
stepEnsureFlexNode propagates config (cert, SSH, governance) but does NOT provision nodes
        ↓
kubectl cleanroom mdi create
        ↓
MDI pod created with:
  - toleration: pod-policy=required:NoSchedule
  - nodeSelector: pod-policy=required
        ↓
Pod is Pending (no flex nodes exist yet)
        ↓
Karpenter detects unschedulable pod → creates NodeClaim
        ↓
NodeClaim Reconciler (new controller in cleanroom operator):
  1. Calls KindScaler to add a worker node (add-worker-node.sh)
  2. Configures the node (taint, labels, api-server-proxy, kubelet-proxy)
  3. Updates NodeClaim status → node is registered
        ↓
MDI pod schedules on the new flex node
```

---

## Design Decisions

### 1. FlexNodeProfile Mode Field

A new `Mode` field on `FlexNodeProfileSpec` controls how flex nodes are provisioned:

```yaml
# Karpenter mode — nodes provisioned on-demand by Karpenter
flexNodeProfile:
  enabled: true
  mode: karpenter
  policySigningCertPem: "..."
  insecure: true

# Explicit mode — fixed node count, pre-provisioned (today's behavior, default)
flexNodeProfile:
  enabled: true
  mode: explicit         # default when omitted (backward compatible)
  nodeCount: 2
  policySigningCertPem: "..."
  insecure: true
```

| Mode                 | Trigger                                           | Node Count        | Scale-Down              |
| -------------------- | ------------------------------------------------- | ----------------- | ----------------------- |
| `explicit` (default) | `stepEnsureFlexNode` provisions nodes immediately | Fixed `NodeCount` | Not supported           |
| `karpenter`          | Pod demand → Karpenter NodeClaim → reconciler     | Dynamic, per-pod  | Karpenter consolidation |

**Default is `explicit`** for backward compatibility — existing deployments behave identically.

### 2. `stepEnsureFlexNode` Behavior Per Mode

`stepEnsureFlexNode` in the ModelRegistration controller is **retained** and still sets `FlexNodeProfile.Enabled = true` on the Environment/Cluster CRs. Its behavior differs by mode:

| Mode        | `stepEnsureFlexNode` Action                                                                                                                   |
| ----------- | --------------------------------------------------------------------------------------------------------------------------------------------- |
| `explicit`  | Provisions `NodeCount` flex nodes immediately (today's behavior)                                                                              |
| `karpenter` | Propagates config (cert, SSH keys, governance) to Cluster CR but **skips node provisioning**. The NodeClaim reconciler handles it reactively. |

This means:
* In `karpenter` mode, the Cluster controller's call to `POST /clusters/{name}/update` still runs, but `EnableFlexNodeAsync` is a no-op when `mode == karpenter` (it only ensures cluster-level prerequisites like registries are configured).
* The config (policy signing cert, SSH keys, governance endpoint, insecure flag) **must** be propagated to the Cluster CR so the NodeClaim reconciler can read it when provisioning nodes.

### 3. Imperative Node Create/Delete APIs

The current cluster provider has **no imperative node APIs** — only a declarative `UpdateCluster` with desired `NodeCount`. For Karpenter mode we need per-node lifecycle:

| API            | Route                                          | Purpose                                      |
| -------------- | ---------------------------------------------- | -------------------------------------------- |
| CreateFlexNode | `POST /clusters/{name}/flexnodes`              | Create a single flex node, return node name  |
| DeleteFlexNode | `DELETE /clusters/{name}/flexnodes/{nodeName}` | Drain, uncordon, remove a specific flex node |
| GetFlexNode    | `GET /clusters/{name}/flexnodes/{nodeName}`    | Get status of a specific flex node           |

These are needed because:

* Karpenter creates/deletes NodeClaims **one at a time** based on pod demand
* Each NodeClaim maps to exactly one physical node
* Scale-down requires removing a **specific** node (the one Karpenter selected for disruption)
* The declarative "desired count" model cannot express "remove node X but keep node Y"

### 4. Virtual Setup — KindScaler as the Backend

For this PoC, all testing runs on the **virtual cleanroom cluster** (Kind). The KindScaler already supports adding worker nodes dynamically. We add:

* `remove-worker-node.sh` — drains, cordons, and removes a specific kind worker container
* New provider endpoints (`CreateFlexNode`, `DeleteFlexNode`) that call the scripts

---

## Architecture

```text
┌─────────────────────────────────────────────────────────────────┐
│                     Management Cluster (Kind)                    │
│                                                                 │
│  ┌──────────────┐     ┌──────────────┐     ┌────────────────┐  │
│  │   Karpenter  │────▶│  NodeClaim   │◀────│  NodeClaim     │  │
│  │  (scheduler) │     │     CR       │     │  Reconciler    │  │
│  └──────────────┘     └──────────────┘     │  (new ctrl)    │  │
│         │                                   └───────┬────────┘  │
│         │ detects pending pods                      │           │
│         ▼                                           │           │
│  ┌──────────────┐                                   │           │
│  │   NodePool   │  (defines taint/labels            │           │
│  │     CR       │   for flex nodes)                 │           │
│  └──────────────┘                                   │           │
│                                                     ▼           │
│                                          ┌──────────────────┐   │
│                                          │ Cluster Provider  │   │
│                                          │   Client (REST)   │   │
│                                          └────────┬─────────┘   │
│                                                   │             │
└───────────────────────────────────────────────────┼─────────────┘
                                                    │
                                                    ▼
┌─────────────────────────────────────────────────────────────────┐
│                   Workload Cluster (Kind)                         │
│                                                                  │
│  ┌────────────────┐  ┌────────────────┐  ┌────────────────┐     │
│  │  control-plane │  │  worker (base) │  │  flex-worker-1 │     │
│  │                │  │                │  │  (dynamic)     │     │
│  └────────────────┘  └────────────────┘  └────────────────┘     │
│                                            taint: pod-policy=    │
│                                              required:NoSchedule │
│                                            label: pod-policy=    │
│                                              required            │
│                                            label: cleanroom.     │
│                                              azure.com/          │
│                                              flexnode=true       │
└─────────────────────────────────────────────────────────────────┘
```

---

## CRD Changes

### New Type: `FlexNodeMode`

```go
// File: src/cleanroom-operator/api/v1alpha1/cluster_types.go

// +kubebuilder:validation:Enum=explicit;karpenter
type FlexNodeMode string

const (
    FlexNodeModeExplicit  FlexNodeMode = "explicit"
    FlexNodeModeKarpenter FlexNodeMode = "karpenter"
)
```

### Updated: `FlexNodeProfileSpec`

```go
type FlexNodeProfileSpec struct {
    Enabled              *bool        `json:"enabled,omitempty"`
    Mode                 FlexNodeMode `json:"mode,omitempty"` // NEW — defaults to "explicit"
    NodeCount            *int32       `json:"nodeCount,omitempty"`
    VmSize               string       `json:"vmSize,omitempty"`
    PolicySigningCertPem string       `json:"policySigningCertPem,omitempty"`
    GovernanceConfigRef  string       `json:"governanceConfigRef,omitempty"`
    Insecure             *bool        `json:"insecure,omitempty"`
    UseKindScaler        *bool        `json:"useKindScaler,omitempty"`
    SSHPublicKey         string       `json:"sshPublicKey,omitempty"`
    SSHPrivateKey        string       `json:"sshPrivateKey,omitempty"`
}
```

### Default Behavior

When `Mode` is empty or omitted, the operator treats it as `explicit` (backward compatible). The NodeClaim reconciler only activates when `Mode == "karpenter"`.

---

## NodeClaim Reconciler — Detailed Design

### Guard Condition

The reconciler's first action is to look up the target Cluster CR and check:

```go
if cluster.Spec.FlexNodeProfile.Mode != FlexNodeModeKarpenter {
    // Not in karpenter mode — skip this NodeClaim.
    return ctrl.Result{}, nil
}
```

This ensures the reconciler is a no-op for clusters using explicit mode.

### Controller Registration

```go
// File: src/cleanroom-operator/internal/controller/nodeclaim_controller.go

type NodeClaimReconciler struct {
    client.Client
    Scheme        *runtime.Scheme
    ClusterClient clusterclient.Interface
    FlexNodeConfig FlexNodeConfig // resolved from Cluster CR
}

// Watches: karpenter.sh/v1 NodeClaim
// Owns: nothing (NodeClaim is owned by Karpenter)
```

### Reconcile Loop — Create Path

When a NodeClaim transitions to a state indicating it needs a node provisioned:

```text
1. Read NodeClaim spec (instance type, requirements, taints)
2. Determine target cluster from NodePool labels/annotations
3. Call POST /clusters/{name}/flexnodes with:
   - NodeName: generated from NodeClaim UID or name
   - PolicySigningCert: from Cluster CR's FlexNodeProfile
   - SSH keys: from Cluster CR's FlexNodeProfile
   - Insecure: from Cluster CR's FlexNodeProfile
4. Wait for node to appear in workload cluster and reach Ready
5. Patch NodeClaim status:
   - Set .status.nodeName = <created-node-name>
   - Set .status.providerID = "kind://<node-name>"
   - Set conditions to indicate node is ready
6. Requeue if node not ready yet
```

### Reconcile Loop — Delete Path

When a NodeClaim has a deletion timestamp (Karpenter is disrupting):

```text
1. Read .status.nodeName from the NodeClaim
2. Call DELETE /clusters/{name}/flexnodes/{nodeName}
   - Provider drains the node (kubectl drain --ignore-daemonsets --delete-emptydir-data)
   - Provider cordons the node
   - Provider runs kubeadm reset on the node
   - Provider removes the Docker container
3. Remove finalizer from NodeClaim to let Karpenter complete deletion
```

### Finalizer Strategy

The reconciler adds a finalizer (`cleanroom.azure.com/flex-node`) to every NodeClaim it provisions. This ensures:

* The node is properly drained before removal
* The kind container is cleaned up
* No orphaned nodes remain

### Error Handling

| Scenario                                | Behavior                                                              |
| --------------------------------------- | --------------------------------------------------------------------- |
| `add-worker-node.sh` fails              | Set NodeClaim condition to `NodeReady=False`, requeue with backoff    |
| `ConfigureFlexNodeWorker` fails partway | Retry from the failed step (idempotent operations)                    |
| Node never reaches Ready                | Timeout after 5 minutes, set condition `NodeReady=False` with message |
| Delete fails (container already gone)   | Treat as success, remove finalizer                                    |
| Cluster provider unreachable            | Requeue with exponential backoff                                      |

---

## Imperative Provider APIs — Detailed Design

### `POST /clusters/{name}/flexnodes` — CreateFlexNode

**Request:**

```json
{
  "nodeName": "my-cluster-worker5",
  "policySigningCertPem": "...",
  "sshPublicKey": "...",
  "insecure": true,
  "governanceEndpoint": "https://..."
}
```

**Implementation (Virtual Provider):**

```text
1. Call KindClient.AddWorkerNode(kindClusterName, nodeName, "for-flex-node=true:NoSchedule")
   → add-worker-node.sh --cluster-name <kind> --node-name <name> --add-taint for-flex-node=true:NoSchedule
2. Configure local registries on the new node
3. Call ConfigureFlexNodeWorkerAsync(nodeName):
   a. kubectl taint node <name> pod-policy=required:NoSchedule
   b. kubectl label node <name> cleanroom.azure.com/flexnode=true
   c. kubectl label node <name> pod-policy=required
   d. kubectl taint node <name> for-flex-node- (remove placeholder)
   e. Install api-server-proxy OCI package
   f. Install kubelet-proxy OCI package
   g. kubectl label node <name> cleanroom.azure.com/ready=true
4. Return node status
```

**Response:**

```json
{
  "nodeName": "my-cluster-worker5",
  "status": "Ready",
  "providerID": "kind://my-cluster-worker5"
}
```

### `DELETE /clusters/{name}/flexnodes/{nodeName}` — DeleteFlexNode

**Implementation (Virtual Provider):**

```text
1. kubectl drain <nodeName> --ignore-daemonsets --delete-emptydir-data --timeout=120s
2. kubectl cordon <nodeName>
3. kubectl delete node <nodeName>
4. docker exec <container> kubeadm reset -f (cleanup kubelet state)
5. docker rm -f <container> (remove the kind worker container)
```

This requires a new script: `remove-worker-node.sh`.

### `GET /clusters/{name}/flexnodes/{nodeName}` — GetFlexNode

Returns the node's status from the workload cluster (labels, taints, conditions).

---

## NodePool Configuration

The Karpenter `NodePool` CR tells Karpenter what taints/labels to set on NodeClaims and what disruption policies to use:

```yaml
apiVersion: karpenter.sh/v1
kind: NodePool
metadata:
  name: flex-node-pool
spec:
  template:
    spec:
      taints:
        - key: pod-policy
          value: required
          effect: NoSchedule
      requirements:
        - key: "kubernetes.io/arch"
          operator: In
          values: ["amd64"]
  disruption:
    consolidationPolicy: WhenEmpty
    consolidateAfter: 5m
  limits:
    cpu: "100"    # max total CPU across all nodes in pool
    memory: "400Gi"
```

### NodeClass (Minimal for PoC)

Since we're using Kind (not a cloud provider), we define a minimal `NodeClass` or use Karpenter's built-in `NodeClaim` without a provider-specific class:

```yaml
apiVersion: karpenter.sh/v1
kind: NodeClass
metadata:
  name: kind-flex-node
spec:
  # Minimal — the reconciler handles all provisioning
  amiFamily: Custom
```

> **Note:** For the PoC, we may not need a full NodeClass if we use Karpenter's "virtual" mode or patch out the cloud provider requirement. The key integration point is the NodeClaim lifecycle, not the NodeClass.

---

## End-to-End Flow (PoC Test Scenario)

### Setup

```bash
# 1. Start Kind cluster (management + workload already running from dev setup)
bash test/onebox/kind-up.sh

# 2. Install Karpenter CRDs + controller in management cluster
# (use karpenter helm chart with --set controller.enabled=true)

# 3. Apply NodePool CR
kubectl apply -f nodepool-flex.yaml

# 4. Deploy cleanroom operator with new NodeClaim reconciler
pwsh build/cleanroom-operator/build-cleanroom-operator.ps1 -push

# 5. Create environment with flex node mode: karpenter
kubectl cleanroom environment create my-env \
  --infra-type virtual --profile inferencing \
  --enable-flex-node \
  --flex-node-config '{"mode": "karpenter"}' \
  --wait
```

### Demo Steps

```bash
# Step 1: Create MDI (inference pod gets toleration pod-policy=required:NoSchedule)
kubectl cleanroom mdi create my-model-instance --model-registration my-model

# Step 2: Observe pod is Pending (no flex nodes exist)
kubectl get pods -n kserve-inferencing
# → my-model-predictor-xxx   0/1   Pending   (no nodes match nodeSelector)

# Step 3: Karpenter detects unschedulable pod, creates NodeClaim
kubectl get nodeclaims
# → flex-abc123   Pending   <none>

# Step 4: NodeClaim Reconciler provisions flex node
# - Calls POST /clusters/my-cluster/flexnodes
# - add-worker-node.sh runs
# - Node joins workload cluster with correct taints/labels
kubectl get nodes --show-labels
# → my-cluster-worker3   Ready   pod-policy=required,cleanroom.azure.com/flexnode=true

# Step 5: Pod schedules on new flex node
kubectl get pods -n kserve-inferencing
# → my-model-predictor-xxx   1/1   Running   (on my-cluster-worker3)

# Step 6: Delete MDI → pod removed → node becomes idle
kubectl cleanroom mdi delete my-model-instance

# Step 7: Karpenter consolidation removes idle NodeClaim after 5m
kubectl get nodeclaims
# → (empty — NodeClaim deleted)

# Step 8: NodeClaim Reconciler removes flex node
# - Calls DELETE /clusters/my-cluster/flexnodes/my-cluster-worker3
# - Node drained, container removed
kubectl get nodes
# → (only control-plane + base workers remain)
```

---

## Implementation Plan

### Phase 1 — Foundation (MVP)

| #   | Task                                                                                 | Component                                                            |
| --- | ------------------------------------------------------------------------------------ | -------------------------------------------------------------------- |
| 1   | Add `Mode FlexNodeMode` field to `FlexNodeProfileSpec`                               | `src/cleanroom-operator/api/v1alpha1/cluster_types.go`               |
| 2   | Add `remove-worker-node.sh` script                                                   | `src/cleanroom-cluster/cleanroom-cluster-provider-client/kind/`      |
| 3   | Add `POST /clusters/{name}/flexnodes` endpoint                                       | `ClustersController.cs` + `VirtualClusterProvider.cs`                |
| 4   | Add `DELETE /clusters/{name}/flexnodes/{nodeName}` endpoint                          | Same                                                                 |
| 5   | Add `GET /clusters/{name}/flexnodes/{nodeName}` endpoint                             | Same                                                                 |
| 6   | Guard `EnableFlexNodeAsync` to no-op node creation in `karpenter` mode               | `VirtualClusterProvider.cs`                                          |
| 7   | Create `NodeClaimReconciler` controller (guards on `mode == karpenter`)              | `src/cleanroom-operator/internal/controller/nodeclaim_controller.go` |
| 8   | Register Karpenter CRDs in operator scheme                                           | `cmd/main.go` or scheme setup                                        |
| 9   | Adjust `stepEnsureFlexNode` to propagate config but skip node wait in karpenter mode | `modelregistration_controller.go`                                      |
| 10  | Install Karpenter in Kind dev setup                                                  | `test/onebox/kind-up.sh` or separate script                          |
| 11  | Add NodePool + NodeClass manifests                                                   | `test/onebox/` or `src/cleanroom-operator/config/`                   |

### Phase 2 — Robustness

| #   | Task                                                                     | Component                 |
| --- | ------------------------------------------------------------------------ | ------------------------- |
| 10  | Finalizer-based cleanup on NodeClaim deletion                            | `nodeclaim_controller.go` |
| 11  | Status reporting (NodeClaim conditions)                                  | `nodeclaim_controller.go` |
| 12  | Integration test: MDI create → Pending → NodeClaim → Flex Node → Running | `test/onebox/`            |
| 13  | Integration test: MDI delete → consolidation → node removal              | `test/onebox/`            |
| 14  | Handle concurrent NodeClaims (multiple MDIs at once)                     | `nodeclaim_controller.go` |
| 15  | Exponential backoff on provider failures                                 | `nodeclaim_controller.go` |

### Phase 3 — Production Readiness

| #   | Task                                                   | Component                                               |
| --- | ------------------------------------------------------ | ------------------------------------------------------- |
| 16  | AKS provider: `CreateFlexNode` → Azure VM provisioning | `src/cleanroom-cluster/aks-cleanroom-cluster-provider/` |
| 17  | AKS provider: `DeleteFlexNode` → Azure VM deletion     | Same                                                    |
| 18  | GPU-aware NodePool (nvidia.com/gpu requirements)       | NodePool CR                                             |
| 19  | Multi-pool support (CPU vs GPU flex nodes)             | Multiple NodePool CRs                                   |
| 20  | Observability (metrics, events, tracing)               | `nodeclaim_controller.go`                               |

---

## NodeClaim Reconciler — Controller Logic (Pseudocode)

```go
func (r *NodeClaimReconciler) Reconcile(ctx context.Context, req ctrl.Request) (ctrl.Result, error) {
    nodeClaim := &karpenterv1.NodeClaim{}
    if err := r.Get(ctx, req.NamespacedName, nodeClaim); err != nil {
        return ctrl.Result{}, client.IgnoreNotFound(err)
    }

    // Determine which cluster this NodeClaim targets.
    // Convention: NodePool has annotation cleanroom.azure.com/cluster-name
    clusterName := r.resolveClusterName(nodeClaim)
    if clusterName == "" {
        // Not a cleanroom-managed NodeClaim, skip.
        return ctrl.Result{}, nil
    }

    // Handle deletion.
    if !nodeClaim.DeletionTimestamp.IsZero() {
        return r.reconcileDelete(ctx, nodeClaim, clusterName)
    }

    // Handle creation.
    return r.reconcileCreate(ctx, nodeClaim, clusterName)
}

func (r *NodeClaimReconciler) reconcileCreate(ctx, nodeClaim, clusterName) (ctrl.Result, error) {
    // Add finalizer if not present.
    if !controllerutil.ContainsFinalizer(nodeClaim, FlexNodeFinalizer) {
        controllerutil.AddFinalizer(nodeClaim, FlexNodeFinalizer)
        if err := r.Update(ctx, nodeClaim); err != nil {
            return ctrl.Result{}, err
        }
    }

    // If already provisioned (nodeName set), check readiness.
    if nodeClaim.Status.NodeName != "" {
        return r.checkNodeReady(ctx, nodeClaim, clusterName)
    }

    // Resolve flex node configuration from Cluster CR.
    cluster := &v1alpha1.Cluster{}
    if err := r.Get(ctx, types.NamespacedName{Name: clusterName}, cluster); err != nil {
        return ctrl.Result{}, err
    }

    // Generate node name.
    nodeName := r.generateNodeName(clusterName, nodeClaim.Name)

    // Call provider to create the flex node.
    err := r.ClusterClient.CreateFlexNode(ctx, clusterName, CreateFlexNodeInput{
        NodeName:             nodeName,
        PolicySigningCertPem: cluster.Spec.FlexNodeProfile.PolicySigningCertPem,
        Insecure:            *cluster.Spec.FlexNodeProfile.Insecure,
        // ... other config
    })
    if err != nil {
        // Set condition and requeue.
        meta.SetStatusCondition(&nodeClaim.Status.Conditions, metav1.Condition{
            Type:    "NodeReady",
            Status:  metav1.ConditionFalse,
            Reason:  "ProvisioningFailed",
            Message: err.Error(),
        })
        r.Status().Update(ctx, nodeClaim)
        return ctrl.Result{RequeueAfter: 30 * time.Second}, nil
    }

    // Update NodeClaim status with provisioned node info.
    nodeClaim.Status.NodeName = nodeName
    nodeClaim.Status.ProviderID = "kind://" + nodeName
    if err := r.Status().Update(ctx, nodeClaim); err != nil {
        return ctrl.Result{}, err
    }

    return ctrl.Result{RequeueAfter: 5 * time.Second}, nil // Requeue to verify Ready
}

func (r *NodeClaimReconciler) reconcileDelete(ctx, nodeClaim, clusterName) (ctrl.Result, error) {
    if !controllerutil.ContainsFinalizer(nodeClaim, FlexNodeFinalizer) {
        return ctrl.Result{}, nil
    }

    nodeName := nodeClaim.Status.NodeName
    if nodeName != "" {
        // Call provider to delete the flex node.
        err := r.ClusterClient.DeleteFlexNode(ctx, clusterName, nodeName)
        if err != nil && !isNotFound(err) {
            return ctrl.Result{RequeueAfter: 15 * time.Second}, err
        }
    }

    // Remove finalizer.
    controllerutil.RemoveFinalizer(nodeClaim, FlexNodeFinalizer)
    if err := r.Update(ctx, nodeClaim); err != nil {
        return ctrl.Result{}, err
    }

    return ctrl.Result{}, nil
}
```

---

## Key Considerations

### Karpenter Installation in Kind

Karpenter typically requires a cloud provider. For this PoC:

* **Option A:** Use Karpenter with `--feature-gates=EnableNodeClaimWithoutProvider=true` (if available) or patch out the provider requirement.
* **Option B:** Use Karpenter's "fake" cloud provider mode designed for testing (kwok provider).
* **Option C:** Implement a minimal Karpenter `CloudProvider` interface that delegates to our reconciler (this is what Karpenter expects in production).

**Recommended:** Option C — implement the minimal `CloudProvider` interface pointing at our controller. This is what Karpenter is designed for and avoids hacks.

### Mapping NodeClaims to Clusters

The reconciler needs to know which workload cluster to provision nodes in. Convention:

```yaml
apiVersion: karpenter.sh/v1
kind: NodePool
metadata:
  name: flex-node-pool
  annotations:
    cleanroom.azure.com/cluster-name: "my-env-cluster"
```

The reconciler reads this annotation from the NodeClaim's owning NodePool.

### Concurrency

Multiple MDI creates may trigger multiple NodeClaims simultaneously. The reconciler must:

* Use unique node names (derived from NodeClaim UID)
* Handle concurrent `add-worker-node.sh` executions (the script is safe for parallel use since each creates a uniquely-named container)

### Existing `FlexNodeProfile` Fields

The `FlexNodeProfile` on the Cluster CR retains its role as the **configuration source** for flex node setup (policy cert, SSH keys, governance, insecure flag). In karpenter mode, `NodeCount` is unused (Karpenter manages the count dynamically). `Enabled` still means "this cluster needs flex nodes" — the new `Mode` field controls *how* they are provisioned.

---

## Files to Create/Modify

| Action     | File                                                                                        | Description                                                                                                           |
| ---------- | ------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------- |
| **Modify** | `src/cleanroom-operator/api/v1alpha1/cluster_types.go`                                      | Add `FlexNodeMode` type + `Mode` field to `FlexNodeProfileSpec`                                                       |
| **Create** | `src/cleanroom-cluster/cleanroom-cluster-provider-client/kind/remove-worker-node.sh`        | Drain + remove a kind worker node                                                                                     |
| **Create** | `src/cleanroom-operator/internal/controller/nodeclaim_controller.go`                        | NodeClaim reconciler (guards on `mode == karpenter`)                                                                  |
| **Create** | `src/cleanroom-operator/internal/controller/nodeclaim_controller_test.go`                   | Unit tests                                                                                                            |
| **Modify** | `src/cleanroom-cluster/cleanroom-cluster-provider-client/Controllers/ClustersController.cs` | Add flexnodes endpoints                                                                                               |
| **Modify** | `src/cleanroom-cluster/virtual-cleanroom-cluster-provider/VirtualClusterProvider.cs`        | Add `CreateFlexNodeAsync`, `DeleteFlexNodeAsync`; no-op node creation in `EnableFlexNodeAsync` when mode is karpenter |
| **Modify** | `src/cleanroom-cluster/cleanroom-cluster-provider/ICleanRoomClusterProvider.cs`             | Add `CreateFlexNode`, `DeleteFlexNode` to interface                                                                   |
| **Modify** | `src/cleanroom-operator/internal/controller/modelregistration_controller.go`                  | Adjust `stepEnsureFlexNode`: propagate config in all modes, skip node-ready wait in karpenter mode                    |
| **Modify** | `src/cleanroom-operator/cmd/main.go`                                                        | Register NodeClaim reconciler + Karpenter scheme                                                                      |
| **Create** | `test/onebox/karpenter/install-karpenter.sh`                                                | Install Karpenter in Kind                                                                                             |
| **Create** | `test/onebox/karpenter/nodepool-flex.yaml`                                                  | NodePool CR for testing                                                                                               |

---

## Testing Strategy

### Unit Tests

* `nodeclaim_controller_test.go` — mock ClusterClient, verify create/delete calls
* Test finalizer addition/removal
* Test error paths and requeue behavior

### Integration Tests (Kind)

```bash
# Full E2E: MDI create → NodeClaim → flex node → pod scheduled
pwsh test/onebox/karpenter/run-karpenter-e2e.ps1

# Scale-down: MDI delete → consolidation → node removed
pwsh test/onebox/karpenter/run-scaledown-e2e.ps1
```

### Manual Verification

```bash
# Watch the full flow in real-time
kubectl get nodeclaims -w &
kubectl get nodes -w &
kubectl get pods -n kserve-inferencing -w &

kubectl cleanroom mdi create test-model-instance --model-registration test-model
# Observe: Pending → NodeClaim created → Node joins → Pod Running
```

---

## Open Questions

1. **Karpenter CloudProvider interface:** What's the minimal implementation needed for Kind? Does kwok-provider suffice or do we need a custom Go provider?

2. **NodeClaim ↔ Cluster mapping:** Should we use NodePool annotations, or a label on the NodeClaim itself? Multiple workload clusters may share a management cluster.

3. **GPU scheduling:** For GPU workloads, the NodePool needs `nvidia.com/gpu` requirements. How does GFD (GPU Feature Discovery) work on Kind workers? (Likely out of scope for this PoC.)

4. **FlexNodeProfile lifecycle:** Should `stepEnsureFlexNode` be fully removed, or should it remain as a "bootstrap minimum nodes" guarantee while Karpenter handles scaling above that minimum?

5. **Karpenter version:** Which Karpenter release to target? v1.0+ has stable NodeClaim/NodePool APIs.
