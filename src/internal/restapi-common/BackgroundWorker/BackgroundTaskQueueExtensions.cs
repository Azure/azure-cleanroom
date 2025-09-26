// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using Microsoft.AspNetCore.Http;

namespace Controllers;

public static class BackgroundTaskQueueExtensions
{
    public static async Task PerformAsync<TResource>(
        this BackgroundTaskQueue queue,
        IOperationStore operationStore,
        HttpContext httpContext,
        Func<IProgress<string>, Task<TResource>> func)
    {
        var operationId = Guid.NewGuid().ToString();
        var operationStatus = new OperationStatus { OperationId = operationId };
        operationStore.AddOperation(operationStatus);
        await queue.EnqueueAsync(async token =>
        {
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
                operationStore.UpdateStatus(operationId, op =>
                {
                    (var statusCode, var error) = ODataError.FromException(ex);
                    op.Status = "Failed";
                    op.Error = error;
                    op.StatusCode = statusCode;
                });
            }
        });

        httpContext.Response.Headers.RetryAfter = "5";
        httpContext.Response.Headers["Operation-Location"] = $"/operations/{operationId}";
    }
}
