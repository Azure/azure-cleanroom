// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Mvc.Filters;

namespace Controllers;

internal class GlobalActionFilter : IActionFilter
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;

    public GlobalActionFilter(
        ILogger logger,
        IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
    }

    public void OnActionExecuting(ActionExecutingContext actionContext)
    {
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}