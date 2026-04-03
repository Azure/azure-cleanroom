// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class QueryRunResult
{
    [JsonPropertyName("id")]
    public string JobId { get; set; } = string.Empty;

    public JobStatus Status { get; set; } = new();

    public List<JobEvent> Events { get; set; } = new();

    [JsonPropertyName("x-ms-correlation-id")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("x-ms-client-request-id")]
    public string? ClientRequestId { get; set; }
}

public class JobStatus
{
    public ApplicationState? ApplicationState { get; set; }

    public DriverInfo? DriverInfo { get; set; }

    public Dictionary<string, string>? ExecutorState { get; set; }

    public int? ExecutionAttempts { get; set; }

    public int? SubmissionAttempts { get; set; }

    public DateTime? TerminationTime { get; set; }

    public DateTime? LastSubmissionAttemptTime { get; set; }
}

public class DriverInfo
{
    public string? PodName { get; set; }

    public string? WebUIAddress { get; set; }

    public int? WebUIPort { get; set; }

    public string? WebUIServiceName { get; set; }
}

public class ApplicationState
{
    public string? State { get; set; }
}

public class JobEvent
{
    public string? Name { get; set; }

    public string? Reason { get; set; }

    public string? Message { get; set; }

    public string? Type { get; set; }

    public DateTime? FirstTimestamp { get; set; }

    public DateTime? LastTimestamp { get; set; }

    public int? Count { get; set; }
}