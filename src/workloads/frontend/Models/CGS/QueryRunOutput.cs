// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class QueryRunOutput
{
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string JobId { get; set; } = string.Empty;

    public bool? DryRun { get; set; }

    public string? JobIdField { get; set; }

    public bool? OptimizationUsed { get; set; }

    public string? Reasoning { get; set; }

    public SkuSettingsResponse? SkuSettings { get; set; }

    [JsonPropertyName("x-ms-correlation-id")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("x-ms-client-request-id")]
    public string? ClientRequestId { get; set; }
}

public class SkuSettingsResponse
{
    public DriverSettingsResponse Driver { get; set; } = new();

    public ExecutorSettingsResponse Executor { get; set; } = new();
}

public class DriverSettingsResponse
{
    public float Cores { get; set; }

    public string Memory { get; set; } = string.Empty;

    public string ServiceAccount { get; set; } = string.Empty;
}

public class ExecutorSettingsResponse
{
    public float Cores { get; set; }

    public string Memory { get; set; } = string.Empty;

    public ExecutorInstancesResponse Instances { get; set; } = new();

    public bool DeleteOnTermination { get; set; }
}

public class ExecutorInstancesResponse
{
    public int Min { get; set; }

    public int Max { get; set; }
}