---
applyTo: "src/cleanroom-operator/**"
---

# Cleanroom Operator Development Instructions

## Critical Rules

> **NEVER** run `kubectl cleanroom dev down`, `kubectl cleanroom environment delete`,
> or destroy/recreate the Kind cluster during iterative development without first
> **explicitly asking the user for confirmation**. Always prefer the iterative dev
> loop (build → tag → push → load → rollout restart → retry) over teardown and
> recreation. Destruction is only warranted when the cluster is in an
> unrecoverable state that cannot be fixed by redeploying.

> **NEVER** run `go build`, `go run`, or `go install` directly. All builds
> **must** go through the PowerShell build scripts:
> ```bash
> # Operator image
> pwsh build/cleanroom-operator/build-cleanroom-operator.ps1 -push
> # kubectl plugin image
> pwsh build/cleanroom-operator/build-kubectl-cleanroom.ps1 -push
> ```
> These scripts handle Docker-based builds, image tagging, registry push, and
> CRD/manifest generation. Direct `go build` bypasses Dockerfile layers,
> analyzers, and tag conventions.

> **NEVER** use `latest` tag when loading images into Kind or restarting the
> operator. The deployment uses a **fixed numeric tag** (e.g.
> `100.1778043567`). After building, you **must** re-tag the image with the
> tag currently used by the deployment, push it to the local registry, and
> load it into Kind. Check the current tag with:
> ```bash
> kubectl get deployment cleanroom-operator -n cleanroom-system \
>   -o jsonpath='{.spec.template.spec.containers[0].image}'
> ```
> Then tag+push+load with that exact tag. Using `latest` has no effect because
> the deployment references the fixed tag.

> **NEVER** run `controller-gen` directly on the host. CRD generation **must** go
> through `build-cleanroom-operator.ps1` which calls
> `generate-cleanroom-operator-manifests.ps1` to run `controller-gen` inside a
> Docker container (pinned version) and outputs CRDs to
> `helm/chart/templates/crds/`. Running `controller-gen` locally creates stale
> artifacts in `config/crd/bases/` (the tool's default output path) that are not
> part of the project and will never be deployed.

## Flow 1: Clean Setup (First-Time / Fresh Environment)

Use this flow when starting from scratch or when the Kind cluster does not exist.
Follow the README quickstart:

```bash
# 1. Build all containers and generate the env file
pwsh build/onebox/build-dev-up-containers.ps1 -outDir generated

# 2. Install the kubectl cleanroom CLI plugin
az cleanroom operator install-cli --env-file generated/dev-up.env

# 3. Create the Kind cluster and deploy the operator
kubectl cleanroom dev up --env-file generated/dev-up.env \
  --output generated/mgmt-kubeconfig.yaml

# 4. Create an environment (e.g., virtual inferencing)
kubectl cleanroom environment create my-env \
  --infra-type virtual --profile inferencing --wait

# 5. Verify
kubectl cleanroom environment status my-env
```

After this flow completes you have a running Kind cluster (`cleanroom-mgmt`) with
the operator deployed and an environment ready. Proceed to Flow 2 for ongoing
development.

## Flow 2: Iterative Dev Loop

Use this flow for **all ongoing code changes**. The operator runs in a Kind
cluster named `cleanroom-mgmt` with a local registry at `localhost:5000`. The
deployed operator uses a fixed image tag (not `latest`), so you must tag, push,
and load the image into Kind nodes after every build.

### Steps

```bash
# 1. Build the operator (pushes :latest to local registry — NOT deployed yet)
pwsh build/cleanroom-operator/build-cleanroom-operator.ps1 -push

# 2. Find the current tag in use by the deployment
kubectl get deployment cleanroom-operator -n cleanroom-system \
  -o jsonpath='{.spec.template.spec.containers[0].image}'
# Example output: localhost:5000/cleanroom-operator:100.1777960382

# 3. Tag, push, and load into Kind (replace TAG with the tag from step 2)
#    ALL THREE commands are required — docker push alone is insufficient.
docker tag localhost:5000/cleanroom-operator:latest localhost:5000/cleanroom-operator:TAG
docker push localhost:5000/cleanroom-operator:TAG
kind load docker-image localhost:5000/cleanroom-operator:TAG --name cleanroom-mgmt

# 4. Restart the operator to pick up the new image
kubectl rollout restart deployment cleanroom-operator -n cleanroom-system
kubectl rollout status deployment cleanroom-operator -n cleanroom-system --timeout=60s

# 5. Retry any failed/stuck resources (if applicable)
kubectl cleanroom environment reconcile my-env

# 5b. Or reconcile and wait for Ready
kubectl cleanroom environment reconcile my-env --wait
```

### When you add a new CRD type

Building the operator generates CRD manifests, but `rollout restart` alone does
not install them in the cluster. After building, you must explicitly apply the
new CRD and the updated RBAC ClusterRole:

```bash
# Apply the new CRD (e.g., after adding ModelDeployment)
kubectl apply -f src/cleanroom-operator/helm/chart/templates/crds/

# Apply updated RBAC (the ClusterRole gains verbs for the new resource)
kubectl apply -f src/cleanroom-operator/helm/chart/templates/rbac-role.yaml

# Then restart the operator so it registers the new controller
kubectl rollout restart deployment cleanroom-operator -n cleanroom-system
```

Without the CRD apply the API server rejects the new resource type. Without the
RBAC apply the operator gets 403 Forbidden when trying to watch/update it.

### When to rebuild the kubectl plugin

If you changed code under `kubectl-plugin/` or the Helm chart under
`helm/`, also rebuild and reinstall the CLI plugin:

```bash
pwsh build/cleanroom-operator/build-kubectl-cleanroom.ps1 -push
az cleanroom operator install-cli --env-file generated/dev-up.env
```

## Key Details

- The container name inside the deployment is `operator` (not `cleanroom-operator`).
- `kind load docker-image` is required because `imagePullPolicy: IfNotPresent`
  caches the old image on the node. `docker push` alone is insufficient.
- The operator ConfigMap is `cleanroom-operator-config` in namespace
  `cleanroom-system`.
- OCI artifacts pulled from inside pods use `ccr-registry:5000` (the Docker
  network name), not `localhost:5000`.
- The Kind cluster name is `cleanroom-mgmt`.

## Testing and Verification

Check operator logs:

```bash
kubectl logs deployment/cleanroom-operator -n cleanroom-system --since=30s
```

Check resource status:

```bash
kubectl get environment,governancecontract,ccfmember,ccfnetwork,governanceservice,cluster \
  -o custom-columns="KIND:.kind,NAME:.metadata.name,PHASE:.status.phase" --no-headers
```

Query CGS from inside the cluster:

```bash
kubectl run -i --rm --restart=Never debug --image=curlimages/curl -- \
  http://cgs-MEMBER-NAME.default.svc.cluster.local:8080/contracts/CONTRACT-ID
```

## Troubleshooting

If the operator pod is crash-looping or won't start:

```bash
# Check events
kubectl describe deployment cleanroom-operator -n cleanroom-system
kubectl get events -n cleanroom-system --sort-by='.lastTimestamp'

# Check if image is available on the node
docker exec cleanroom-mgmt-control-plane crictl images | grep cleanroom-operator
```

If a resource is stuck and `retry` annotation doesn't help, check for
reconciler errors in the logs before considering more invasive actions.

## Documentation

> **ALWAYS** update the how-it-works docs when changing controller step
> sequences, conditions, status fields, or CLI output. These docs are the
> source of truth for how the operator behaves and must stay in sync with code.

The operator has documentation files that must stay in sync with code:

- `README.md` — quickstart, project structure
- `docs/how-it-works/environment.md` — condition table, CLI output, sequence diagram
- `docs/how-it-works/model-registration.md` — same structure
- `docs/how-it-works/model-deployment.md` — same structure
- `docs/troubleshooting.md` — status, Aspire dashboard, collect-logs, reconcile
- `docs/why-operator.md` — rationale for operator vs script approach

When a change affects what the user sees, update the relevant docs:

| Code change | Update |
|-------------|--------|
| Add/remove/rename a condition constant | Condition table + CLI output + sequence diagram in the how-it-works page |
| Add/remove a controller step | Sequence diagram + condition table in the how-it-works page |
| Add/remove/rename a status field | Status fields section in the how-it-works page |
| Change CLI command flags or output | README quickstart + CLI output in the how-it-works page |
| Change project directory structure | README project structure tree |

Do **not** create separate reference docs (CLI flags, CRD fields). Those are
served by `--help` and `kubectl explain`.

## Controller Architecture Patterns

### Step Machine

Controllers use condition-gated steps. Each step checks
`conditionIsTrue(conditions, ConditionType...)` before executing. On success it
calls `setConditionTrue(...)`. On failure it calls `setConditionFalse(...)` and
sets the CR phase to `Failed`. Steps are idempotent — re-running a reconcile
skips already-completed steps.

### Parent/Child CR Relationships

Parent CRs (e.g., Environment, ModelRegistration) create child CRs with:

- `controllerutil.SetControllerReference(parent, child, scheme)` — sets ownerRef
  so the child is garbage-collected when the parent is deleted.
- `Owns(&ChildType{})` in `SetupWithManager` — makes the parent controller
  re-reconcile whenever a child's status changes.
- Retry propagation: when a parent's `reconcileRequestedAt` annotation is set,
  the reconcile loop propagates it to all Failed children before re-evaluating
  parent status.

### Retry / Reconcile Mechanism

The `reconcileRequestedAt` annotation triggers re-processing of a Failed
resource:

1. CLI sets `reconcileRequestedAt` = current timestamp on the CR.
2. Controller sees `reconcileRequestedAt != lastHandledReconcileAt`, resets
   phase to the initial state (e.g., `Configuring`), clears Failed conditions,
   sets `lastHandledReconcileAt` = `reconcileRequestedAt`.
3. Normal step-machine reconciliation resumes from the first incomplete step.

### Shared Helpers

The `controller` package provides common functions used by all reconcilers:
`startSpan`, `resolveTraceContext`, `saveTrace`, `recordContextError`,
`truncateMessage`, `conditionIsTrue`, `setConditionTrue`, `setConditionFalse`.

## CLI (kubectl plugin) Patterns

### Standard Subcommand Set

All CR types expose a consistent set of subcommands:

| Command     | Description |
|-------------|-------------|
| `create`    | Server-side Apply (SSA) the CR, optionally `--wait` for Ready |
| `get`       | Show CR details (YAML-like output) |
| `list`      | List all CRs of this type |
| `delete`    | Delete the CR |
| `status`    | Show phase + conditions table with ✓/✗/○ indicators |
| `wait`      | Watch the CR until Ready or Failed (colored output) |
| `reconcile` | Set `reconcileRequestedAt`, poll for acknowledgment, then wait |

`reconcile` is aliased as `retry` for convenience.

### Create + Wait Race Condition

`create --wait` calls `Apply` (SSA) then immediately starts a Kubernetes watch.
If the resource was previously in `Failed` state, the watch may see the stale
Failed phase before the controller has reconciled the new spec. This is a known
limitation — the controller does not yet implement `observedGeneration` checking
to auto-reset Failed on spec changes. Workaround: use `reconcile --wait` after
a failed `create` to force re-processing.
