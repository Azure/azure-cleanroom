// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Http.Headers;
using Azure.Core;

namespace Controllers;

public class TokenCredentialDelegatingHandler : DelegatingHandler
{
    private CcfTokenCredential tokenCredential;
    private string scope;

    public TokenCredentialDelegatingHandler(CcfTokenCredential tokenCredential, string scope)
    {
        this.tokenCredential = tokenCredential;
        this.scope = scope;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!CustomAuth(request))
        {
            var ctx = new TokenRequestContext(new string[] { this.scope });
            string token = await this.tokenCredential.GetTokenAsync(
                ctx,
                cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken);

        static bool CustomAuth(HttpRequestMessage request)
        {
            return request.Options.TryGetValue(
                new HttpRequestOptionsKey<bool>("UsingCustomAuthorizationValue"),
                out var value) &&
                value;
        }
    }
}
