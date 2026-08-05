# Azure Managed Grafana Dashboard Conversion - Detailed Implementation

## Overview

This document provides code-level details for converting existing Grafana dashboards from local observability (Prometheus/Loki/Tempo) to Azure Monitor data sources (Log Analytics workspace with KQL queries).

## Log Analytics Table Schemas

### PrometheusMetrics_CL

**Stream**: `Microsoft-PrometheusMetrics`  
**Type**: Custom table (fixed name by Azure Monitor)

**Key Columns**:
```
TimeGenerated       datetime      When the metric was generated
Name                string        Metric name (e.g., "http_requests_total")
Labels              dynamic       JSON object with label key-value pairs
Value               real          Metric value
Resource_*          string        Resource attributes (service_name, namespace, etc.)
WorkspaceName       string        LA workspace display name
```

**Example Row**:
```json
{
  "TimeGenerated": "2024-06-18T12:00:00Z",
  "Name": "http_requests_total",
  "Labels": {"method": "GET", "status": "200", "handler": "/api/jobs"},
  "Value": 1547.0,
  "Resource_service_name": "analytics-agent",
  "WorkspaceName": "customer-cluster-1"
}
```

### CleanroomLogs_CL

**Stream**: `Custom-LogData`  
**Type**: Custom table (configurable name)

**Key Columns**:
```
TimeGenerated       datetime      When the log was generated
Body                string        Log message body
SeverityText        string        Log level (INFO, WARN, ERROR, etc.)
SeverityNumber      int           Numeric severity (0-24)
Resource_*          string        Resource attributes
Attributes_*        dynamic       Custom log attributes
WorkspaceName       string        LA workspace display name
```

**Example Row**:
```json
{
  "TimeGenerated": "2024-06-18T12:00:05Z",
  "Body": "Failed to connect to database: connection timeout",
  "SeverityText": "ERROR",
  "SeverityNumber": 17,
  "Resource_service_name": "spark-frontend",
  "WorkspaceName": "customer-cluster-1"
}
```

### Traces

**Stream**: `Microsoft-Trace`  
**Type**: Standard Azure Monitor table

**Key Columns**:
```
TimeGenerated       datetime      Span start time
TraceId             string        Distributed trace ID
SpanId              string        Unique span ID
ParentSpanId        string        Parent span ID (null for root)
Name                string        Span name/operation
Duration            real          Span duration in milliseconds
Resource_*          string        Resource attributes
Attributes_*        dynamic       Span attributes
WorkspaceName       string        LA workspace display name
```

**Example Row**:
```json
{
  "TimeGenerated": "2024-06-18T12:00:10Z",
  "TraceId": "abc123def456",
  "SpanId": "span789",
  "ParentSpanId": null,
  "Name": "ProcessSparkJob",
  "Duration": 1250.5,
  "Resource_service_name": "analytics-agent"
}
```

## Dashboard Variable Configuration

### Workspace Selector Variable

**Variable Definition (Grafana JSON)**:
```json
{
  "templating": {
    "list": [
      {
        "name": "workspace",
        "type": "query",
        "label": "Workspace",
        "datasource": {
          "type": "grafana-azure-monitor-datasource",
          "uid": "azure-monitor-datasource"
        },
        "query": {
          "queryType": "Azure Log Analytics",
          "azureLogAnalytics": {
            "query": "union PrometheusMetrics_CL, CleanroomLogs_CL, Traces | distinct WorkspaceName | order by WorkspaceName asc",
            "resource": "/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.OperationalInsights/workspaces/{workspace}"
          }
        },
        "current": {
          "selected": false,
          "text": "customer-cluster-1",
          "value": "customer-cluster-1"
        },
        "multi": false,
        "includeAll": false,
        "refresh": 1,
        "sort": 1
      }
    ]
  }
}
```

**Usage in Queries**:
All panel queries must include:
```kql
| where WorkspaceName == "$workspace"
```

## Query Conversion Examples

### Example 1: Metrics - Request Rate

**Before (PromQL)**:
```promql
rate(http_requests_total{job="analytics-agent",status="200"}[5m])
```

**After (KQL)**:
```kql
PrometheusMetrics_CL
| where TimeGenerated > ago(5m)
| where Name == "http_requests_total"
| where WorkspaceName == "$workspace"
| where Labels.status == "200"
| where Resource_service_name == "analytics-agent"
| extend LabelHash = hash(Labels)
| summarize Value = sum(Value) by bin(TimeGenerated, 1m), LabelHash, Labels
| extend Rate = Value / 60.0
| project TimeGenerated, Rate
| order by TimeGenerated asc
```

**Key Conversions**:
- `rate()` → `summarize sum()` with time binning + divide by seconds
- `{job="..."}` → `where Resource_service_name == "..."`
- Label filters → `where Labels.key == "value"`
- Time range `[5m]` → `where TimeGenerated > ago(5m)`

### Example 2: Metrics - Average with Percentiles

**Before (PromQL)**:
```promql
histogram_quantile(0.95, sum(rate(http_request_duration_seconds_bucket[5m])) by (le))
```

**After (KQL)**:
```kql
PrometheusMetrics_CL
| where TimeGenerated > ago(5m)
| where Name == "http_request_duration_seconds"
| where WorkspaceName == "$workspace"
| summarize percentile(Value, 95) by bin(TimeGenerated, 1m)
| render timechart
```

**Key Conversions**:
- `histogram_quantile()` → `percentile()`
- Bucket aggregation → Direct percentile calculation in KQL

### Example 3: Logs - Error Search

**Before (LogQL)**:
```logql
{app="spark-frontend", level="error"} |= "exception" | json | line_format "{{.timestamp}} {{.message}}"
```

**After (KQL)**:
```kql
CleanroomLogs_CL
| where TimeGenerated > ago(1h)
| where WorkspaceName == "$workspace"
| where Resource_service_name == "spark-frontend"
| where SeverityText == "ERROR"
| where Body contains "exception"
| project TimeGenerated, Body, Resource_service_name, SeverityText
| order by TimeGenerated desc
```

**Key Conversions**:
- `{app="..."}` → `where Resource_service_name == "..."`
- `level="error"` → `where SeverityText == "ERROR"`
- `|=` operator → `where Body contains "..."`
- `| json` → KQL handles JSON natively in Body field
- `line_format` → `project` for column selection

### Example 4: Logs - Aggregation Count

**Before (LogQL)**:
```logql
sum(count_over_time({app="analytics-agent"}[5m])) by (level)
```

**After (KQL)**:
```kql
CleanroomLogs_CL
| where TimeGenerated > ago(5m)
| where WorkspaceName == "$workspace"
| where Resource_service_name == "analytics-agent"
| summarize Count = count() by bin(TimeGenerated, 1m), SeverityText
| render timechart
```

**Key Conversions**:
- `count_over_time()` → `count()`
- `by (level)` → `by SeverityText`
- Time window implicit in `where TimeGenerated`

### Example 5: Traces - Latency Analysis

**Before (Tempo Query)**:
```
Search: { service.name = "analytics-agent" }
Group by: span.name
Aggregate: avg(duration)
```

**After (KQL)**:
```kql
Traces
| where TimeGenerated > ago(30m)
| where WorkspaceName == "$workspace"
| where Resource_service_name == "analytics-agent"
| summarize 
    AvgDuration = avg(Duration),
    P50Duration = percentile(Duration, 50),
    P95Duration = percentile(Duration, 95),
    P99Duration = percentile(Duration, 99),
    Count = count()
    by Name
| order by Count desc
```

**Key Features**:
- Direct span querying with aggregations
- Multiple percentiles in single query
- Group by span name

### Example 6: Traces - Trace Visualization

**Before (Tempo Query)**:
```
Trace ID: abc123def456
```

**After (KQL)**:
```kql
Traces
| where WorkspaceName == "$workspace"
| where TraceId == "abc123def456"
| project TimeGenerated, SpanId, ParentSpanId, Name, Duration, Resource_service_name
| order by TimeGenerated asc
```

**Visualization**: Use Grafana's Trace visualization panel to display hierarchical trace view

## Common KQL Patterns

### Pattern 1: Time-Series Metrics Aggregation
```kql
PrometheusMetrics_CL
| where TimeGenerated between (startTime .. endTime)
| where Name == "metric_name"
| where WorkspaceName == "$workspace"
| summarize avg(Value) by bin(TimeGenerated, 5m), Resource_service_name
| render timechart
```

### Pattern 2: Multi-Label Filtering
```kql
PrometheusMetrics_CL
| where TimeGenerated > ago(1h)
| where Name == "http_requests_total"
| where WorkspaceName == "$workspace"
| where Labels.method == "GET"
| where Labels.status =~ "2.."  // Regex match 2xx
| summarize sum(Value) by bin(TimeGenerated, 1m)
```

### Pattern 3: Log Pattern Matching
```kql
CleanroomLogs_CL
| where TimeGenerated > ago(1h)
| where WorkspaceName == "$workspace"
| where Body matches regex @"ERROR.*database.*timeout"
| project TimeGenerated, Body, Resource_service_name
| order by TimeGenerated desc
```

### Pattern 4: Join Metrics and Logs
```kql
let errors = CleanroomLogs_CL
| where TimeGenerated > ago(1h)
| where WorkspaceName == "$workspace"
| where SeverityText == "ERROR"
| summarize ErrorCount = count() by bin(TimeGenerated, 5m), Resource_service_name;
let metrics = PrometheusMetrics_CL
| where TimeGenerated > ago(1h)
| where Name == "http_requests_total"
| where WorkspaceName == "$workspace"
| summarize RequestCount = sum(Value) by bin(TimeGenerated, 5m), Resource_service_name;
errors
| join kind=inner (metrics) on TimeGenerated, Resource_service_name
| project TimeGenerated, Resource_service_name, ErrorCount, RequestCount, 
          ErrorRate = (ErrorCount * 100.0 / RequestCount)
| render timechart
```

### Pattern 5: Trace Performance Analysis
```kql
Traces
| where TimeGenerated > ago(1h)
| where WorkspaceName == "$workspace"
| where Resource_service_name == "analytics-agent"
| where ParentSpanId == ""  // Root spans only
| summarize 
    Count = count(),
    AvgDuration = avg(Duration),
    P95Duration = percentile(Duration, 95),
    MaxDuration = max(Duration)
    by bin(TimeGenerated, 5m)
| render timechart
```

### Pattern 6: Dynamic Label Access
```kql
PrometheusMetrics_CL
| where TimeGenerated > ago(1h)
| where Name == "http_requests_total"
| where WorkspaceName == "$workspace"
| extend method = tostring(Labels.method)
| extend status = tostring(Labels.status)
| extend handler = tostring(Labels.handler)
| summarize sum(Value) by bin(TimeGenerated, 5m), method, status, handler
```

## Dashboard Panel Configuration

### Panel Example: Request Rate Time Series

```json
{
  "type": "timeseries",
  "title": "HTTP Request Rate - $workspace",
  "datasource": {
    "type": "grafana-azure-monitor-datasource",
    "uid": "azure-monitor-datasource"
  },
  "targets": [
    {
      "queryType": "Azure Log Analytics",
      "azureLogAnalytics": {
        "query": "PrometheusMetrics_CL\n| where TimeGenerated > $__timeFrom\n| where TimeGenerated < $__timeTo\n| where Name == 'http_requests_total'\n| where WorkspaceName == '$workspace'\n| summarize Value = sum(Value) by bin(TimeGenerated, $__interval)\n| extend Rate = Value / $__interval_s\n| project TimeGenerated, Rate",
        "resource": "/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.OperationalInsights/workspaces/{workspace}"
      },
      "refId": "A"
    }
  ],
  "fieldConfig": {
    "defaults": {
      "unit": "reqps"
    }
  }
}
```

**Grafana Time Variables**:
- `$__timeFrom` - Dashboard time range start
- `$__timeTo` - Dashboard time range end
- `$__interval` - Auto-calculated aggregation interval
- `$__interval_s` - Interval in seconds

## Dashboard Files to Convert

Current dashboards in repository:
- `src/cleanroom-cluster/cleanroom-cluster-provider-client/observability/grafana/dashboards/`

**Target Locations** (post-conversion):
- Azure Managed Grafana dashboard library
- Or exported JSON files for manual import

### Conversion Checklist

For each dashboard:
- [ ] Update data source to Azure Monitor
- [ ] Add `$workspace` template variable
- [ ] Convert all panel queries to KQL
- [ ] Update table references (PrometheusMetrics_CL, CleanroomLogs_CL, Traces)
- [ ] Add workspace filter to all queries
- [ ] Test queries against sample LA workspace
- [ ] Update panel titles to include workspace reference
- [ ] Validate time range variables work correctly
- [ ] Check alert rules (if any) and convert thresholds

## Testing Queries

### Test Query 1: Verify Workspace Filtering
```kql
union PrometheusMetrics_CL, CleanroomLogs_CL, Traces
| distinct WorkspaceName
| order by WorkspaceName asc
```
**Expected**: List of all LA workspaces with data

### Test Query 2: Verify Metrics Ingestion
```kql
PrometheusMetrics_CL
| where TimeGenerated > ago(15m)
| where WorkspaceName == "test-workspace"
| summarize count() by Name
| order by count_ desc
| take 10
```
**Expected**: Top 10 metric names with counts

### Test Query 3: Verify Log Ingestion
```kql
CleanroomLogs_CL
| where TimeGenerated > ago(15m)
| where WorkspaceName == "test-workspace"
| summarize count() by SeverityText
```
**Expected**: Count by log level (INFO, WARN, ERROR, etc.)

### Test Query 4: Verify Trace Ingestion
```kql
Traces
| where TimeGenerated > ago(15m)
| where WorkspaceName == "test-workspace"
| summarize count() by Resource_service_name
```
**Expected**: Count by service name

## Troubleshooting

### Issue: No Data Returned
**Possible Causes**:
1. Workspace filter not matching actual WorkspaceName
2. Time range outside of available data
3. First-party SP doesn't have "Log Analytics Reader" role

**Debug Query**:
```kql
union PrometheusMetrics_CL, CleanroomLogs_CL, Traces
| where TimeGenerated > ago(7d)
| summarize 
    MinTime = min(TimeGenerated),
    MaxTime = max(TimeGenerated),
    Count = count()
    by WorkspaceName
```

### Issue: Query Timeout
**Possible Causes**:
1. Too wide time range
2. Expensive aggregations without proper filtering
3. Missing indexes on custom tables

**Optimization**:
- Add time filters early: `where TimeGenerated > ago(1h)`
- Filter by workspace first: `where WorkspaceName == "$workspace"`
- Use `take` to limit results during testing

### Issue: Labels Not Accessible
**Problem**: Labels stored as JSON dynamic field

**Solution**:
```kql
PrometheusMetrics_CL
| extend label_value = tostring(Labels.key_name)
| where label_value == "expected_value"
```

## KQL Reference Links

- [KQL Quick Reference](https://learn.microsoft.com/en-us/azure/data-explorer/kql-quick-reference)
- [String Operators](https://learn.microsoft.com/en-us/azure/data-explorer/kusto/query/datatypes-string-operators)
- [Aggregation Functions](https://learn.microsoft.com/en-us/azure/data-explorer/kusto/query/aggregation-functions)
- [Time Series Analysis](https://learn.microsoft.com/en-us/azure/data-explorer/kusto/query/time-series-analysis)
- [Dynamic Fields](https://learn.microsoft.com/en-us/azure/data-explorer/kusto/query/scalar-data-types/dynamic)
