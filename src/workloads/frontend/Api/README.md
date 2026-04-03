# Frontend API Versioning

This folder contains the versioned API structure for the Frontend service.

## Folder Structure

```
Api/
├── Common/                           # Shared base types across all versions
│   ├── BaseModels/                   # Base classes for API models
│   │   ├── QueryRunInputBase.cs
│   │   ├── VoteRequestBase.cs
│   │   ├── SecretValueRequestBase.cs
│   │   ├── SetIssuerUrlInputBase.cs
│   │   └── ConsentActionRequestBase.cs
│   └── CollaborationControllerBase.cs
│
└── V2026_03_01_Preview/              # API version 2026-03-01-preview
    ├── ApiVersionConstants.cs        # Version constants
    ├── Controllers/
    │   └── CollaborationController.cs
    ├── Models/                       # Versioned request/response models
    │   ├── QueryRunInput.cs
    │   ├── VoteRequest.cs
    │   ├── SecretValueRequest.cs
    │   ├── SetIssuerUrlInput.cs
    │   └── ConsentActionRequest.cs
    └── Schema/                       # Generated TypeScript types
        ├── index.ts                  # Re-exports all types
        ├── frontend.ts               # Main API types
        ├── dataset.ts                # Dataset schema types
        └── cleanroom.ts              # Cleanroom schema types

# OpenAPI specs are in src/sdk/openapi/V*/
```

## Versioning Strategy

### Adding a New API Version

1. **Create new version folder**: Copy `V2026_03_01_Preview` to a new folder (e.g., `V2027_01_01`)

2. **Update version constants**: Modify `ApiVersionConstants.cs` with the new version string

3. **Evolve models**:
   - For non-breaking changes: Add optional properties to versioned models
   - For breaking changes: Create new model classes that inherit from base or previous version

4. **Update controller**: Either inherit from previous version's controller or create fresh

5. **Register version**: Add the new version to `Startup.cs`:
   ```csharp
   services.Configure<MvcOptions>(options =>
   {
       options.Filters.Add(new ApiVersionValidationFilter(
           [V2026_03_01_Preview.ApiVersionConstants.Version,
            V2027_01_01.ApiVersionConstants.Version]));
   });
   ```

6. **Update OpenAPI spec**: Create new versioned folder in `src/sdk/openapi/V*/` with updated schemas

### Breaking Change Examples

**Adding a required field** (breaking):
```csharp
// V2027_01_01/Models/QueryRunInput.cs
public class QueryRunInput : V2026_03_01_Preview.Models.QueryRunInput
{
    public required string NewRequiredField { get; set; }
}
```

**Renaming a field** (breaking):
```csharp
// V2027_01_01/Models/QueryRunInput.cs
public class QueryRunInput : QueryRunInputBase
{
    [Obsolete("Use OptimizationMode instead")]
    public bool UseOptimizer { get; set; }

    public required string OptimizationMode { get; set; }
}
```

**Adding an optional field** (non-breaking):
```csharp
// V2026_03_01_Preview/Models/QueryRunInput.cs - can modify in place
public class QueryRunInput : QueryRunInputBase
{
    public bool UseOptimizer { get; set; } = false;
    public bool DryRun { get; set; } = false;
    public string? NewOptionalField { get; set; }  // Non-breaking addition
}
```

## Model Layers

- **Base Models** (`Common/BaseModels/`): Abstract classes with core required properties
- **Versioned Models** (`V*/Models/`): Concrete classes for specific API versions
- **Internal Models** (`../Models/`): Used internally between services, not part of public API

## Best Practices

1. **Never modify base models** once they're released - only extend
2. **Keep versioned models thin** - delegate to shared services
3. **Document breaking changes** in the OpenAPI spec description
4. **Maintain backward compatibility** by supporting old versions for a deprecation period
5. **Use model conversion** when internal and API models diverge
