// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Tokens;
using SystemNetHttpHeaders = System.Net.Http.Headers;

namespace CcfConsortiumMgr.Auth;

internal class JwtAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly AuthConfigHandler authConfigHandler;
    private readonly ILogger logger;

    public JwtAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder,
        AuthConfigHandler authConfigHandler,
        ILogger logger)
        : base(options, loggerFactory, encoder)
    {
        this.authConfigHandler = authConfigHandler;
        this.logger = logger;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        try
        {
            return await this.HandleAuthenticateInternal();
        }
        catch (Exception ex)
        {
            this.logger.LogError(
                ex,
                $"HandleAuthenticateInternal failed for {this.Request.Path}.");
            return AuthenticateResult.Fail(ex);
        }
    }

    private async Task<AuthenticateResult> HandleAuthenticateInternal()
    {
        if (this.authConfigHandler.IsNoAuthMode)
        {
            this.logger.LogInformation(
                "Skipping auth validations as consortium manager running in no-auth mode.");
            return this.GetNoAuthModeAuthenticateResult();
        }

        var authHeaderToken = this.GetAuthHeaderToken();
        var jwtToken = new JwtSecurityToken(authHeaderToken);
        var authConfig = this.GetAuthConfig(jwtToken);

        var validationParams =
            new TokenValidationParameters()
            {
                ValidateAudience = true,
                ValidAudience = authConfig.Audience,
                ValidateIssuer = true,
                ValidIssuers = authConfig.ValidIssuers,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = await authConfig.GetSigningKeys()
            };
        TokenValidationResult result =
            await new JwtSecurityTokenHandler().ValidateTokenAsync(
                authHeaderToken,
                validationParams);

        if (result.IsValid)
        {
            var ticket = new AuthenticationTicket(
                new ClaimsPrincipal(result.ClaimsIdentity),
                AuthConstants.BearerScheme);
            return AuthenticateResult.Success(ticket);
        }
        else
        {
            return AuthenticateResult.Fail(result.Exception);
        }
    }

    private string GetAuthHeaderToken()
    {
        if (!this.Request.Headers.TryGetValue(
            AuthConstants.Authorization,
            out StringValues authHeaderValues))
        {
            throw new Exception("Authorization header is missing.");
        }

        if (!SystemNetHttpHeaders.AuthenticationHeaderValue.TryParse(
            authHeaderValues,
            out SystemNetHttpHeaders.AuthenticationHeaderValue? authHeader))
        {
            throw new Exception("Authentication header is invalid.");
        }

        if (!authHeader.Scheme.Equals(
            AuthConstants.BearerScheme,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception($"Authentication header scheme: {authHeader.Scheme} is not bearer.");
        }

        if (authHeader.Parameter == null)
        {
            throw new Exception("Authentication header parameter is null.");
        }

        return authHeader.Parameter;
    }

    private AuthConfig GetAuthConfig(JwtSecurityToken jwtToken)
    {
        string tenantId =
            jwtToken.Claims.Single(t => t.Type == AuthConstants.TenantId).Value;
        string objectId =
            jwtToken.Claims.Single(t => t.Type == AuthConstants.ObjectId).Value;

        AuthConfig? authConfig = this.authConfigHandler.GetAuthConfig(tenantId, objectId);
        if (authConfig == null)
        {
            throw new Exception($"Invalid tid/oid: ({tenantId}/{objectId}).");
        }

        return authConfig;
    }

    private AuthenticateResult GetNoAuthModeAuthenticateResult()
    {
        List<Claim> claims = [new(AuthConstants.BearerScheme, AuthConstants.BearerScheme)];
        ClaimsIdentity claimsIdentity = new(claims, AuthConstants.BearerScheme);

        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(claimsIdentity),
            AuthConstants.BearerScheme);
        return AuthenticateResult.Success(ticket);
    }
}
