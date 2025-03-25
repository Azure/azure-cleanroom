// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Controllers;

public class ApiExceptionFilter : IExceptionFilter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private ILogger logger;
    private IConfiguration configuration;

    public ApiExceptionFilter(
        ILogger logger,
        IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
    }

    public void OnException(ExceptionContext context)
    {
        (var statusCode, var error) = ODataError.FromException(context.Exception);
        context.Result = new ObjectResult(error);

        this.logger.LogError(
            context.Exception,
            $"Returning error: {JsonSerializer.Serialize(error, Options)}");

        context.HttpContext.Response.StatusCode = statusCode;
    }
}