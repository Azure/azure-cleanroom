// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace Controllers;

public interface IOperationStore
{
    void AddOperation(OperationStatus job);

    OperationStatus? GetStatus(string jobId);

    void UpdateStatus(string jobId, Action<OperationStatus> updateAction);
}

public class OperationStatus
{
    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = default!;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "Queued"; // Queued, Running, Completed, Failed

    [JsonPropertyName("progress")]
    public List<string> Progress { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonPropertyName("error")]
    public ODataError? Error { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonPropertyName("statusCode")]
    public int? StatusCode { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonPropertyName("resource")]
    public object? Resource { get; set; }
}

public class InMemoryOperationStore : IOperationStore
{
    private readonly ConcurrentDictionary<string, OperationStatus> jobs = new();

    public void AddOperation(OperationStatus job)
    {
        this.jobs[job.OperationId] = job;
    }

    public OperationStatus? GetStatus(string jobId)
    {
        return this.jobs.TryGetValue(jobId, out var job) ? job : null;
    }

    public void UpdateStatus(string jobId, Action<OperationStatus> updateAction)
    {
        if (this.jobs.TryGetValue(jobId, out var job))
        {
            updateAction(job);
        }
    }
}