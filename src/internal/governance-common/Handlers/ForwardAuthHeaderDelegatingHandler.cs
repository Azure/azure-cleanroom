// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Controllers;

public class ForwardAuthHeaderDelegatingHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor hhttpContextAccessor;

    public ForwardAuthHeaderDelegatingHandler(IHttpContextAccessor httpContextAccessor)
    {
        this.hhttpContextAccessor = httpContextAccessor ??
            throw new ArgumentNullException(nameof(httpContextAccessor));
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var ctx = this.hhttpContextAccessor.HttpContext;
        if (ctx?.Request?.Headers?.TryGetValue("Authorization", out var auth) == true)
        {
            request.Headers.TryAddWithoutValidation("Authorization", auth.ToString());
        }

        return base.SendAsync(request, cancellationToken);
    }
}