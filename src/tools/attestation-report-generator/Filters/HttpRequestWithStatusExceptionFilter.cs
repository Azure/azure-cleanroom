// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Rest.TransientFaultHandling;

namespace Controllers;

public class HttpRequestWithStatusExceptionFilter : IActionFilter, IOrderedFilter
{
    public int Order => int.MaxValue - 10;

    public void OnActionExecuting(ActionExecutingContext context)
    {
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
        if (context.Exception is HttpRequestWithStatusException httpResponseException)
        {
            context.Result = new ObjectResult(httpResponseException.Message)
            {
                StatusCode = (int)httpResponseException.StatusCode
            };

            context.ExceptionHandled = true;
        }
    }
}