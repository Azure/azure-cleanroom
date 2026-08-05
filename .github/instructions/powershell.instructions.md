---
applyTo: "**/*.ps1"
---

# PowerShell Conventions (azure-cleanroom)

## Role
PowerShell is the primary build and deployment orchestration tool. Build scripts live in `build/`, deployment/test scripts in `test/onebox/` and `samples/`.

## Naming
- Use approved verbs: `Get-`, `Set-`, `New-`, `Remove-`, etc.
- PascalCase for function names and parameters.

## Formatting
- 4 spaces for indentation.

## Kind Cluster Interaction
- Use `kind load docker-image` to load images directly into nodes.
- `docker push` to the local registry alone is insufficient due to `imagePullPolicy: IfNotPresent` caching.

## Key Build Commands
```powershell
# Build all containers for local Kind development
pwsh build/onebox/build-containers.ps1

# Component-specific builds
pwsh build/ccf/build-ccf-infra-containers.ps1

# Run a multi-party scenario
pwsh test/onebox/multi-party-collab/nginx-hello/run-collab.ps1
```

## Shell Scripts
- Use `#!/bin/bash` shebang.
- Use `set -e` to exit on errors.
