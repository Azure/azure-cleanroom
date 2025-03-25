// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Polly;

namespace Controllers;

[ApiController]
public class SecretsController : ControllerBase
{
    private static HttpClient httpClient = new();
    private readonly IConfiguration config;
    private readonly ILogger<SecretsController> logger;
    private readonly Dictionary<string, object> retryContextData;

    public SecretsController(
        IConfiguration config,
        ILogger<SecretsController> logger)
    {
        this.config = config;
        this.logger = logger;
        this.retryContextData = new Dictionary<string, object>
        {
            {
                "logger",
                this.logger
            }
        };
    }

    [HttpPost("/secrets/unwrap")]
    public async Task<IActionResult> UnwrapSecret([FromBody] UnwrapSecretRequest unwrapRequest)
    {
        if (string.IsNullOrEmpty(this.config[SettingName.IdentityPort]))
        {
            return this.BadRequest(new ODataError(
                code: "IdentityPortNotSet",
                message: "IDENTITY_PORT environment variable not set."));
        }

        if (string.IsNullOrEmpty(this.config[SettingName.SkrPort]))
        {
            return this.BadRequest(new ODataError(
                code: "SkrPortNotSet",
                message: "SKR_PORT environment variable not set."));
        }

        this.logger.LogInformation($"Unwrapping secret for request "
            + $"{JsonSerializer.Serialize(unwrapRequest)}.");

        // Get the Kek.
        string scope = unwrapRequest.Kek.AkvEndpoint.ToLower().Contains("vault.azure.net") ?
            "https://vault.azure.net/.default" : "https://managedhsm.azure.net/.default";
        string accessToken =
            await this.FetchAccessToken(scope, unwrapRequest.ClientId, unwrapRequest.TenantId);
        string kek = await this.ReleaseKey(unwrapRequest.Kek, accessToken);

        // Get the wrapped secret.
        scope = "https://vault.azure.net/.default";
        accessToken =
            await this.FetchAccessToken(scope, unwrapRequest.ClientId, unwrapRequest.TenantId);
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
        return this.Ok(new JsonObject
        {
            ["value"] = Convert.ToBase64String(plainText)
        });
    }

    private async Task<string> FetchAccessToken(string scope, string clientId, string tenantId)
    {
        string version = "2018-02-01";
        string queryParams =
            $"?scope={scope}" +
            $"&tenantId={tenantId}" +
            $"&clientId={clientId}" +
            $"&apiVersion={version}";

        string uri = $"http://localhost:{this.config[SettingName.IdentityPort]}" +
            $"/metadata/identity/oauth2/token" +
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

        string uri = $"http://localhost:{this.config[SettingName.SkrPort]}" +
            $"/key/release";
        var skrRequest = new JsonObject
        {
            ["maa_endpoint"] = maaEndpoint,
            ["akv_endpoint"] = akvEndpoint,
            ["kid"] = kekInfo.Kid,
            ["access_token"] = accessToken
        };

        this.logger.LogInformation($"Releasing key from {uri}.");
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
        JsonObject? jsonResponse = await HttpRetries.Policies.DefaultRetryPolicy.ExecuteAsync(
            async (ctx) =>
            {
                using (HttpRequestMessage request = new(HttpMethod.Get, uri))
                {
                    request.Headers.Add("Authorization", $"Bearer {accessToken}");
                    using HttpResponseMessage response = await httpClient.SendAsync(request);
                    await response.ValidateStatusCodeAsync(this.logger);
                    var jsonResponse = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
                    return jsonResponse;
                }
            },
            new Context("oauth/token", this.retryContextData));

        return jsonResponse["value"]!.ToString();
    }
}
