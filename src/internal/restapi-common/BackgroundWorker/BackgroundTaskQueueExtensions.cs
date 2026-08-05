// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Controllers;

public static class BackgroundTaskQueueExtensions
{
    /// <summary>
    /// Enqueue a long-running operation and return its operation ID.
    /// Transport-agnostic: does not depend on HttpContext.
    /// </summary>
    /// <typeparam name="TResource">The type of the resource produced
    /// by the background operation.</typeparam>
    /// <param name="queue">The background task queue.</param>
    /// <param name="operationStore">The operation store that tracks
    /// operation status.</param>
    /// <param name="func">The async function to execute in the
    /// background.</param>
    /// <param name="logger">The logger instance.</param>
    /// <returns>The operation ID for the enqueued operation.</returns>
    public static async Task<string> PerformAsync<TResource>(
        this BackgroundTaskQueue queue,
        IOperationStore operationStore,
        Func<IProgress<string>, Task<TResource>> func,
        ILogger logger)
    {
        var operationId = Guid.NewGuid().ToString();
        var operationStatus = new OperationStatus { OperationId = operationId };
        operationStore.AddOperation(operationStatus);

        // Capture the current Activity so background work inherits
        // the caller's trace context (traceparent from the HTTP
        // request). Without this, logs emitted by the background
        // task have no trace ID in OTLP.
        var parentActivity = Activity.Current;

        await queue.EnqueueAsync(async token =>
        {
            // Restore the parent activity so that ILogger picks up
            // the trace/span IDs for structured log correlation.
            Activity.Current = parentActivity;

            operationStore.UpdateStatus(operationId, op => op.Status = "Running");
            IProgress<string> progressReporter = new Progress<string>(m =>
                operationStore.UpdateStatus(operationId, op =>
                {
                    op.Progress.Add(m);
                }));
            try
            {
                TResource resource = await func(progressReporter);
                if (resource == null)
                {
                    throw new ApiException(
                        HttpStatusCode.NotFound,
                        new ODataError(
                            code: "ResourceNotFound",
                            message: "Specified resource was not found."));
                }

                operationStore.UpdateStatus(operationId, op =>
                {
                    op.Status = "Succeeded";
                    op.Resource = resource;
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Background operation {operationId} failed.");
                operationStore.UpdateStatus(operationId, op =>
                {
                    (var statusCode, var error) = ODataError.FromException(ex);
                    op.Status = "Failed";
                    op.Error = error;
                    op.StatusCode = statusCode;
                });
            }
        });

        return operationId;
    }

    /// <summary>
    /// Enqueue a long-running operation and write the standard
    /// HTTP async response headers (Retry-After, Operation-Location).
    /// </summary>
    /// <typeparam name="TResource">The type of the resource produced
    /// by the background operation.</typeparam>
    /// <param name="queue">The background task queue.</param>
    /// <param name="operationStore">The operation store that tracks
    /// operation status.</param>
    /// <param name="httpContext">The HTTP context for writing response
    /// headers.</param>
    /// <param name="func">The async function to execute in the
    /// background.</param>
    /// <param name="logger">The logger instance.</param>
    /// <returns>A task representing the async operation.</returns>
    public static async Task PerformAsync<TResource>(
        this BackgroundTaskQueue queue,
        IOperationStore operationStore,
        HttpContext httpContext,
        Func<IProgress<string>, Task<TResource>> func,
        ILogger logger)
    {
        var operationId = await queue.PerformAsync(operationStore, func, logger);

        httpContext.Response.Headers.RetryAfter = "5";
        httpContext.Response.Headers["Operation-Location"] = $"/operations/{operationId}";
    }
}
