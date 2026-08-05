# CCF Governance API TypeSpec

TypeSpec definitions for the CCF member governance API (`gov/*` endpoints):
proposals, voting, state digests, recovery, and service state queries.

API version: `2024-07-01`
Generated client namespace: `Microsoft.Ccf.Client`

## Source

Pulled from the [`ccf_gov_2024_07_01`](https://github.com/eddyashton/azure-rest-api-specs/tree/ccf_gov_2024_07_01/specification/confidentialledger/Microsoft.ManagedCcf)
branch on eddyashton's fork of azure-rest-api-specs
([microsoft/CCF#6321](https://github.com/microsoft/CCF/pull/6321)).

The derived OpenAPI spec is checked in at
`src/specifications/schemas/ccf/gov/2024-07-01/gov.json`.

## Changes from upstream

- Renamed namespace from `Microsoft.ManagedCcf` to `Microsoft.Ccf`.
- `main.tsp` moved out of `gov/` to project root; updated `@service` title to
  `"CCF Governance"`; added `@clientNamespace("Microsoft.Ccf.Client")`; kept only
  the `2024-07-01` version enum; removed
  `useDependency(Azure.Core.Versions.v1_0_Preview_2)` (not available in
  azure-core v0.64.0).
- Replaced `@visibility("query")` / `@visibility("read", "query")` with
  `@visibility(Lifecycle.Query)` / `@visibility(Lifecycle.Read, Lifecycle.Query)`
  for TypeSpec compiler v1.8.0+ compatibility.
