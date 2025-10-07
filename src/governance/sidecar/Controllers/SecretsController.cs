// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AttestationClient;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class SecretsController : ControllerBase
{
    private readonly ILogger<SecretsController> logger;
    private readonly CcfClientManager ccfClientManager;
    private readonly Routes routes;

    public SecretsController(
        ILogger<SecretsController> logger,
        CcfClientManager ccfClientManager,
        Routes routes)
    {
        this.logger = logger;
        this.ccfClientManager = ccfClientManager;
        this.routes = routes;
    }

    internal WebContext WebContext =>
        (WebContext)this.ControllerContext.HttpContext.Items[WebContext.WebContextIdentifer]!;

    [HttpPost("/secrets/{secretId}")]
    public async Task<IActionResult> GetSecret([FromRoute] string secretId)
    {
        var appClient = await this.ccfClientManager.GetAppClient();
        var wsConfig = await this.ccfClientManager.GetWsConfig();
        var content = Attestation.PrepareRequestContent(
            wsConfig.Attestation.PublicKey,
            wsConfig.Attestation.Report);

        using (HttpRequestMessage request = new(
            HttpMethod.Post,
            this.routes.Secrets(this.WebContext, secretId)))
        {
            request.Content = new StringContent(
                content.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await appClient.SendAsync(request);
            this.Response.CopyHeaders(response.Headers);
            await response.ValidateStatusCodeAsync(this.logger);
            var jsonResponse = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            string base64WrappedValue = jsonResponse["value"]!.ToString();
            byte[] wrappedValue = Convert.FromBase64String(base64WrappedValue);
            byte[] unwrappedValue = wrappedValue.UnwrapRsaOaepAesKwpValue(
                wsConfig.Attestation.PrivateKey);
            string secret = Encoding.UTF8.GetString(unwrappedValue);
            return this.Ok(new JsonObject
            {
                ["value"] = secret
            });
        }
    }

    [HttpPut("/secrets/{secretName}")]
    public async Task<JsonObject> PutSecret(
        [FromRoute] string secretName,
        [FromBody] JsonObject data)
    {
        var appClient = await this.ccfClientManager.GetAppClient();
        var wsConfig = await this.ccfClientManager.GetWsConfig();
        var paddingMode = RSASignaturePaddingMode.Pss;

        var dataBytes = Encoding.UTF8.GetBytes(data.ToJsonString());
        var signature = Signing.SignData(dataBytes, wsConfig.Attestation.PrivateKey, paddingMode);

        var content = Attestation.PrepareSignedDataRequestContent(
            dataBytes,
            signature,
            wsConfig.Attestation.PublicKey,
            wsConfig.Attestation.Report);

        using (HttpRequestMessage request = new(
            HttpMethod.Put,
            this.routes.Secrets(this.WebContext, secretName)))
        {
            request.Content = new StringContent(
                content.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await appClient.SendAsync(request);
            this.Response.CopyHeaders(response.Headers);
            await response.ValidateStatusCodeAsync(this.logger);
            await response.WaitAppTransactionCommittedAsync(this.ccfClientManager);
            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
            return jsonResponse!;
        }
    }

    [HttpPost("/secrets/{secretId}/cleanroompolicy")]
    public async Task<IActionResult> SetSecretCleanRoomPolicy(
            [FromRoute] string secretId,
            [FromBody] JsonObject data)
    {
        var appClient = await this.ccfClientManager.GetAppClient();
        var wsConfig = await this.ccfClientManager.GetWsConfig();
        var paddingMode = RSASignaturePaddingMode.Pss;

        var dataBytes = Encoding.UTF8.GetBytes(data.ToJsonString());
        var signature = Signing.SignData(dataBytes, wsConfig.Attestation.PrivateKey, paddingMode);

        var content = Attestation.PrepareSignedDataRequestContent(
            dataBytes,
            signature,
            wsConfig.Attestation.PublicKey,
            wsConfig.Attestation.Report);

        using (HttpRequestMessage request = new(
            HttpMethod.Post,
            this.routes.SecretCleanRoomPolicy(this.WebContext, secretId)))
        {
            request.Content = new StringContent(
                content.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await appClient.SendAsync(request);
            this.Response.CopyHeaders(response.Headers);
            await response.ValidateStatusCodeAsync(this.logger);
            return this.Ok();
        }
    }
}
