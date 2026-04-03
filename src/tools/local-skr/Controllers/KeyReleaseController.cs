// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HttpRetries;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Polly;

namespace Controllers;

[ApiController]
public class KeyReleaseController : ControllerBase
{
    private static IAsyncPolicy retryPolicy =
        Policy.Handle<Exception>((e) => RetryUtilities.IsRetryableException(e))
        .WaitAndRetryAsync(
            5,
            retryAttempt =>
            {
                Random jitterer = new();
                return TimeSpan.FromSeconds(10) + TimeSpan.FromSeconds(jitterer.Next(0, 20));
            },
            (exception, timeSpan, retryCount, context) =>
            {
                ILogger logger = (ILogger)context["logger"];
                logger.LogWarning(
                    $"Hit retryable exception while performing operation: " +
                    $"{context.OperationKey}. Retrying after " +
                    $"{timeSpan}. RetryCount: {retryCount}. Exception: {exception}.");
            });

    private readonly ILogger<KeyReleaseController> logger;
    private readonly ClientManager clientManager;
    private readonly Dictionary<string, object> retryContextData;

    public KeyReleaseController(
        ILogger<KeyReleaseController> logger,
        ClientManager clientManager)
    {
        this.logger = logger;
        this.clientManager = clientManager;
        this.retryContextData = new Dictionary<string, object>
        {
            {
                "logger",
                this.logger
            }
        };
    }

    internal WebContext WebContext =>
        (WebContext)this.ControllerContext.HttpContext.Items[WebContext.WebContextIdentifer]!;

    [HttpPost("/key/release")]
    public async Task<IActionResult> KeyRelease([FromBody] KeyReleaseRequest content)
    {
        var appClient = await this.clientManager.GetAppClient();
        var wsConfig = await this.clientManager.GetWsConfig();
        string maaEndpoint = content.MaaEndpoint;
        if (!maaEndpoint.StartsWith("http"))
        {
            maaEndpoint = "https://" + maaEndpoint;
        }

        maaEndpoint = maaEndpoint.TrimEnd('/');
        string maaToken = await retryPolicy.ExecuteAsync(
            async (ctx) =>
            {
                using (HttpRequestMessage request = new(
                    HttpMethod.Post,
                    $"{maaEndpoint}/attest/SevSnpVm?api-version=2022-08-01"))
                {
                    request.Content = new StringContent(
                        wsConfig.MaaRequest.ToJsonString(),
                        Encoding.UTF8,
                        "application/json");
                    using HttpResponseMessage response = await appClient.SendAsync(request);
                    await response.ValidateStatusCodeAsync(this.logger);
                    var jsonResponse = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
                    return jsonResponse["token"]!.ToString();
                }
            },
            new Context("attest/SevSnpVm", this.retryContextData));

        string accessToken = content.AccessToken;
        string akvEndpoint = content.AkvEndpoint;
        string kid = content.Kid;

        if (!akvEndpoint.StartsWith("http"))
        {
            akvEndpoint = "https://" + akvEndpoint;
        }

        akvEndpoint = akvEndpoint.TrimEnd('/');

        JsonObject jsonResponse = await retryPolicy.ExecuteAsync(
            async (ctx) =>
            {
                using (HttpRequestMessage request = new(
                    HttpMethod.Post,
                    $"{akvEndpoint}/keys/{kid}/release?api-version=7.3"))
                {
                    var body = new JsonObject
                    {
                        ["target"] = maaToken
                    };

                    request.Content = new StringContent(
                        body.ToJsonString(),
                        Encoding.UTF8,
                        "application/json");
                    request.Headers.Add("Authorization", $"Bearer {accessToken}");
                    using HttpResponseMessage response = await appClient.SendAsync(request);
                    await response.ValidateStatusCodeAsync(this.logger);
                    return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
                }
            },
            new Context($"keys/kid/release", this.retryContextData));

        // https://thomasvanlaere.com/posts/2022/12/azure-confidential-computing-secure-key-release/
        var value = jsonResponse["value"]!.ToString();

        // Ideally we should verify the key vault certificate chain mentioned in the header
        // and verify the signature on the payload to ensure we trust the response. But this
        // is an insecure sample and it goes straight ahead and consumes the payload.
        var base64payload = value.Split(".")[1];
        var payload = Base64UrlEncoder.Decode(base64payload);
        var payloadJson =
            JsonSerializer.Deserialize<JsonObject>(payload)!;
        var alg = payloadJson["request"]!["enc"]!.ToString();
        this.logger.LogInformation(
            JsonSerializer.Serialize(
                payloadJson,
                new JsonSerializerOptions { WriteIndented = true }));
        var kty = payloadJson["response"]!["key"]!["key"]!["kty"]!.ToString();
        var keyHsmBase64 = payloadJson["response"]!["key"]!["key"]!["key_hsm"]!.ToString();
        var keyHsm = Base64UrlEncoder.Decode(keyHsmBase64);
        var keyHsmJson =
            JsonSerializer.Deserialize<JsonObject>(keyHsm)!;
        var base64CipherText = keyHsmJson["ciphertext"]!.ToString();
        var wrappedKey = Base64UrlEncoder.DecodeBytes(base64CipherText);
        byte[] unwrappedKey = wrappedKey.UnwrapRsaOaepAesKwpValue(wsConfig.PrivateKey, alg);
        JsonWebKey jwk;
        if (kty == "RSA")
        {
            using var k = RSA.Create();
            k.ImportPkcs8PrivateKey(unwrappedKey, out int _);
            var rsasecuritykey = new RsaSecurityKey(k.ExportParameters(true))
            {
                KeyId = kid
            };

            jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(rsasecuritykey);
        }
        else if (kty == "EC")
        {
            using var k = ECDsa.Create();
            k.ImportPkcs8PrivateKey(unwrappedKey, out int _);
            var ecdsaSecurityKey = new ECDsaSecurityKey(k)
            {
                KeyId = kid
            };

            jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(ecdsaSecurityKey);
        }
        else
        {
            throw new NotSupportedException($"Unhandled kty: {kty}. Fix this.");
        }

        return this.Ok(new JsonObject
        {
            ["key"] = JsonSerializer.Serialize(jwk)
        });
    }

    public class KeyReleaseRequest
    {
        [Required]
        [JsonPropertyName("maa_endpoint")]
        public string MaaEndpoint { get; set; } = default!;

        [Required]
        [JsonPropertyName("akv_endpoint")]
        public string AkvEndpoint { get; set; } = default!;

        [Required]
        [JsonPropertyName("kid")]
        public string Kid { get; set; } = default!;

        [Required]
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = default!;
    }
}
