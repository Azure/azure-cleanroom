﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using AttestationClient;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class OAuthController : ControllerBase
{
    private readonly ILogger<OAuthController> logger;
    private readonly CcfClientManager ccfClientManager;
    private readonly Routes routes;

    public OAuthController(
        ILogger<OAuthController> logger,
        CcfClientManager ccfClientManager,
        Routes routes)
    {
        this.logger = logger;
        this.ccfClientManager = ccfClientManager;
        this.routes = routes;
    }

    internal WebContext WebContext =>
        (WebContext)this.ControllerContext.HttpContext.Items[WebContext.WebContextIdentifer]!;

    [HttpPost("/oauth/token")]
    public async Task<IActionResult> GetToken(
    [FromQuery] string sub,
    [FromQuery] string tenantId,
    [FromQuery] string aud)
    {
        var appClient = await this.ccfClientManager.GetAppClient();
        var wsConfig = await this.ccfClientManager.GetWsConfig();
        var content = Attestation.PrepareRequestContent(
            wsConfig.Attestation.PublicKey,
            wsConfig.Attestation.Report);

        var fromEpoc = DateTimeOffset.Now.ToUnixTimeSeconds();
        var toEpoc = DateTimeOffset.Now.AddMinutes(5).ToUnixTimeSeconds();
        var jti = Guid.NewGuid().ToString();
        string query =
            $"?&nbf={fromEpoc}" +
            $"&exp={toEpoc}" +
            $"&iat={fromEpoc}" +
            $"&jti={jti}" +
            $"&sub={sub}" +
            $"&tid={tenantId}" +
            $"&aud={aud}";

        using (HttpRequestMessage request = new(
            HttpMethod.Post,
            this.routes.OAuthToken(this.WebContext) + query))
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
            string token = Encoding.UTF8.GetString(unwrappedValue);
            return this.Ok(new JsonObject
            {
                ["value"] = token
            });
        }
    }
}
