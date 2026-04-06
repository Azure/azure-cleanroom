// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Controllers;

/// <summary>
/// Validates that incoming requests include a supported api-version query parameter.
/// Actions or controllers decorated with [SkipApiVersionValidation] are excluded.
/// </summary>
public class ApiVersionValidationFilter : IActionFilter
{
    private const string ApiVersionParam = "api-version";

    private readonly HashSet<string> supportedVersions;

    public ApiVersionValidationFilter(IEnumerable<string> supportedVersions)
    {
        this.supportedVersions = new HashSet<string>(
            supportedVersions,
            StringComparer.OrdinalIgnoreCase);
    }

    public void OnActionExecuting(ActionExecutingContext context)
    {
        bool skip = context.ActionDescriptor.EndpointMetadata
            .Any(m => m is SkipApiVersionValidationAttribute);
        if (skip)
        {
            return;
        }

        string? apiVersion = context.HttpContext.Request.Query[ApiVersionParam]
            .FirstOrDefault();

        if (string.IsNullOrEmpty(apiVersion))
        {
            context.Result = new BadRequestObjectResult(
                new ODataError(
                    "ApiVersionRequired",
                    $"The '{ApiVersionParam}' query parameter is required."));
            return;
        }

        if (!this.supportedVersions.Contains(apiVersion))
        {
            string supported = string.Join(", ", this.supportedVersions);
            context.Result = new BadRequestObjectResult(
                new ODataError(
                    "UnsupportedApiVersion",
                    $"The api-version '{apiVersion}' is not supported. " +
                    $"Supported versions: {supported}."));
        }
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
