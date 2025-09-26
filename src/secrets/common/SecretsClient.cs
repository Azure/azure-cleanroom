// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace CcrSecrets;

public class SecretsClient
{
    private static HttpClient httpClient = default!;
    private readonly IConfiguration config;
    private readonly ILogger logger;

    public SecretsClient(ILogger logger, IConfiguration config)
    {
        this.logger = logger;
        this.config = config;
        httpClient ??= new(new PolicyHttpMessageHandler(
            HttpRetries.Policies.DefaultRetryPolicy(logger))
        {
            InnerHandler = new HttpClientHandler()
        });
    }

    public async Task<byte[]> UnwrapSecret(UnwrapSecretRequest unwrapRequest)
    {
        this.logger.LogInformation($"Unwrapping secret for request {unwrapRequest}.");

        // Get the Kek.
        string accessToken;
        if (!string.IsNullOrEmpty(unwrapRequest.Kek.AccessToken))
        {
            accessToken = unwrapRequest.Kek.AccessToken;
        }
        else
        {
            string scope = unwrapRequest.Kek.AkvEndpoint.ToLower().Contains("vault.azure.net") ?
                "https://vault.azure.net/.default" : "https://managedhsm.azure.net/.default";
            accessToken = await this.FetchAccessToken(
                scope,
                unwrapRequest.ClientId,
                unwrapRequest.TenantId);
        }

        string kek = await this.ReleaseKey(unwrapRequest.Kek, accessToken);

        // Get the wrapped secret.
        if (!string.IsNullOrEmpty(unwrapRequest.AccessToken))
        {
            accessToken = unwrapRequest.AccessToken;
        }
        else
        {
            string scope = "https://vault.azure.net/.default";
            accessToken = await this.FetchAccessToken(
                scope,
                unwrapRequest.ClientId,
                unwrapRequest.TenantId);
        }

        string b64EncodedWrappedSecret =
            await this.GetKeyVaultSecret(unwrapRequest.Kid, unwrapRequest.AkvEndpoint, accessToken);

        // Unwrap the secret using the kek.
        var jsonWebKey = JsonSerializer.Deserialize<JsonWebKey>(kek)!;
        var rsaParameters = new RSAParameters
        {
            Modulus = Base64UrlEncoder.DecodeBytes(jsonWebKey.N),
            Exponent = Base64UrlEncoder.DecodeBytes(jsonWebKey.E),
            D = Base64UrlEncoder.DecodeBytes(jsonWebKey.D),
            DP = Base64UrlEncoder.DecodeBytes(jsonWebKey.DP),
            DQ = Base64UrlEncoder.DecodeBytes(jsonWebKey.DQ),
            P = Base64UrlEncoder.DecodeBytes(jsonWebKey.P),
            Q = Base64UrlEncoder.DecodeBytes(jsonWebKey.Q),
            InverseQ = Base64UrlEncoder.DecodeBytes(jsonWebKey.QI)
        };

        using var rsaKey = RSA.Create(rsaParameters);
        byte[] cipherText = Convert.FromBase64String(b64EncodedWrappedSecret);
        byte[] plainText = rsaKey.Decrypt(cipherText, RSAEncryptionPadding.OaepSHA256);
        return plainText;
    }

    private async Task<string> FetchAccessToken(string scope, string clientId, string tenantId)
    {
        string version = "2018-02-01";
        string queryParams =
            $"?scope={scope}" +
            $"&tenantId={tenantId}" +
            $"&clientId={clientId}" +
            $"&apiVersion={version}";
        string? port = this.config[SettingName.IdentityPort];
        if (string.IsNullOrEmpty(port))
        {
            throw new ApiException(
                System.Net.HttpStatusCode.BadRequest,
                new ODataError(
                    code: "IdentityPortNotConfigured",
                    message: $"{SettingName.IdentityPort} env variable is not set."));
        }

        string uri = $"http://localhost:{port}/metadata/identity/oauth2/token" +
            queryParams;
        this.logger.LogInformation($"Fetching access token from {uri}.");
        HttpResponseMessage response = await httpClient.GetAsync(uri);
        await response.ValidateStatusCodeAsync(this.logger);
        var identityToken = await response.Content.ReadFromJsonAsync<JsonObject>();
        return identityToken!["token"]!.ToString();
    }

    private async Task<string> ReleaseKey(KekInfo kekInfo, string accessToken)
    {
        var maaEndpoint = GetHost(kekInfo.MaaEndpoint);
        var akvEndpoint = GetHost(kekInfo.AkvEndpoint);

        string? port = this.config[SettingName.SkrPort];
        if (string.IsNullOrEmpty(port))
        {
            throw new ApiException(
                System.Net.HttpStatusCode.BadRequest,
                new ODataError(
                    code: "SkrPortNotConfigured",
                    message: $"{SettingName.SkrPort} env variable is not set."));
        }

        string uri = $"http://localhost:{port}/key/release";
        var skrRequest = new JsonObject
        {
            ["maa_endpoint"] = maaEndpoint,
            ["akv_endpoint"] = akvEndpoint,
            ["kid"] = kekInfo.Kid,
            ["access_token"] = accessToken
        };

        this.logger.LogInformation($"Releasing key '{kekInfo.Kid}'.");
        HttpResponseMessage response = await httpClient.PostAsJsonAsync(uri, skrRequest);
        await response.ValidateStatusCodeAsync(this.logger);
        var skrResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return skrResponse!["key"]!.ToString();

        static string GetHost(string s)
        {
            if (!s.StartsWith("http"))
            {
                s = "https://" + s;
            }

            return new Uri(s).Host;
        }
    }

    private async Task<string> GetKeyVaultSecret(
        string kid,
        string akvEndpoint,
        string accessToken)
    {
        string uri = $"{akvEndpoint.TrimEnd('/')}/secrets/{kid}?api-version=7.4";
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.Add("Authorization", $"Bearer {accessToken}");
        using HttpResponseMessage response = await httpClient.SendAsync(request);
        await response.ValidateStatusCodeAsync(this.logger);
        var jsonResponse = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        return jsonResponse["value"]!.ToString();
    }
}
