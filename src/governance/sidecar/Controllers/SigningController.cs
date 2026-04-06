// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using AttestationClient;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class SigningController : ControllerBase
{
    private readonly ILogger<SigningController> logger;
    private readonly CcfClientManager ccfClientManager;
    private readonly Routes routes;

    public SigningController(
        ILogger<SigningController> logger,
        CcfClientManager ccfClientManager,
        Routes routes)
    {
        this.logger = logger;
        this.ccfClientManager = ccfClientManager;
        this.routes = routes;
    }

    internal WebContext WebContext =>
        (WebContext)this.ControllerContext.HttpContext.Items[WebContext.WebContextIdentifer]!;

    [HttpPost("/signing/sign")]
    public async Task<IActionResult> SignPayload([FromBody] JsonObject body)
    {
        var appClient = await this.ccfClientManager.GetAppClient();
        var wsConfig = await this.ccfClientManager.GetWsConfig();
        var content = Attestation.PrepareRequestContent(
            wsConfig.KeyPair.PublicKey,
            wsConfig.Report);

        // Add the payload to sign from the request body.
        if (body.TryGetPropertyValue("payload", out var payload))
        {
            content["payload"] = payload!.DeepClone();
        }

        using (HttpRequestMessage request = new(
            HttpMethod.Post,
            this.routes.Sign(this.WebContext)))
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
                wsConfig.KeyPair.PrivateKey);
            string signature = Encoding.UTF8.GetString(unwrappedValue);
            return this.Ok(new JsonObject
            {
                ["value"] = signature
            });
        }
    }
}
