# Copilot Instructions for azure-cleanroom

## Architecture Overview

This is a multi-language monorepo for **Azure Clean Rooms** — a confidential computing platform enabling secure multi-party data collaboration using AMD SEV-SNP hardware attestation and the Confidential Consortium Framework (CCF).

### Core Layers

- **Governance (C#/.NET + TypeScript)** — `src/governance/`: The Clean Room Governance Service (CGS) is a CCF application that enforces data access policies, manages contracts (Draft→Proposed→Accepted), stores secrets, issues OIDC tokens, and provides audit logging. The CCF app is in `src/governance/ccf-app/js/`. Multi-member voting governs all contract and policy changes.
- **Identity & Secrets (C#/.NET)** — `src/identity/`, `src/secrets/`: Credential management sidecars with telemetry/metrics integration.
- **CCF Providers (C#/.NET)** — `src/ccf/`: Virtual and CACI (Confidential ACI) providers for CCF network lifecycle, recovery, and consortium management.
- **Proxy (Go + Envoy)** — `src/proxy-ext-processor/`: gRPC-based Envoy external processor using OPA policy filters for request-level authorization. `src/proxy/` contains Envoy configs.
- **Workloads (Python/Scala)** — `src/workloads/`: Analytics (Spark SQL) and inferencing (KServe) workload runtimes.
- **Launchers (Python)** — `src/blobfuse-launcher/`, `src/s3fs-launcher/`, `src/code-launcher/`: Sidecar containers for storage mounting and code execution.
- **SDKs (Python/TypeScript)** — `src/sdk/`: Client libraries and governance proxy. TypeScript types are auto-generated from OpenAPI/TypeSpec specs in `src/specifications/`.
- **Shared Libraries (C#)** — `src/internal/`: Attestation, COSE signing, OPA filtering, REST API commons.
- **Azure CLI Extension (Python)** — `src/tools/azure-cli-extension/cleanroom/`: `az cleanroom` commands for governance, CCF, and deployment management.
- **Cluster Management (C#)** — `src/cleanroom-cluster/`: AKS and virtual Kubernetes cluster providers.

### Key Domain Concepts

- **Clean Room**: A confidential computing environment with SEV-SNP hardware attestation where multiple organizations can process shared data under governance policies.
- **CCF**: Confidential Consortium Framework — the ledger-backed governance layer.
- **CGS**: Clean Room Governance Service — the CCF application managing contracts, secrets, attestation, and audit.
- **CACI**: Confidential ACI — Azure container instances with SEV-SNP.
- **Attestation**: Cryptographic proof of code/hardware integrity; clean rooms must present valid attestation to access secrets or log events.
- **Contracts**: Governance agreements that define what operations a clean room can perform; require multi-member voting.

## Build and Test

### Container Builds (Primary)

All components are containerized (41+ Dockerfiles in `build/docker/`). Build orchestration uses PowerShell:

> **IMPORTANT**: Never use raw `dotnet build`, `dotnet restore`, `dotnet publish`, `go build`, or `go run` commands directly. Always use the PowerShell build scripts under `build/`. These scripts handle Docker-based builds, CRD generation, image tagging, and registry push. Direct tool invocations bypass Dockerfile layers, analyzers, and tag conventions.

```bash
# Build all local containers for Kind cluster development
pwsh build/onebox/build-containers.ps1

# Component-specific builds are in build/{ccf,ccr,workloads,cleanroom-cluster,cleanroom-operator}/
pwsh build/ccf/build-ccf-infra-containers.ps1
pwsh build/cleanroom-operator/build-cleanroom-operator.ps1
pwsh build/cleanroom-operator/build-kubectl-cleanroom.ps1
pwsh build/cleanroom-cluster/build-cleanroom-cluster-provider-client.ps1
```

### .NET (C#)

```bash
# Build a specific solution
dotnet build src/governance/governance.sln
dotnet build src/ccf/ccf.sln
dotnet build src/identity/Identity.sln

# Run CGS unit tests
dotnet test src/governance/test/cgs-tests.csproj --logger "console;verbosity=normal"

# Run a specific test class or method
dotnet test src/governance/test/cgs-tests.csproj --filter "FullyQualifiedName~UserDocumentTests"
dotnet test src/governance/test/cgs-tests.csproj --filter "FullyQualifiedName~UserDocumentTests.ListUserDocumentsWithQueryParameter"
```

### Go

```bash
# Build proxy-ext-processor
cd src/proxy-ext-processor && go build ./...

# Run tests
cd src/proxy-ext-processor && go test ./...

# Lint
golangci-lint run
```

### Python

Uses **uv** workspace manager. Workspace members are defined in the root `pyproject.toml`.

```bash
# Set up virtual environment and install all workspace packages
uv venv && source .venv/bin/activate
uv lock && uv sync --all-packages

# Run Python unit tests (uses unittest, not pytest)
python -m unittest src/code-launcher/test/test_code_launch.py
python -m unittest src/code-launcher/test/test_code_launch.TestCodeLauncherMain.test_parse_args_pubic_reg_unenc_image

# Lint Python (CI checks)
black --check .
isort --check-only --profile black .
```

### Local Integration Testing (Kind)

```bash
# Start a local Kubernetes cluster
bash test/onebox/kind-up.sh

# Build and load container images
pwsh build/onebox/build-containers.ps1

# Run a multi-party collaboration scenario
pwsh test/onebox/multi-party-collab/nginx-hello/run-collab.ps1

# Tear down
bash test/onebox/kind-down.sh
```

Integration tests in `test/onebox/` use Python scripts and PowerShell. They require a running Kind cluster with deployed services.

### C/C++ Format Checking

```bash
bash scripts/ci-checks.sh        # Check formatting
bash scripts/ci-checks.sh -f src  # Fix formatting
```

## Code Conventions

Detailed, language-specific conventions are in `.github/instructions/` and activate automatically when editing files of that language:

| File | Scope | Key rules |
|---|---|---|
| `csharp.instructions.md` | `**/*.cs` | File-scoped namespaces, StyleCop, `this.` prefix, central package management, 101-char lines |
| `golang.instructions.md` | `**/*.go` | golangci-lint shadow checking, Logrus, OTLP tracing |
| `python.instructions.md` | `**/*.py` | uv workspace, ruff formatting/linting, unittest (not pytest) |
| `typescript.instructions.md` | `**/*.{ts,js}` | Prettier, auto-generated SDKs (don't edit), 2-space indent |
| `powershell.instructions.md` | `**/*.ps1` | Build orchestration, Kind image loading, approved verbs |

### General

- **Max line width**: 100 characters (101 for C# via Menees).
- **Indentation**: 4 spaces for C#/PowerShell; 2 spaces for JSON, XML, YAML, TypeScript, JavaScript.
- **Containerized everything**: Every component runs as a container. Dockerfiles are in `build/docker/`.
- **Kind image loading**: Use `kind load docker-image` to load directly into nodes. `docker push` to the local registry alone is insufficient due to `imagePullPolicy: IfNotPresent` caching.
- **Shell scripts**: Use `#!/bin/bash` shebang and `set -e`.
