// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FrontendSvc.Auth;

/// <summary>
/// Authentication handler that validates Microsoft Entra bearer tokens using
/// <see cref="EntraTokenValidator"/> before any controller logic runs.
/// </summary>
internal class EntraJwtAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly EntraTokenValidator tokenValidator;
    private readonly ILogger logger;

    public EntraJwtAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder,
        EntraTokenValidator tokenValidator,
        ILogger logger)
        : base(options, loggerFactory, encoder)
    {
        this.tokenValidator = tokenValidator;
        this.logger = logger;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!this.Request.Headers.TryGetValue(
            AuthConstants.AuthorizationHeader,
            out var authHeaderValues))
        {
            return AuthenticateResult.NoResult();
        }

        if (!AuthenticationHeaderValue.TryParse(authHeaderValues, out var authHeader) ||
            !AuthConstants.BearerScheme.Equals(
                authHeader.Scheme,
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(authHeader.Parameter))
        {
            return AuthenticateResult.Fail("Invalid Authorization header.");
        }

        try
        {
            var principal = await this.tokenValidator.ValidateAsync(authHeader.Parameter);
            var ticket = new AuthenticationTicket(principal, this.Scheme.Name);
            return AuthenticateResult.Success(ticket);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Bearer token validation failed.");
            return AuthenticateResult.Fail(ex);
        }
    }
}
