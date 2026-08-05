---
applyTo: "**/*.cs"
---

# C# Conventions (azure-cleanroom)

See [csharp-style-examples.md](../csharp-style-examples.md) for bad/good examples of every rule below.

## Formatting
- Max line width: 101 characters (Menees Analyzers).
- 4 spaces for indentation (no tabs).
- 2 spaces for JSON, XML, YAML embedded in C# projects.

### Line-breaking decision process

1. Write the full statement on one line.
2. If it fits in 101 chars → keep it on one line. Do not break.
3. If it exceeds 101 chars → break at a natural point (after `(`, before `.`, after `,`).
4. After breaking, check each resulting line. If two consecutive lines can merge and still fit in 101 chars, merge them.

- Target 90-101 character line widths. Code that wraps at 50-70 characters wastes vertical space.

### Argument breaks

- When a function argument is on a separate line, all subsequent arguments must be on new lines too.

### Positional record parameters

- Keep `[property:]` attributes on the same line as their parameter when the combined line fits in 101 chars.

## Namespaces and Usings
- File-scoped namespaces enforced as error (`namespace Foo;` not `namespace Foo { }`).
- `using` directives outside the namespace.
- Sort `System` usings first, then alphabetically.

## Code Style
- Use `this.` prefix for instance members (fields, properties, methods).
- Braces for all control flow (`if`, `for`, `while`, etc.).

### `var` vs explicit type

- Use `var` only when the type is obvious from the right-hand side (`new`, cast, literal, generic factory with type argument, tuple deconstruction, `Enum.Parse<T>()`).
- Use the explicit type when the return type is not immediately clear (method returns, indexers, `string.Equals`, extension methods).

### General

- Expression-bodied members for simple properties/indexers; block bodies for methods/constructors.
- Use `await` instead of `.Result` or `.Wait()`.
- Use `nameof()` for parameter names in argument exceptions.

## Naming
- PascalCase: types, methods, properties, public fields.
- camelCase: local variables, parameters.
- Prefix interfaces with `I`.
- Prefix private field access with `this.`.

## StyleCop Compliance
- **TreatWarningsAsErrors** is enabled.
- SA1201 ordering: enums before records/classes in the same file.
- Member ordering by access then static/instance:
  1. Public static
  2. Public instance
  3. Private static
  4. Private instance
- Within each level: fields → constructors → properties → methods.

## Comments
- All comments end with a period.
- `//` with a space after slashes for single-line comments.

## Copyright Header
```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
```

## Package Management
- Central package management via `Directory.Packages.props`. Do NOT specify versions in individual `.csproj` files.

## Error Handling
- Use specific exception types, not generic `Exception`.
- Include meaningful messages in exceptions.

## Build
```bash
dotnet build src/governance/governance.sln
dotnet test src/governance/test/cgs-tests.csproj --logger "console;verbosity=normal"
dotnet test src/governance/test/cgs-tests.csproj --filter "FullyQualifiedName~ClassName.MethodName"
```
