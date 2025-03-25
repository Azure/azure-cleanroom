// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AttestationClient;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class CAController : ControllerBase
{
    private readonly ILogger<CAController> logger;
    private readonly CcfClientManager ccfClientManager;
    private readonly Routes routes;

    public CAController(
        ILogger<CAController> logger,
        CcfClientManager ccfClientManager,
        Routes routes)
    {
        this.logger = logger;
        this.ccfClientManager = ccfClientManager;
        this.routes = routes;
    }

    internal WebContext WebContext =>
        (WebContext)this.ControllerContext.HttpContext.Items[WebContext.WebContextIdentifer]!;

    [HttpGet("/ca/isEnabled")]
    public async Task<IActionResult> IsCAEnabled()
    {
        var appClient = await this.ccfClientManager.GetAppClient();
        var wsConfig = await this.ccfClientManager.GetWsConfig();
        var content = Attestation.PrepareRequestContent(
            wsConfig.Attestation.Report);

        using (HttpRequestMessage request = new(
            HttpMethod.Get,
            this.routes.IsCAEnabled(this.WebContext)))
        {
            request.Content = new StringContent(
                content.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await appClient.SendAsync(request);
            this.Response.CopyHeaders(response.Headers);
            await response.ValidateStatusCodeAsync(this.logger);
            var jsonResponse = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            this.logger.LogInformation(jsonResponse.ToString());
            return this.Ok(JsonSerializer.Deserialize<JsonObject>(jsonResponse.ToString()));
        }
    }

    [HttpPost("/ca/generateEndorsedCert")]
    public async Task<IActionResult> GenerateEndorsedCert([FromBody] JsonObject data)
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
            this.routes.GenerateEndorsedCert(this.WebContext)))
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
            string serializedJson = Encoding.UTF8.GetString(unwrappedValue);
            return this.Ok(JsonSerializer.Deserialize<JsonObject>(serializedJson));
        }
    }
}
