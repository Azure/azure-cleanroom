## Implementation Strategy

### Overview

The implementation follows a layered approach:

1. **Foundation Layer**: Update data models and add Azure SDK dependencies
2. **Azure Resource Layer**: Implement creation of Log Analytics, DCR, DCE, and workload identity
3. **Control Flow Layer**: Refactor observability enablement logic to support dual modes
4. **Integration Layer**: Wire up telemetry endpoints and workload identity across all workload types
5. **User Interface Layer**: Expose the feature through CLI and PowerShell scripts
6. **Cleanup Layer**: Ensure proper resource cleanup on cluster deletion

### Detailed Implementation Plan

#### Phase 1: Foundation - Model and Dependency Updates

**Goal**: Extend the API surface to support the new mode without breaking existing functionality.

**Changes**:
1. **ObservabilityProfileInput.cs**: Add `public bool UseAzureMonitor { get; set; }` property
   - Default value: `false` (backward compatible)
   - Controls whether to use Azure Monitor or local Helm charts

2. **ObservabilityProfile.cs**: Add `UseAzureMonitor` property with JSON serialization attributes
   - Include `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` for optional serialization

3. **aks-cleanroom-cluster-provider.csproj**: Add NuGet package reference
   - Package: `Azure.ResourceManager.OperationalInsights`
   - Provides APIs for Log Analytics workspace, DCR, and DCE management

**Why This Matters**: The profile extension maintains backward compatibility - existing code and configurations work unchanged. Only when `useAzureMonitor: true` is explicitly set does the new behavior activate.

#### Phase 2: Azure Resource Management

**Goal**: Implement infrastructure creation for Azure Monitor ingestion pipeline.

**New Methods in AksClusterProvider.cs**:

1. **CreateLogAnalyticsWorkspaceAsync**
   - Create workspace in cluster resource group
   - Name pattern: `{ToAksName(clusterName)}-la-workspace`
   - Tag with `CleanRoomClusterTag` for lifecycle management
   - Follow existing `forceCreate` pattern to handle pre-existing resources
   - Return: `LogAnalyticsWorkspaceResource`

2. **CreateDataCollectionEndpointAsync**
   - Create DCE for telemetry ingestion
   - Name: `{aksClusterName}-dce`
   - Public network access enabled (pods push from cluster)
   - Location: same as AKS cluster
   - Return: DCE resource with ingestion endpoint URL

3. **CreateDataCollectionRuleAsync**
   - Create DCR that routes telemetry to workspace
   - Name: `{aksClusterName}-dcr`
   - Configure data flows:
     - Prometheus metrics → custom table
     - OTLP logs → custom table
     - OTLP traces → custom table
   - Link to DCE endpoint
   - Link to Log Analytics workspace
   - Return: DCR resource with immutableId

4. **CreateObservabilityWorkloadIdentity**
   - Follow the external-dns pattern (lines 1771-1870 in current code)
   - MI name: `{clusterName}-observability-identity`
   - Create federated credential:
     - Subject: `system:serviceaccount:{namespace}:azure-monitor-otel`
     - Issuer: AKS OIDC issuer URL
   - Apply to all workload namespaces (analytics-agent, inferencing-agent, etc.)
   - Return: UserAssignedIdentityResource

5. **AssignMonitoringRoleToObservabilityIdentity**
   - Assign role: "Monitoring Metrics Publisher" (ID: `3913510d-42f4-4e42-8a64-420c390055eb`)
   - Scope: the DCR resource
   - Follow existing RBAC pattern (lines 1679-1700)
   - Handle `RoleAssignmentExists` gracefully

**Why This Matters**: These methods encapsulate Azure resource creation with proper error handling, tagging, and idempotency. They mirror the existing patterns in the codebase for consistency.

#### Phase 3: Observability Control Flow Refactoring

**Goal**: Make `EnableClusterObservabilityAsync` mode-aware while preserving existing behavior.

**Refactoring Strategy**:

Current structure (lines 1560-1663):
```csharp
EnableClusterObservabilityAsync(...)
{
    // Setup namespace and DNS
    // Install Prometheus Helm chart
    // Install Loki Helm chart  
    // Install Tempo Helm chart
    // Install Grafana dashboards and chart
    // Wait for all services to be ready
}
```

New structure:
```csharp
EnableClusterObservabilityAsync(...)
{
    // Common setup (both modes)
    CreateNamespace(Constants.ObservabilityNamespace)
    CreatePrivateDNSZone(...)
    
    if (input.ObservabilityProfile.UseAzureMonitor)
    {
        // Azure Monitor path
        laWorkspace = await CreateLogAnalyticsWorkspaceAsync(...)
        dce = await CreateDataCollectionEndpointAsync(...)
        dcr = await CreateDataCollectionRuleAsync(laWorkspace, dce, ...)
        observabilityMi = await CreateObservabilityWorkloadIdentity(...)
        await AssignMonitoringRoleToObservabilityIdentity(observabilityMi, dcr)
        
        // Store DCE endpoint and DCR immutableId for later use
        // in workload deployment
    }
    else
    {
        // Local Helm charts path (existing logic)
        InstallPrometheusChart(...)
        InstallLokiChart(...)
        InstallTempoChart(...)
        InstallGrafanaDashboards(...)
        InstallGrafanaChart(...)
        
        if (!noWaitOnReady)
        {
            WaitForPrometheusUp(...)
            WaitForLokiUp(...)
            WaitForTempoUp(...)
            WaitForGrafanaUp(...)
        }
    }
}
```

**Why This Matters**: The conditional branching makes the mode switch explicit and maintains the existing code path untouched when Azure Monitor isn't enabled. This minimizes regression risk.

#### Phase 4: OTel Collector Configuration

**Goal**: Configure the OpenTelemetry collector to authenticate and push telemetry to Azure Monitor when `useAzureMonitor: true`.

**File**: `src/otel-collector/src/otel_collector/otel-config.yaml.j2`

**Current State** (lines 67-84):
- Uses `prometheusremotewrite`, `otlphttp/logs`, `otlp/tempo` exporters
- No authentication (works for in-cluster services)
- Static service endpoints (prometheus-service, loki-service, tempo-service)

**Target State**:
Add conditional Azure Monitor configuration using OTLP HTTP + OAuth2:

1. **OAuth2 Client Extension** (for workload identity authentication):
```yaml
{% if use_azure_monitor %}
extensions:
  oauth2client:
    token_url: https://login.microsoftonline.com/{{ azure_tenant_id }}/oauth2/v2.0/token
    client_id: {{ observability_mi_client_id }}
    token_file: /var/run/secrets/azure/tokens/azure-identity-token
    scopes:
      - https://monitor.azure.com/.default
{% endif %}
```

2. **Azure Monitor OTLP HTTP Exporters** (three DCE streams):
```yaml
{% if use_azure_monitor %}
exporters:
  otlphttp/azuremonitor-metrics:
    endpoint: {{ dce_endpoint }}/dataCollectionRules/{{ dcr_immutable_id }}/streams/Microsoft-PrometheusMetrics
    auth:
      authenticator: oauth2client
  
  otlphttp/azuremonitor-logs:
    endpoint: {{ dce_endpoint }}/dataCollectionRules/{{ dcr_immutable_id }}/streams/Custom-LogData
    auth:
      authenticator: oauth2client
  
  otlphttp/azuremonitor-traces:
    endpoint: {{ dce_endpoint }}/dataCollectionRules/{{ dcr_immutable_id }}/streams/Microsoft-Trace
    auth:
      authenticator: oauth2client
{% else %}
  # Existing local exporters (lines 67-84)
  prometheusremotewrite:
    endpoint: "http://prometheus-service:9090/api/v1/write"
  # ... etc
{% endif %}
```

3. **Service Pipelines** (route to appropriate exporters):
```yaml
service:
{% if use_azure_monitor %}
  extensions: [oauth2client]
{% endif %}
  pipelines:
    metrics:
      receivers: [otlp, apachespark, prometheus]
      processors: [batch, resource, transform/resource_labels, transform/spark]
{% if use_azure_monitor %}
      exporters: [otlphttp/azuremonitor-metrics]
{% else %}
      exporters: [prometheusremotewrite]
{% endif %}
    
    logs:
      receivers: [otlp]
      processors: [batch]
{% if use_azure_monitor %}
      exporters: [otlphttp/azuremonitor-logs]
{% else %}
      exporters: [otlphttp/logs]
{% endif %}
    
    traces:
      receivers: [otlp]
      processors: [batch]
{% if use_azure_monitor %}
      exporters: [otlphttp/azuremonitor-traces]
{% else %}
      exporters: [otlp/tempo]
{% endif %}
```

**Template Variables to Pass**:
- `use_azure_monitor`: boolean flag from `ObservabilityProfile.UseAzureMonitor`
- `azure_tenant_id`: From Azure environment or cluster OIDC issuer
- `observability_mi_client_id`: Client ID of observability managed identity
- `dce_endpoint`: DCE endpoint URL (e.g., `https://{dce-name}.eastus-1.ingest.monitor.azure.com`)
- `dcr_immutable_id`: DCR immutableId from creation response

**Environment Variables** (pod spec):
Add to all workload pods that run OTel collector sidecars:
```yaml
env:
  - name: AZURE_CLIENT_ID
    value: {{ observability_mi_client_id }}
  - name: AZURE_TENANT_ID
    value: {{ azure_tenant_id }}
  - name: AZURE_FEDERATED_TOKEN_FILE
    value: /var/run/secrets/azure/tokens/azure-identity-token
```

**Verification Step**:
Check `src/otel-collector/builder-config.yaml` includes `oauth2clientauthextension`:
```yaml
extensions:
  - gomod: go.opentelemetry.io/collector/extension/auth/oauth2clientauthextension v0.xxx.x
```
If missing, add this extension to the builder configuration.

**Why This Matters**: 
- The `oauth2client` extension handles Azure AD authentication automatically using workload identity
- No manual credential management or token refresh logic needed
- Three separate exporters ensure metrics, logs, and traces flow to correct DCR streams
- Conditional logic preserves backward compatibility with local observability mode

#### Phase 5: Workload Pod Configuration

**Goal**: Configure workload pods to use Azure identity and pass OTel template variables.

**Helm Values Updates**:
Add service account configuration to `values.app.yaml` templates for:
- spark-analytics-agent
- spark-frontend  
- kserve-inferencing-agent
- kserve-inferencing-frontend

Template addition:
```yaml
serviceAccount:
  annotations:
    azure.workload.identity/client-id: <OBSERVABILITY_MI_CLIENT_ID>
```

**Update Locations in AksClusterProvider.cs**:
- `EnableAnalyticsWorkloadAsync` (lines ~1448-1461)
- `EnableKServeInferencingWorkloadAsync` (lines ~2054-2068, 2265-2269, 2488-2492)

**Changes**:
1. Replace inline endpoint construction with centralized helper method
2. Pass DCE endpoint, DCR immutableId, tenant ID, and MI client ID to OTel config template rendering
3. Add environment variables for Azure identity configuration
4. Set service account annotation in Helm values

**New Helper Method**:
```csharp
private OTelConfigVariables GetOTelConfigVariables(
    CleanRoomClusterInput input,
    string? dceEndpoint,
    string? dcrImmutableId,
    string? tenantId,
    string? miClientId)
{
    bool useAzureMonitor = input.ObservabilityProfile?.UseAzureMonitor == true;
    
    return new OTelConfigVariables
    {
        UseAzureMonitor = useAzureMonitor,
        DceEndpoint = dceEndpoint,
        DcrImmutableId = dcrImmutableId,
        AzureTenantId = tenantId,
        ObservabilityMiClientId = miClientId
    };
}
```

**Why This Matters**: Centralized configuration ensures all workload types receive consistent Azure Monitor settings and authentication parameters.

#### Phase 6: User Interface - CLI and Scripts

**Goal**: Expose the feature to users through command-line interfaces.

**Azure CLI Extension Updates** (`src/tools/azure-cli-extension/cleanroom/`):

1. **_params.py**:
   - Add `use_azure_monitor` boolean parameter to `cluster create` and `cluster up` commands
   - Help text: "Whether to use Azure Monitor for observability instead of local Helm charts"
   - Options list: `["--use-azure-monitor"]`

2. **custom_cleanroom_cluster.py**:
   - Update `cluster_create` function to accept `use_azure_monitor` parameter
   - When `use_azure_monitor=True`, add to request body:
     ```python
     "observabilityProfile": {
         "enabled": True,
         "useAzureMonitor": True
     }
     ```
   - Same pattern for `cluster_up` function

**PowerShell Script Updates**:

1. **samples/workloads/azcli/deploy-cluster.ps1**:
   - Add parameter: `[switch]$useAzureMonitor`
   - In cluster create command builder (~line 413):
     ```powershell
     if ($useAzureMonitor) {
         $clusterCreateCmd += @("--use-azure-monitor")
     }
     ```

2. **test/onebox/multi-party-collab/cleanroom-cluster-up.ps1**:
   - Add parameter: `[switch]$useAzureMonitor`
   - Pass `--use-azure-monitor` flag to `az cleanroom cluster up` when switch is present

**Why This Matters**: Consistent UX across CLI and scripts. Users can opt into Azure Monitor mode with a simple flag.

#### Phase 7: Cleanup and Validation

**Goal**: Ensure proper resource lifecycle and error handling.

**DeleteCluster Updates** (lines 843-912):

Add deletion logic following the existing pattern:
```csharp
// After deleting MIs
deleteTasks.Clear();
var dcrsToDelete = await GetDataCollectionRules(clClusterName, resourceGroupResource);
foreach (var resource in dcrsToDelete)
{
    deleteTasks.Add(Task.Run(async () => {
        await resource.DeleteAsync(WaitUntil.Completed);
    }));
}
await Task.WhenAll(deleteTasks);

// Same for DCEs
// DO NOT delete Log Analytics workspace - preserve for audit/compliance
```

**Validation for Virtual Provider** (VirtualClusterProvider.cs):

Add check in `CreateClusterValidate`:
```csharp
if (input?.ObservabilityProfile?.UseAzureMonitor == true)
{
    return new ODataError(
        code: "AzureMonitorNotSupportedOnVirtual",
        message: "Azure Monitor observability is only supported on AKS clusters. " +
                 "Virtual clusters use local Helm charts (Prometheus/Loki/Tempo/Grafana).");
}
```

**Why This Matters**: Clean resource lifecycle prevents orphaned resources. Validation prevents misconfiguration.

### Testing Strategy

**Test 1: Backward Compatibility - Local Mode**
- Deploy cluster with `--enable-observability` (without `--use-azure-monitor`)
- Verify Prometheus, Loki, Tempo, Grafana pods are running in observability namespace
- Verify workload telemetry is collected and queryable in Grafana
- Verify NO Azure Monitor resources (LA workspace, DCR, DCE) are created
- Expected result: Existing behavior unchanged

**Test 2: Azure Monitor Mode**
- Deploy AKS cluster with `--enable-observability --use-azure-monitor`
- Verify Azure resources created:
  - Log Analytics workspace: `{aksClusterName}-la-workspace`
  - DCE: `{aksClusterName}-dce`
  - DCR: `{aksClusterName}-dcr`
  - Managed identity: `{clusterName}-observability-identity`
- Verify NO Prometheus/Loki/Tempo/Grafana pods in observability namespace
- Verify workload pods have `azure-monitor-otel` service account with workload identity annotations
- Verify telemetry data appears in Log Analytics workspace (query custom tables)
- Expected result: Telemetry routed to Azure Monitor successfully

**Test 3: Cleanup Behavior**
- Delete cluster deployed in Test 2 using `az cleanroom cluster delete`
- Verify DCR deleted
- Verify DCE deleted
- Verify observability MI deleted
- Verify Log Analytics workspace STILL EXISTS
- Verify historical telemetry data remains queryable
- Expected result: Infrastructure cleaned up, data preserved

**Test 4: Virtual Provider Validation**
- Attempt to create virtual cluster with `--use-azure-monitor`
- Expected result: Validation error returned before cluster creation starts

## Out of Scope (Future Work)

The following items are explicitly out of scope for this implementation phase and will be addressed in separate work:

### Azure Managed Grafana Integration (Separate Plan Document Needed)

**Topics to Cover**:
1. **Provisioning Managed Grafana Instance**
   - Terraform/Bicep templates or manual Azure Portal setup
   - Choosing the right SKU (Standard vs Essential)
   - Network configuration (public vs private endpoint)

2. **Connecting to Log Analytics Workspace**
   - Adding Azure Monitor datasource in Grafana
   - Configuring managed identity authentication
   - Granting Grafana MI the "Monitoring Reader" role on LA workspace

3. **Dashboard Migration**
   - Importing existing JSON dashboards from `src/cleanroom-cluster/cleanroom-cluster-provider-client/observability/grafana/dashboards/`
   - Updating datasource UIDs to match Log Analytics datasource
   - Translating PromQL queries to KQL where necessary
   - Adding datasource template variables for multi-workspace filtering

4. **Multi-Cluster Observability**
   - Connecting Grafana to multiple Log Analytics workspaces
   - Creating workspace selector variable in dashboards
   - Unified cross-cluster views

5. **RBAC and Access Control**
   - Configuring Grafana workspace RBAC (Admin, Editor, Viewer roles)
   - Integration with Azure AD for SSO
   - Per-dashboard access controls
