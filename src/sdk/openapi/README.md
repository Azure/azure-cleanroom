# Frontend OpenAPI Specifications

This folder contains versioned OpenAPI specifications for the Frontend API.

## Folder Structure

```
openapi/
├── V2026_03_01_Preview/      # API version 2026-03-01-preview
│   └── frontend.yaml         # Main API specification (refs shared schemas via ../)
├── cleanroom.yaml            # Shared cleanroom schema definitions
├── dataset.yaml              # Shared dataset schema definitions
├── query.yaml                # Shared query schemas
└── spark.yaml                # Shared spark schemas
```

## Files

### Versioned (per API version)
- `frontend.yaml` - Main API specification with paths and operations. References shared
  schemas via relative paths (e.g., `../cleanroom.yaml`).

### Shared (across versions)
- `cleanroom.yaml` - Cleanroom schema definitions (referenced by frontend.yaml)
- `dataset.yaml` - Dataset schema definitions (referenced by frontend.yaml)
- `query.yaml` - Query-related schemas
- `spark.yaml` - Spark-related schemas

## Generating Clients

### TypeScript Client
```bash
npx openapi-typescript ./V2026_03_01_Preview/frontend.yaml --output ./frontend.ts
```

### C# Client SDK
```bash
openapi-generator-cli generate -i ./V2026_03_01_Preview/frontend.yaml -g csharp -o ./sdk/dotnet-client
```

### ASP.NET Core Controller Stubs
```bash
openapi-generator-cli generate -i ./V2026_03_01_Preview/frontend.yaml -g aspnetcore -o ./server-stubs
```

## Adding a New API Version

1. Create a new folder (e.g., `V2027_01_01/`)
2. Copy the YAML files from the previous version
3. Update the `version` field in `frontend.yaml`
4. Update the `api-version` enum values
5. Make schema changes as needed for the new version

## Versioning Guidelines

- **Non-breaking changes**: Can be added to existing version (new optional fields)
- **Breaking changes**: Require a new API version folder
- **Deprecation**: Mark old versions as deprecated in their `frontend.yaml` description

## Related Files

- Controllers: `src/workloads/frontend/Api/V*/Controllers/`
- Models: `src/workloads/frontend/Api/V*/Models/`
- Version constants: `src/workloads/frontend/Api/V*/ApiVersionConstants.cs`
