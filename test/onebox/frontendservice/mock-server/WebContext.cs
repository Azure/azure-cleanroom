// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Mvc.Filters;

namespace MockSvc;

public class WebContext
{
    public WebContext(ActionExecutingContext actionContext)
        : this(actionContext.HttpContext)
    {
    }

    public WebContext(HttpContext httpContext)
    {
    }

    public static string WebContextIdentifer => "WebContext";
}
