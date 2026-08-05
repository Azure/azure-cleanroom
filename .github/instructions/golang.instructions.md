---
applyTo: "**/*.go"
---

# Go Conventions (azure-cleanroom)

## Linting
- golangci-lint with shadow checking enabled (`govet.check-shadowing: true`).
- Run: `golangci-lint run`

## Logging
- Use Logrus with structured fields and full timestamps.
- Include descriptive context in log messages.

## Tracing
- OpenTelemetry with OTLP gRPC exporter.

## Error Handling
- Explicit `if err != nil` checks with descriptive log messages.
- Wrap errors with context: `fmt.Errorf("doing X: %w", err)`.

## Formatting
- Max line width: 100 characters.
- Use `gofmt` / `goimports`.

## Build
> **IMPORTANT**: Never use `go build`, `go run`, or `go test` directly. Always use the PowerShell build scripts under `build/`. These handle Docker-based builds, image tagging, and registry push.

```bash
# Operator
pwsh build/cleanroom-operator/build-cleanroom-operator.ps1 -push

# Proxy ext processor
pwsh build/ccr/build-ccr-proxy-ext-processor.ps1 -push
```
