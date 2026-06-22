// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Controllers;

public class HttpRequestWithStatusExceptionFilter : IActionFilter, IOrderedFilter
{
    public int Order => int.MaxValue - 10;

    public void OnActionExecuting(ActionExecutingContext context)
    {
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
        if (context.Exception is Azure.RequestFailedException rfe)
        {
            object body;
            int statusCode;

            if (!string.IsNullOrEmpty(rfe.ErrorCode))
            {
                // ARM SDK error with structured error code (e.g. quota,
                // capacity). Convert to ODataError so the RP can parse
                // the code and message.
                (statusCode, var error) = ODataError.FromException(rfe);
                body = error;
            }
            else
            {
                // If the message is valid JSON (e.g. an ODataError from
                // an upstream service like CCF), pass through the
                // structured object so it gets serialized correctly.
                // Otherwise return the raw message string.
                statusCode = rfe.Status;
                try
                {
                    body = JsonSerializer.Deserialize<JsonElement>(
                        rfe.Message);
                }
                catch (JsonException)
                {
                    body = rfe.Message;
                }
            }

            context.Result = new ObjectResult(body)
            {
                StatusCode = statusCode
            };

            context.ExceptionHandled = true;
        }
    }
}