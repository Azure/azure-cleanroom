// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FrontendSvc.Models;

namespace FrontendSvc.CcfClient;

/// <summary>
/// Client for direct communication with CCF endpoints that don't require authentication.
/// </summary>
public static class CcfClient
{
    /// <summary>
    /// Gets the OIDC signing keys (JWKS) directly from CCF.
    /// This endpoint is unauthenticated in CCF.
    /// </summary>
    /// <param name="ccfClient">The HttpClient configured for CCF communication.</param>
    /// <param name="logger">Logger for logging information and errors.</param>
    /// <returns>The OIDC keys response containing the JWKS.</returns>
    public static async Task<OidcKeysResponse> GetOidcKeysAsync(
        this HttpClient ccfClient,
        ILogger logger)
    {
        return await ccfClient.HttpGetAsync<OidcKeysResponse>("/app/oidc/keys", logger);
    }
}
