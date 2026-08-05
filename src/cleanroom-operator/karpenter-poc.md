# Karpenter + kind Dynamic Node Provisioning PoC

## Objective

Build a minimal proof-of-concept demonstrating that Karpenter NodeClaims can drive dynamic infrastructure realization through a custom reconciler.

Instead of provisioning cloud VMs, the PoC will provision additional worker “nodes” into a running kind cluster by dynamically creating and joining `kindest/node` Docker containers.

This validates:

* Karpenter extensibility
* NodeClaim lifecycle integration
* custom provider/reconciler patterns
* dynamic node realization
* autoscaler-driven infrastructure orchestration

without involving:

* Azure
* AKS
* Flex Node
* VM provisioning
* cloud networking

---

# Core Architecture

```text id="w0a1p0"
Unschedulable Pod
        ↓
Karpenter creates NodeClaim
        ↓
Custom NodeClaim Reconciler
        ↓
Create kind worker container
        ↓
Run kubeadm join
        ↓
Node joins cluster
        ↓
Pod schedules successfully
```

---

# Why This PoC

The PoC isolates and validates the most important architectural question:

```text id="x0a1p0"
Can Karpenter NodeClaims
drive external infrastructure realization?
```

Using kind allows:

* fast local iteration
* simplified debugging
* no cloud dependencies
* deterministic behavior
* rapid experimentation

This creates a lightweight local infrastructure provider simulator.

---

# Technical Approach

## Management Cluster

A local kind cluster will run:

* Karpenter
* CRDs
* custom NodeClaim reconciler

---

## Trigger Mechanism

Deploy intentionally unschedulable workloads.

Example:

* resource requests exceeding cluster capacity
* nodeSelector targeting dynamic nodes

Karpenter should respond by creating NodeClaims.

---

## Custom Reconciler

A custom controller watches:

```text id="y0a1p0"
NodeClaims
```

When a NodeClaim appears:

1. Create a new `kindest/node` container
2. Connect container to the `kind` Docker network
3. Generate/reuse kubeadm join configuration
4. Execute `kubeadm join`
5. Wait for node readiness
6. Update NodeClaim status

---

# Initial Implementation Strategy

## Phase 1 — Use Existing KindScaler Logic

Leverage:
[KindScaler repository](https://github.com/lobuhi/kindscaler?utm_source=chatgpt.com)

KindScaler already demonstrates:

* dynamic worker node injection
* kubeadm join automation
* compatible container bootstrapping

Initial reconciler may simply shell out to:

```text id="z0a1p0"
kindscaler.sh
```

to minimize complexity.

---

## Phase 2 — Native Go Implementation

Replace shell-script orchestration with:

* Docker SDK
* controller-runtime
* native reconciliation logic

This enables:

* idempotency
* retries
* cleanup
* observability
* lifecycle management

---

# Minimal Scope

The PoC intentionally avoids:

* full Karpenter provider implementation
* cloud providers
* VM provisioning
* GPU scheduling
* pricing logic
* spot handling
* advanced lifecycle management
* control-plane scaling

Only worker-node creation is in scope.

---

# Expected Deliverables

## Functional Demo

Demonstrate:

```text id="a1a1p0"
Pending pod
→ NodeClaim
→ New kind worker node
→ Pod scheduled
```

---

## Reconciler Prototype

A minimal controller implementing:

* NodeClaim watch/reconcile loop
* dynamic node creation
* node lifecycle observation

---

## Architecture Validation

Validate that:

* Karpenter can serve as a generic infrastructure orchestration layer
* NodeClaims are a viable extensibility boundary
* external infrastructure realization patterns are practical

---

# Longer-Term Evolution Path

If successful, the same reconciliation pattern can later be adapted to:

```text id="b1a1p0"
NodeClaim
    ↓
Azure VM provisioning
    ↓
AKS Flex Node bootstrap
    ↓
Node joins AKS
```

Potential future areas:

* Azure VM provisioning
* AKS Flex Node integration
* DRA integration
* GPU-aware provisioning
* topology-aware scheduling
* AI workload optimization

---

# Key Milestones

| Milestone | Goal                                          |
| --------- | --------------------------------------------- |
| M1        | Manual worker-node join succeeds              |
| M2        | Karpenter installed on kind                   |
| M3        | NodeClaims generated from pending pods        |
| M4        | Reconciler creates worker nodes automatically |
| M5        | Pods successfully schedule onto new nodes     |

---

# Success Criteria

The PoC is successful if:

1. Karpenter creates NodeClaims
2. Reconciler dynamically adds kind worker nodes
3. New nodes become Ready
4. Pending workloads successfully schedule

This demonstrates a complete:

```text id="c1a1p0"
Karpenter-driven dynamic infrastructure realization loop
```

in a fully local environment.
