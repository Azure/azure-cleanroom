# Azure Monitor and Managed Grafana Integration for Cleanroom Observability

This directory contains the technical plans and architecture documentation for integrating Azure Monitor and Azure Managed Grafana with cleanroom cluster observability.

## Documents

### Azure Monitor Integration

**[azure-monitor-integration-plan.md](azure-monitor-integration-plan.md)** - Main integration plan (human-readable)
- Problem statement and solution overview
- Architecture diagrams and component descriptions
- Authentication flow and data collection process
- Architecture decisions with rationale
- Implementation plan at conceptual level

**[azure-monitor-detailed-implementation.md](azure-monitor-detailed-implementation.md)** - Code-level implementation details
- Phase-by-phase implementation strategy
- File paths, line numbers, and method signatures
- NuGet packages and dependencies
- OTel collector configuration
- Testing scenarios and validation steps

**Architecture Diagrams**:
- [current-state-local-observability.excalidraw](current-state-local-observability.excalidraw) - Current Helm-based observability stack
- [target-state-azure-monitor.excalidraw](target-state-azure-monitor.excalidraw) - Target Azure Monitor architecture

### Azure Managed Grafana Integration

**[grafana-plan.md](grafana-plan.md)** - Grafana integration plan (human-readable)
- Cross-tenant architecture overview
- First-party service principal authentication
- Multi-workspace filtering design
- Dashboard conversion requirements
- Security considerations

**[grafana-dashboard-conversion.md](grafana-dashboard-conversion.md)** - Dashboard conversion guide
- Log Analytics table schemas (PrometheusMetrics_CL, CleanroomLogs_CL, Traces)
- PromQL/LogQL to KQL conversion examples
- Dashboard variable configuration
- Common KQL query patterns
- Testing and troubleshooting procedures

**Architecture Diagram**:
- [grafana-cross-tenant-architecture.excalidraw](grafana-cross-tenant-architecture.excalidraw) - Cross-tenant Grafana architecture

## Quick Reference

### Azure Monitor Flow
```
Workload Pods → OTel Collector → DCE → DCR → Log Analytics Workspace
                      ↓
              Workload Identity (MI with "Monitoring Metrics Publisher" role)
```

### Grafana Flow
```
User → Azure Managed Grafana (AME Tenant) → KQL Queries → LA Workspace (Customer Tenant)
                     ↓
       First-Party Service Principal (Log Analytics Reader)
```

## Out of Scope

- Dashboard conversion implementation (documented requirements only)
- Azure Managed Grafana instance provisioning
- Resource Provider implementation for cross-tenant connections
- Virtual cluster provider support (AKS only)

