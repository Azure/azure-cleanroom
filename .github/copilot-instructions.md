# Copilot Code Generation Instructions

## General Formatting

- Maximum line width is 100 characters.
- Use 4 spaces for indentation (no tabs).
- Use 2 spaces for JSON, XML, YAML, and TypeScript/JavaScript files.

## C# Coding Conventions

### Comments and Documentation

- All comments must end with a period.
- Use `//` for single-line comments with a space after the slashes.
- Multi-line comments should use `/* */` style sparingly; prefer multiple single-line comments.

### Namespaces and Usings

- Use file-scoped namespaces (`namespace Foo;` not `namespace Foo { }`).
- Place `using` directives outside the namespace.
- Sort `System` using directives first, then all others alphabetically by namespace.

### Code Style

- Use `this.` prefix for instance members (fields, properties, methods).
- Prefer explicit types over `var` for non-obvious types.
- Use braces for all control flow statements (if, for, while, etc.).
- Use expression-bodied members for simple properties and indexers.
- Use traditional block bodies for methods, constructors, and operators.

### Async/Await

- Use `await` instead of `.Result` or `.Wait()`.

### Naming Conventions

- Use PascalCase for types, methods, properties, and public fields.
- Use camelCase for local variables and parameters.
- Prefix interfaces with `I` (e.g., `IMyInterface`).
- Prefix private fields with `this.` when accessing them.

### Error Handling

- Use specific exception types rather than generic `Exception`.
- Include meaningful error messages in exceptions.
- Use `nameof()` for parameter names in argument exceptions.

### StyleCop Compliance

- Follow StyleCop ordering rules:
  - Constants and static readonly fields first.
  - Within each member type, order by access then by static/instance:
    1. Public static members
    2. Public instance members
    3. Private static members
    4. Private instance members
  - Fields, constructors, properties, methods (in that order within each access level).

### Copyright Header

All C# files should include the following copyright header:

```csharp
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
```

## PowerShell Conventions

- Use approved verbs for function names (Get-, Set-, New-, Remove-, etc.).
- Use PascalCase for function names and parameters.

## Shell Scripts

- Use `#!/bin/bash` shebang for bash scripts.
- Use `set -e` to exit on errors.
