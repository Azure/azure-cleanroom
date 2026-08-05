---
applyTo: "**/*.{ts,js}"
---

# TypeScript / JavaScript Conventions (azure-cleanroom)

## Formatting
- **Prettier** with `trailingComma: "none"`.
- 2 spaces for indentation.
- ESLint with flat config (`eslint.experimental.useFlatConfig: true`).

## Auto-Generated Code
- API types in `src/sdk/` and `vendored_sdks/` are auto-generated from TypeSpec/OpenAPI specs.
- Do NOT edit generated files. Modify the TypeSpec source in `src/specifications/` and regenerate.
- Regenerate: `pwsh src/specifications/generate_models.ps1`

## CGS CCF App
- The governance CCF application is in `src/governance/ccf-app/js/`.
- Routes defined in `app.json`.
