// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Primitives;

namespace FrontendSvc;

public class WebContext
{
    public const string ApiVersionParam = "api-version";

    public WebContext(ActionExecutingContext actionContext)
        : this(actionContext.HttpContext)
    {
    }

    public WebContext(HttpContext httpContext)
    {
        this.ApiVersion = this.GetApiVersion(httpContext.Request);
    }

    public static string WebContextIdentifer => "WebContext";

    public string? ApiVersion { get; }

    private string? GetApiVersion(HttpRequest request)
    {
        request.Query.TryGetValue(ApiVersionParam, out StringValues apiVersion);
        return apiVersion.FirstOrDefault();
    }
}
