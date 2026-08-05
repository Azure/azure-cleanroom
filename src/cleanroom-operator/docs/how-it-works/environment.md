# How It Works: Environment

An `Environment` is the top-level resource. It creates six child
resources and aggregates their status. The Environment controller
itself makes no external calls — it is a pure orchestrator.

## Conditions

The CLI tracks these conditions during `environment create` or
`environment wait`:

| Condition                 | Child resource       | What it means                                                        |
| ------------------------- | -------------------- | -------------------------------------------------------------------- |
| `CcfMemberReady`          | CcfMember (operator) | Operator member is activated in consortium                           |
| `InitialMemberReady`      | CcfMember (member0)  | Initial member is activated, CGS client deployed                     |
| `CcfNetworkReady`         | CcfNetwork           | CCF network is created and open                                      |
| `GovernanceServiceReady`  | GovernanceService    | CGS app configured (constitution, OIDC, JWT)                         |
| `WorkloadGovernanceReady` | WorkloadGovernance   | Contract created, deployment spec proposed (kserve-inferencing only) |
| `ClusterRunning`          | Cluster              | Cluster infrastructure is up (does not gate Ready)                   |
| `ClusterReady`            | Cluster              | Cluster running and all workload profiles applied                    |

## CLI output

```
$ kubectl cleanroom environment create my-env --infra-type virtual --profile inferencing
  · CcfMember (member0): generating certificates...
  · CcfMember (operator): generating certificates...
  ✓ CcfMemberReady
  · CcfNetwork: creating network...
  · CcfNetwork: polling for completion...
  ✓ CcfNetworkReady
  ✓ InitialMemberReady
  · GovernanceService: setting CA cert bundle...
  · GovernanceService: configuring JWT issuers...
  · GovernanceService: deploying constitution...
  · GovernanceService: configuring JS runtime...
  · GovernanceService: deploying JS app...
  · GovernanceService: enabling OIDC issuer...
  · GovernanceService: uploading OIDC documents...
  ✓ GovernanceServiceReady
  · WorkloadGovernance: creating contract...
  · WorkloadGovernance: enabling signing...
  · WorkloadGovernance: generating deployment...
  · WorkloadGovernance: proposing deployment spec...
  · WorkloadGovernance: proposing clean room policy...
  · WorkloadGovernance: updating ConfigMap...
  ✓ WorkloadGovernanceReady
  · Cluster: creating cluster...
  · Cluster: polling for completion...
  ✓ ClusterRunning
  ✓ ClusterReady
Environment my-env is Ready
```

Lines prefixed with `·` are Kubernetes Events emitted by child
controllers. Lines prefixed with `✓` indicate a condition became True.

## Sequence diagram

```mermaid
sequenceDiagram
    participant CLI as kubectl-cleanroom
    participant K8s as Kubernetes API
    participant Env as Environment Controller
    participant Mem as CcfMember Controller
    participant Net as CcfNetwork Controller
    participant GS as GovernanceService Controller
    participant WG as WorkloadGovernance Controller
    participant GC as GovernanceContract Controller
    participant Cls as Cluster Controller
    participant CCF as CCF Provider Client
    participant CGS as CGS Client
    participant ClsP as Cluster Provider Client

    CLI->>K8s: Create Environment CR
    K8s-->>Env: Reconcile

    Note over Env: Create child CRs
    Env->>K8s: Create CcfMember (operator)
    Env->>K8s: Create CcfMember (member0)

    par CcfMember reconciliation
        K8s-->>Mem: Reconcile operator member
        Mem->>Mem: Generate signing cert + key
        Mem->>K8s: Store certs in Secret
        K8s-->>Mem: Reconcile member0
        Mem->>Mem: Generate signing cert + encryption key
        Mem->>K8s: Store certs in Secret
    end

    Note over Env: Certs ready → create network
    Env->>K8s: Create CcfNetwork CR

    K8s-->>Net: Reconcile
    Net->>CCF: CreateNetwork (async)
    loop Poll every 30s
        Net->>CCF: GetOperation
    end
    Net->>CCF: Fetch service cert from node
    Net->>CCF: ConfigureProvider (operator cert)
    Net->>CCF: TransitionToOpen
    Note over Net: Phase → Open

    par Member activation
        K8s-->>Mem: Reconcile operator member
        Mem->>K8s: Deploy CGS client (Deployment + Service)
        Mem->>CGS: ActivateMember
        Note over Mem: Phase → Active

        K8s-->>Mem: Reconcile member0
        Mem->>K8s: Deploy CGS client (Deployment + Service)
        Mem->>CGS: ActivateMember
        Note over Mem: Phase → Active
    end

    Env->>K8s: Create GovernanceService CR
    K8s-->>GS: Reconcile
    GS->>CGS: SetCaCertBundle
    GS->>CGS: SetJwtIssuers
    GS->>CGS: SetConstitution (from OCI)
    GS->>CGS: SetJsRuntime
    GS->>CGS: SetJsApp (from OCI)
    GS->>CGS: EnableOidc + GenerateSigningKey
    GS->>GS: Upload OIDC discovery documents
    Note over GS: Phase → Ready

    Env->>K8s: Create WorkloadGovernance CR
    K8s-->>WG: Reconcile
    WG->>K8s: Create GovernanceContract CR

    K8s-->>GC: Reconcile
    GC->>CGS: Create contract
    GC->>CGS: Propose + Accept contract
    opt DeploymentSpec provided
        GC->>CGS: ProposeDeploymentSpec + Vote
    end
    opt CleanRoomPolicy provided
        GC->>CGS: ProposeCleanRoomPolicy + Vote
    end
    opt EnableCA
        GC->>CGS: ProposeEnableCA + Vote
        GC->>CGS: GenerateCAKey
        GC->>CGS: GetCAInfo → store CaCert in status
    end
    GC->>K8s: Write output ConfigMap
    Note over GC: Phase → Ready

    Note over WG: GovernanceContract Ready
    WG->>CGS: EnableSigning
    WG->>CGS: GenerateSigningKey
    WG->>ClsP: GenerateKServeInferencingDeployment
    WG->>CGS: ProposeDeploymentSpec + Vote
    WG->>CGS: ProposeCleanRoomPolicy + Vote
    WG->>K8s: Update ConfigMap (configurationUrl)
    Note over WG: Phase → Ready

    Env->>K8s: Create Cluster CR
    K8s-->>Cls: Reconcile
    Note over Cls: Wait for ConfigMap (configurationUrl)
    Cls->>CCF: CreateCluster (async)
    loop Poll every 30s
        Cls->>CCF: GetOperation
    end
    Note over Cls: Phase → Running

    K8s-->>Env: Aggregate status
    Note over Env: All children Ready → Phase = Ready
    Env->>K8s: Set EnvironmentReady = True
```

## Error handling

If any child resource reaches `Failed`, the Environment is marked
`Failed` with a message identifying which child failed and why. Fixing
the underlying issue and updating the child resource (or the
Environment spec) triggers a retry.
