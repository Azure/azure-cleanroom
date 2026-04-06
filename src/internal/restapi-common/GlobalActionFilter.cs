// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Controllers;

internal class GlobalActionFilter(
    ILogger logger,
    IConfiguration configuration) : IActionFilter
{
#pragma warning disable IDE0052 // Remove unread private members
    private readonly ILogger logger = logger;
    private readonly IConfiguration configuration = configuration;

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }

#pragma warning restore IDE0052 // Remove unread private members

    public void OnActionExecuting(ActionExecutingContext actionContext)
    {
        OpenTelemetryUtilities.SetLoggingContext(actionContext);
    }
}