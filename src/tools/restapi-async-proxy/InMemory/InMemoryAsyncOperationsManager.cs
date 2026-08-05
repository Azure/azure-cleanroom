// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using ArmClient;
using Common;
using Controllers;
using OperationStatus = EntityDataModel.OperationStatus;

namespace MetaRpDatabase.Test;

internal class InMemoryAsyncOperationsManager : IAsyncOperationsManager
{
    private ConcurrentDictionary<string, Operation> resources;

    public InMemoryAsyncOperationsManager()
    {
        this.resources = new ConcurrentDictionary<string, Operation>(
            StringComparer.OrdinalIgnoreCase);
    }

    public Task RecordOperationStart(
        string operationId,
        string method,
        string path,
        CancellationTokenSource cts)
    {
        var opStatus = NewAcceptedOperationStatus(operationId);
        opStatus.Method = method;
        opStatus.Path = path;

        this.resources[operationId] = new Operation
        {
            Id = operationId,
            Status = opStatus,
            Cts = cts
        };

        static OperationStatus NewAcceptedOperationStatus(string operationId)
        {
            var uriParts = operationId.Split('/').ToList();
            string name = uriParts[uriParts.FindIndex(
                s => string.Equals(s, "operationStatuses", StringComparison.OrdinalIgnoreCase)) + 1];
            return new OperationStatus
            {
                Id = operationId,
                StartTime = DateTime.UtcNow.ToString(),
                Status = nameof(OperationState.Accepted),
            };
        }

        return Task.CompletedTask;
    }

    public Task RecordOperationEnd(
        string operationId,
        string status,
        int statusCode,
        JsonObject? result,
        ODataError? error)
    {
        if (this.resources.TryGetValue(operationId, out var item))
        {
            item.Status.EndTime = DateTime.UtcNow.ToString();
            item.Status.Status = status;
            item.Status.StatusCode = statusCode;
            item.Status.Result = result;
            item.Status.Error = error;
        }

        return Task.CompletedTask;
    }

    public Task<OperationStatus?> GetOperationStatus(string operationId)
    {
        this.resources.TryGetValue(operationId, out var item);
        return Task.FromResult(item?.Status);
    }

    public async Task<bool> CancelOperation(string operationId)
    {
        if (this.resources.TryGetValue(operationId, out var item))
        {
            await item.Cts.CancelAsync();
            return true;
        }

        return false;
    }

    Task<Controllers.OperationStatus?> IAsyncOperationsManager.GetOperationStatus(string operationId)
    {
        throw new NotImplementedException();
    }

    public class Operation
    {
        public string Id { get; set; } = default!;

        public OperationStatus Status { get; set; } = default!;

        public CancellationTokenSource Cts { get; set; } = default!;
    }
}
