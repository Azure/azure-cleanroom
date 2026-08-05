// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Claims;
using AttestationClient;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace FrontendSvc.Auth;

/// <summary>
/// Cryptographically validates inbound Microsoft Entra bearer JWTs before their
/// claims are trusted as caller identity. It performs the minimum needed to stop
/// identity spoofing - proving the token is genuinely Entra-signed (valid
/// signature, asymmetric algorithm, not expired). Issuer/tenant trust and
/// membership are enforced downstream by CCF/CGS (trusted issuers) and the
/// membership manager.
/// </summary>
internal class EntraTokenValidator
{
    // Entra "common" OIDC metadata endpoint. Its JWKS validates signatures for any
    // Microsoft Entra tenant (work or personal), for both v1 and v2 tokens, which is
    // all the frontend needs: it only proves the token was genuinely issued by Entra.
    private const string MetadataEndpoint =
        "https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration";

    // Entra signs tokens with RS256. Restricting the accepted algorithms (together
    // with RequireSignedTokens) rejects "alg=none" and symmetric-key forgeries.
    private static readonly string[] AllowedAlgorithms = [SecurityAlgorithms.RsaSha256];

    private readonly ILogger logger;
    private readonly bool authRequired;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? openIdConfigManager;

    public EntraTokenValidator(ILogger logger, IHostEnvironment hostEnvironment)
    {
        this.logger = logger;

        // Enforce validation only in confidential (SNP/CACI) production. It is skipped
        // in the insecure-virtual mode (no real Entra to validate against) and in the
        // Development environment, where integration/BVT tests use fake token issuers.
        this.authRequired = Attestation.IsSnpCACI() && !hostEnvironment.IsDevelopment();

        if (this.authRequired)
        {
            // ConfigurationManager caches and periodically refreshes the JWKS.
            this.openIdConfigManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                MetadataEndpoint,
                new OpenIdConnectConfigurationRetriever());
        }
        else
        {
            this.logger.LogWarning(
                "Inbound bearer-token signature validation is DISABLED (insecure-virtual " +
                "or Development environment). This must only be used in dev/test environments.");
        }
    }

    /// <summary>
    /// Validates the supplied bearer token and returns the resulting principal.
    /// </summary>
    /// <param name="token">The raw bearer token.</param>
    /// <returns>The validated claims principal.</returns>
    /// <exception cref="SecurityTokenValidationException">
    /// Thrown when the token fails validation.
    /// </exception>
    public async Task<ClaimsPrincipal> ValidateAsync(string token)
    {
        if (!this.authRequired)
        {
            // Dev/test only: no Entra authority to validate against. Parse the token
            // so downstream claim extraction keeps working, without crypto validation.
            var devToken = new JsonWebTokenHandler().ReadJsonWebToken(token);
            return new ClaimsPrincipal(
                new ClaimsIdentity(devToken.Claims, AuthConstants.BearerScheme));
        }

        OpenIdConnectConfiguration oidcConfig =
            await this.openIdConfigManager!.GetConfigurationAsync();

        var validationParameters = new TokenValidationParameters
        {
            // Core anti-forgery checks: the token must carry a valid signature from
            // Microsoft Entra and use an asymmetric algorithm. Together these reject
            // "alg=none" and symmetric-key forgeries. This is the minimum needed to
            // stop identity spoofing, because a caller cannot sign as Entra.
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = oidcConfig.SigningKeys,
            RequireSignedTokens = true,
            ValidAlgorithms = AllowedAlgorithms,

            // Reject expired tokens.
            ValidateLifetime = true,
            RequireExpirationTime = true,

            // Issuer/tenant trust and audience are delegated to CCF/CGS (trusted
            // issuers) and the membership manager, so the frontend accepts any
            // genuinely Entra-signed token (v1 or v2, work or personal).
            ValidateIssuer = false,
            ValidateAudience = false,
        };

        TokenValidationResult result =
            await new JsonWebTokenHandler().ValidateTokenAsync(token, validationParameters);

        if (!result.IsValid)
        {
            throw new SecurityTokenValidationException(
                "Bearer token validation failed.",
                result.Exception);
        }

        return new ClaimsPrincipal(result.ClaimsIdentity);
    }
}
