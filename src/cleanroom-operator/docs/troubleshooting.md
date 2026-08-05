# Troubleshooting

## Checking status

When a resource fails, the CLI prints the error and a trace ID:

```
✗ ModelRegistration tinyllama failed after 42s (trace: abc123def456...)
  DatasetDocCreateFailed: PUT /userdocuments/tinyllama-dataset returned 409
```

Use `status` to see the full picture:

```powershell
kubectl cleanroom md status tinyllama
```

```
  Phase:     Failed
  Message:   PUT /userdocuments/tinyllama-dataset returned 409
  TraceId:   abc123def456...
  Conditions:
    ✓ ModelUploaded
    ✓ FlexNodeReady
    ✓ CcfUserReady
    ✓ OidcIssuerReady
    ✓ AccessConfigured
    ✗ DatasetDocReady
    ○ ModelDocReady
```

The same works for `environment status` and `mdi status`.

## Aspire dashboard

The operator emits OpenTelemetry traces and structured logs to an
Aspire dashboard running in the management cluster. Use it to see the
full distributed trace for a failed operation:

```powershell
kubectl cleanroom dev aspire-dashboard
```

```
Aspire dashboard: http://localhost:18888
Press Ctrl+C to stop
```

Open `http://localhost:18888` in your browser. Go to the **Traces** tab
and filter by the trace ID from the status output. The trace shows
every controller step, external API call (CGS, CCF, Azure), and where
the failure occurred.

The **Structured Logs** tab shows log entries correlated to the same
trace ID, including request/response details and error messages.

## Collecting logs offline

To capture a snapshot of all diagnostics for offline analysis or to
share with someone:

```powershell
kubectl cleanroom dev collect-logs --output-dir ./debug-logs
```

This collects:
- All CR resources (environments, clusters, CCF networks, etc.)
- Pod descriptions and container logs
- Kubernetes events
- Aspire telemetry (traces, logs, resources)

Everything is written as JSON files under the output directory.

## Retrying after a failure

Once you've identified and fixed the issue, use `reconcile` to
retrigger the operation:

```powershell
# Retry an environment
kubectl cleanroom environment reconcile my-env

# Retry a model deployment
kubectl cleanroom md reconcile tinyllama

# Retry a model deployment instance
kubectl cleanroom mdi reconcile tinyllama-svc
```

The `reconcile` command sets a `reconcileRequestedAt` annotation on
the CR, waits for the controller to acknowledge it, then watches for
the resource to reach `Ready`. The controller re-enters the step
machine at the first incomplete step — already-completed steps
(conditions that are `True`) are skipped.

`reconcile` is aliased as `retry` for convenience:

```powershell
kubectl cleanroom md retry tinyllama
```
