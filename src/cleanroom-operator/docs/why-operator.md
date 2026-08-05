# Why the Operator Approach

The cleanroom operator replaces a multi-script workflow
(`setup-env.ps1` → `run-scenario.ps1` → `test-kserve-inferencing.ps1`)
with three `kubectl` commands. This document explains why.

## The script-based approach

Setting up a KServe inferencing environment today requires running a
chain of PowerShell and Python scripts totaling ~1,400 lines across
6+ files:

```
setup-env.ps1 → setup-env.py          # CCF network + governance + cluster
run-scenario.ps1 → deploy-models.py   # model upload + governance docs
test-kserve-inferencing.ps1/.py        # validation + telemetry + Grafana
```

Each script shells out to `az cleanroom`, `az cli`, `kubectl`, and
`curl` in sequence. A `deployment-config.json` file is passed between
stages to carry state.

## The operator-based approach

```powershell
kubectl cleanroom environment create my-env --infra-type virtual --profile inferencing

kubectl cleanroom md create tinyllama --model-id TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF --source-file tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf --env my-env --location centralindia

kubectl cleanroom mdi create tinyllama-svc --model-registration tinyllama
```

Three commands. No intermediate config files. No script dependencies.

## Comparison

|                         | Scripts                                                                                                                                                                                              | Operator                                                                                                                                                                                |
| ----------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Idempotency**         | Partial. Some `az` commands are idempotent, but governance calls (create contract, propose, vote) fail on re-run if the resource already exists. Re-running from the top doubles work or errors out. | Full. The controller checks existing state before acting. `resolveDocId` detects whether a governance document exists and resumes from its current state (Draft → Proposed → Accepted). |
| **Retry on failure**    | None. Scripts use `$ErrorActionPreference = 'Stop'` — any failure aborts the entire run. The user must figure out which step failed, comment out completed steps, and re-run.                        | Built-in. `kubectl cleanroom md reconcile tinyllama` re-enters the step machine at the first incomplete condition. Completed steps are skipped automatically.                           |
| **Progress visibility** | Print statements scattered across 6 files. No structured output. Finding which step failed requires reading interleaved stdout from PowerShell, Python, and `az` CLI.                                | `✓`/`✗`/`·` condition output in real time. `status` command shows a condition table. Aspire dashboard gives distributed traces filterable by trace ID.                                  |
| **State management**    | `deployment-config.json` passed between scripts. Loses track of partial progress — if `run-scenario.ps1` fails halfway, the JSON doesn't record what succeeded.                                      | Kubernetes status subresource. Each condition records what completed. Status fields (`DatasetDocId`, `ModelDocProposalId`) persist across restarts.                                     |
| **Customization**       | Edit hardcoded variables in scripts (`ob-workload-ccf`, `ob-workload-cluster`, etc.) or pass 15+ CLI flags. Different model combinations require modifying `--models` flag mappings.                 | Change spec fields in YAML or pass CLI flags. The CRD schema documents every field with validation and defaults.                                                                        |
| **Portability**         | Requires PowerShell, Python, uv, Azure CLI, and specific Python packages. The PS1→PY delegation chain assumes a Unix-like shell.                                                                     | Requires `kubectl` and the plugin binary. The operator runs in-cluster.                                                                                                                 |
| **Parallelism**         | `setup-env.py` supports `--max-workers` but `run-scenario.ps1` is sequential. Adding a second model means editing the script.                                                                        | The operator reconciles independent resources concurrently. Adding a second `ModelRegistration` just means applying a second YAML.                                                        |
| **Demo setup**          | A partner running a demo must clone the repo, install toolchains (pwsh, python, uv, az CLI), understand the script chain, edit hardcoded names, and hope nothing fails halfway.                      | A partner runs three `kubectl` commands (or applies three YAML files). Failures show a clear condition table and can be retried with one command.                                       |
| **Observability**       | Scripts print to stdout. `test-kserve-inferencing.py` collects telemetry files after the fact. Debugging requires manually correlating timestamps across log files.                                  | The operator emits OpenTelemetry traces with W3C trace context. Every reconcile step, CGS call, and Azure API call is a span. The Aspire dashboard shows the full trace in one view.    |
| **Cleanup**             | Scripts don't track what they created. Cleanup requires knowing which Azure resources, governance documents, and K8s objects were provisioned.                                                       | `kubectl delete environment my-env` cascades to all child resources via owner references. Finalizers handle Azure resource cleanup.                                                     |

## Migrating CI to the operator

The aim is to replace the script-based CI pipeline
(`setup-env` → `run-scenario` → `test-kserve-inferencing`) with
operator-driven infrastructure and a thin test runner on top. The
following gaps must be closed before the scripts can be retired.

### Gaps — operator does not yet handle

| Gap                                      | What the scripts do today                                                                                                                                                                                                | What the operator needs                                                                                                                                                                                                                                               |
| ---------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Multi-party simulation**               | `run-scenario.ps1` creates a second "publisher" user with its own governance client, OIDC issuer, and federated identity. The publisher creates/proposes/votes on model documents independently.                         | Support for multi-member workflows — a way to declare additional participants (publisher, consumer) and have the operator provision their CGS clients, identities, and vote on their behalf.                                                                          |
| **Inference validation**                 | `deploy-models.py` (~2,600 lines) sends inference requests via port-forward, the agent framework, and the OHTTP gateway, then asserts response correctness.                                                              | A `test` or `validate` command (or a separate test CRD) that sends probe requests to deployed InferenceServices and reports pass/fail. This is inherently test logic, not infrastructure, so it may remain as a thin script that consumes operator-created endpoints. |
| **Ledger event assertions**              | `test-kserve-inferencing.py` queries `az cleanroom governance contract event list` and asserts that specific audit messages ("Starting inference service deployment for: {name}") appear in the ledger, with 5 retries.  | Either a `kubectl cleanroom md verify-events` command or an explicit contract that the test runner consumes the operator's status to locate the governance client and contract ID, then runs assertions itself.                                                       |
| **Telemetry file export**                | `get-telemetry.ps1` downloads logs/traces/metrics JSON files from the cluster. The test asserts these files exist and are non-empty.                                                                                     | `kubectl cleanroom dev collect-logs` already collects pod logs and CRD state. It needs to also export OTLP telemetry files (logs, traces, metrics per workload) so CI can validate them.                                                                              |
| **Grafana dashboard install**            | `test-kserve-inferencing.py` installs a ConfigMap with the inferencing dashboard JSON and opens a port-forward.                                                                                                          | A `kubectl cleanroom dev grafana` command that installs dashboards and starts port-forward. Low priority — the Aspire dashboard covers the same need for debugging.                                                                                                   |
| **Security policy pipeline**             | For AKS + policy enforcement, `run-scenario.ps1` generates security policies (`cached-debug`), proposes deployment templates and policies via governance, and votes on them.                                             | The operator handles `allow-all` in virtual mode. AKS with real SNP policies needs the operator to generate, propose, and vote on deployment templates and CCE policies as part of the Environment or MDI reconcile.                                                  |
| **Publisher storage and identity setup** | `setup-kfserving-examples-storage.ps1` creates Azure storage accounts and uploads model files. `setup-kfserving-examples-mi.ps1` creates managed identities. `setup-access.ps1` wires up federated credentials and RBAC. | The ModelRegistration controller handles HuggingFace → blob upload and OIDC/RBAC for the operator's own identity. For multi-party, the publisher's storage and identity setup needs to be expressible as CRD spec fields.                                               |

### Migration path

1. **Short term**: Replace `setup-env.ps1/py` with
   `kubectl cleanroom environment create`. This is ready today.
2. **Medium term**: Replace `run-scenario.ps1` with
   `kubectl cleanroom md create` + `kubectl cleanroom mdi create`.
   Requires closing the multi-party and security policy gaps above.
3. **Long term**: Reduce `test-kserve-inferencing.py` to a thin
   assertion script that reads operator status fields (contract ID,
   governance client, endpoint URL) and runs inference + ledger
   checks. The operator owns all infrastructure; the test runner
   owns only assertions.

## For partners and demos

The operator approach lowers the barrier to running a clean room
inferencing demo from "clone the repo and run a script chain" to
"install the kubectl plugin and run three commands." Partners can:

1. Get a working environment in minutes without understanding the
   internal script plumbing.
2. See exactly where things stand via `status` and condition tables.
3. Fix issues and retry without starting over.
4. Inspect what happened via the Aspire dashboard traces.
5. Share a YAML file instead of a README explaining which scripts to
   run in which order with which flags.
