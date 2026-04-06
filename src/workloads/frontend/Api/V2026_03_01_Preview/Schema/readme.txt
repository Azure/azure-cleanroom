The TypeScript types are generated from the OpenAPI specs in src/sdk/openapi.

For API version 2026-03-01-preview, generate all types using:

    cd src/sdk/openapi/V2026_03_01_Preview
    npx openapi-typescript frontend.yaml --output ../../../workloads/frontend/Api/V2026_03_01_Preview/Schema/frontend.ts

    cd src/sdk/openapi
    npx openapi-typescript dataset.yaml --output ../../workloads/frontend/Api/V2026_03_01_Preview/Schema/dataset.ts
    npx openapi-typescript cleanroom.yaml --output ../../workloads/frontend/Api/V2026_03_01_Preview/Schema/cleanroom.ts

Generated files:
- frontend.ts - Main API operations and response schemas (from versioned frontend.yaml)
- dataset.ts - Dataset-related schemas (DataSchema, DataAccessPolicy)
- cleanroom.ts - Cleanroom-related schemas (ResourceType)
- index.ts - Re-exports all types for convenient imports

The controller stubs are generated using the OpenAPI Generator CLI:
    openapi-generator-cli generate -i ../../sdk/openapi/V2026_03_01_Preview/frontend.yaml -g aspnetcore -o ./server-stubs

The same spec can be used to generate client SDKs in various programming languages using the OpenAPI Generator CLI. For example, to generate a .net client SDK:
    openapi-generator-cli generate -i ../../sdk/openapi/V2026_03_01_Preview/frontend.yaml -g csharp -o ./sdk/dotnet-client

Note:
- OpenAPI specs are versioned in src/sdk/openapi/V*/ folders.
- Generated TypeScript schemas are in Api/V*/Schema/ folders.