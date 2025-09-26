// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Azure.Core;

namespace Controllers;

internal class LocalIdpCachedTokenCredential : CcfTokenCredential
{
    private readonly string localIdpEndpoint;

    public LocalIdpCachedTokenCredential(string localIdpEndpoint)
    {
        this.localIdpEndpoint = localIdpEndpoint;
    }

    public override async ValueTask<string> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(this.localIdpEndpoint))
        {
            throw new ArgumentException("Local IDP endpoint must be provided.");
        }

        using HttpClient client = new();
        using (HttpRequestMessage request = new(
            HttpMethod.Post,
            this.localIdpEndpoint))
        {
            var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();

            if (jsonResponse == null)
            {
                throw new Exception("Unauthorized: No response received from local IDP.");
            }

            if (!jsonResponse.ContainsKey("accessToken"))
            {
                throw new Exception("Unauthorized: No access token found in response.");
            }

            return jsonResponse["accessToken"]!.ToString();
        }
    }
}