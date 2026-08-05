# TypeSpec Code Generation

This directory contains scripts for generating TypeSpec specifications and client code from the CCF governance application.

## Scripts

### `generate_models.ps1`

Generates TypeScript/C#/Python code from TypeSpec model definitions using the `tsp-codegen` Docker image.

**Usage:**
```powershell
pwsh generate_models.ps1 [-forceBuild] [-clean]
```

**Options:**
- `-forceBuild`: Force rebuild of Docker images
- `-clean`: Clean node_modules and generated directories before generation

**What it does:**
1. Builds TypeSpec models using `@typespec/compiler`
2. Generates code for multiple targets:
   - JavaScript for CCF governance service
   - C# for client library and proxy service
   - Python for proxy client
3. Formats Python code using isort and black

### `generate_operations.py`

Standalone Python script that generates TypeSpec API and interface definitions from `app.json`.

**Usage:**
```bash
python3 generate_operations.py [--app-json PATH] [--output-dir PATH] [--dry-run]
```

**Options:**
- `--app-json PATH`: Path to app.json file (default: `src/governance/ccf-app/js/app.json`)
- `--output-dir PATH`: Base directory for TypeSpec output (default: `src/specifications/typespec`)
- `--dry-run`: Show what would be generated without writing files

**What it generates:**
1. `api.tsp` - HTTP API routes and operations for the governance service
   - TypeSpec interfaces with `@route`, `@get`, `@post`, `@put` decorators
   - Path, query, and body parameters
   - Response types

2. `interfaces.tsp` - Client library interfaces abstracting HTTP details
   - Pure TypeScript-like interfaces (no HTTP decorators)
   - Grouped by logical domain (Contracts, Secrets, Members, etc.)
   - Identifies attestation-based operations (empty `authn_policies`)
   - Composite `GovernanceClient` interface

**Examples:**
```bash
# Dry run - analyze endpoints without generating files
python3 generate_operations.py --dry-run

# Generate files with custom paths
python3 generate_operations.py \
  --app-json ./custom-app.json \
  --output-dir ./output

# Generate to default locations
python3 generate_operations.py
```

### `generate_operations.ps1`

PowerShell wrapper that calls `generate_operations.py` with the same interface.

**Usage:**
```powershell
pwsh generate_operations.ps1 [-AppJsonPath PATH] [-OutputDir PATH] [-DryRun]
```

## Workflow

### Initial Setup

When setting up a new workspace or after pulling changes:

```bash
# Generate TypeSpec operations from app.json
python3 generate_operations.py

# Generate code from TypeSpec models
pwsh generate_models.ps1
```

### After Modifying app.json

When CCF endpoints change in `src/governance/ccf-app/js/app.json`:

```bash
# Regenerate TypeSpec definitions
python3 generate_operations.py

# Review generated api.tsp and interfaces.tsp
# Manually refine type names, add missing parameters, improve documentation

# Regenerate client code
pwsh generate_models.ps1
```

### After Modifying TypeSpec Models

When changing TypeSpec models (e.g., adding new request/response types):

```bash
# Just regenerate client code
pwsh generate_models.ps1
```

## Directory Structure

```
src/specifications/
├── README.md                          # This file
├── generate_models.ps1                # Code generation from TypeSpec
├── generate_operations.py             # TypeSpec generation from app.json
├── generate_operations.ps1            # PowerShell wrapper
└── typespec/
    ├── cleanroom-governance-service/
    │   ├── api.tsp                   # Generated: HTTP API routes
    │   ├── models.tsp                 # Manually maintained: Data models
    │   └── tspconfig.yaml
    ├── cleanroom-governance-client-lib/
    │   ├── interfaces.tsp             # Generated: Client interfaces
    │   ├── models.tsp                 # Manually maintained: Client models
    │   └── tspconfig.yaml
    └── cleanroom-governance-client-proxy/
        ├── api.tsp                    # Manually maintained: Proxy API
        ├── models.tsp                 # Manually maintained: Proxy models
        └── tspconfig.yaml
```

## Code Generation Flow

```
app.json (CCF endpoints)
    ↓
generate_operations.py
    ↓
api.tsp + interfaces.tsp (TypeSpec definitions)
    ↓
generate_models.ps1
    ↓
TypeScript/C#/Python client code
```

## Notes

- **api.tsp** and **interfaces.tsp** are generated but may need manual refinement:
  - Verify request/response type names match models.tsp
  - Add missing query parameters
  - Adjust operation names for clarity
  - Add comprehensive documentation comments

- **models.tsp** files are manually maintained and should define:
  - Request/response types
  - Enums and unions
  - Common data structures

- The generator identifies attestation-based operations (those with empty `authn_policies` in app.json) and marks them in comments

## Requirements

- **PowerShell 7+** for running .ps1 scripts
- **Python 3.6+** for generate_operations.py
- **Docker** for running tsp-codegen and python-linter images
- **Git** for repository root detection

## Troubleshooting

### "Python generator not found"
Ensure you're running from the repository root or that the relative path to `generate_operations.py` is correct.

### "app.json not found"
Specify the path explicitly:
```bash
python3 generate_operations.py --app-json path/to/app.json
```

### Generated code has type errors
Review and refine the generated TypeSpec files, ensuring type names match your models.tsp definitions.
