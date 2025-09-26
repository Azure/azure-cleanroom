// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

public static class OperationStoreExtensions
{
    public static IActionResult GetOperationStatus(
        this IOperationStore operationStore,
        string operationId,
        HttpContext httpContext)
    {
        var status = operationStore.GetStatus(operationId);
        if (status != null)
        {
            if (status.Status == "Running")
            {
                httpContext.Response.Headers.RetryAfter = "5";
            }

            httpContext.Response.Headers["Operation-Location"] = $"/operations/{operationId}";
            return new OkObjectResult(status);
        }

        return new NotFoundObjectResult(new ODataError(
            code: "OperationNotFound",
            message: $"No operation with Id {operationId} was found."));
    }
}