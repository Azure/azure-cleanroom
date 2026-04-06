// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Identity;

namespace TokenCredentials;

internal class TenantTokenCredential : TokenCredential
{
    private const string DefaultSuffix = "/.default";

    private static readonly Lazy<HttpClient> HttpClient = new(GetHttpClient);

    private string tenantId;

    public TenantTokenCredential(string tenantId)
    {
        this.tenantId = tenantId;
    }

    public override async ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken = default)
    {
        return await this.GetTokenImplAsync(requestContext, cancellationToken)
            .ConfigureAwait(false);
    }

    public override AccessToken GetToken(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Use GetTokenAsync() instead.");
    }

    private static HttpClient GetHttpClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
            {
                if (errors == SslPolicyErrors.None)
                {
                    return true;
                }

                if (cert == null || chain == null)
                {
                    return false;
                }

                foreach (X509ChainElement element in chain.ChainElements)
                {
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                }

                chain.ChainPolicy.CustomTrustStore.Clear();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                var result = chain.Build(cert);
                return result;
            }
        };

        var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromMinutes(1);

        return httpClient;
    }

    private static string GetIdentityEndpoint()
    {
        return Environment.GetEnvironmentVariable("TENANT_IDENTITY_ENDPOINT")!;
    }

    private static string ScopesToResource(string[] scopes)
    {
        if (scopes == null)
        {
            throw new ArgumentNullException(nameof(scopes));
        }

        if (scopes.Length != 1)
        {
            throw new ArgumentException("Array must be exactly length 1", nameof(scopes));
        }

        if (!scopes[0].EndsWith(DefaultSuffix, StringComparison.Ordinal))
        {
            return scopes[0];
        }

        return scopes[0].Remove(scopes[0].LastIndexOf(DefaultSuffix, StringComparison.Ordinal));
    }

    private async ValueTask<AccessToken> GetTokenImplAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        try
        {
            string endpoint =
                $"{string.Format(GetIdentityEndpoint(), this.tenantId)}" +
                $"?resource={ScopesToResource(requestContext.Scopes)}";

            HttpResponseMessage response =
                await HttpClient.Value.GetAsync(endpoint, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                Stream responseStream =
                    await response.Content.ReadAsStreamAsync(cancellationToken);
                using JsonDocument jsonDoc =
                    await JsonDocument.ParseAsync(
                        responseStream,
                        cancellationToken: cancellationToken);

                return this.GetTokenFromResponse(jsonDoc.RootElement);
            }
            else
            {
                throw new RequestFailedException(
                    (int)response.StatusCode,
                    "AuthFailure: " + response.ReasonPhrase);
            }
        }
        catch (Exception ex) when (ex is not RequestFailedException)
        {
            throw new RequestFailedException(
                (int)HttpStatusCode.InternalServerError,
                "AuthFailure: " + ex.ToString());
        }
    }

    private AccessToken GetTokenFromResponse(JsonElement root)
    {
        string? accessToken = null;
        DateTimeOffset? expiresOn = null;

        foreach (JsonProperty prop in root.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "access_token":
                    accessToken = prop.Value.GetString();
                    break;

                case "expires_on":
                    if (prop.Value.TryGetInt64(out long expiresOnSec))
                    {
                        expiresOn = DateTimeOffset.FromUnixTimeSeconds(expiresOnSec);
                    }

                    break;
            }
        }

        if (accessToken != null && expiresOn.HasValue)
        {
            return new AccessToken(accessToken, expiresOn.Value);
        }
        else
        {
            throw new AuthenticationFailedException("Invalid response.");
        }
    }
}
