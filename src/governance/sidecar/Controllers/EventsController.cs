// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AttestationClient;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class EventsController : ControllerBase
{
    private readonly ILogger<EventsController> logger;
    private readonly CcfClientManager ccfClientManager;
    private readonly Routes routes;

    public EventsController(
        ILogger<EventsController> logger,
        CcfClientManager ccfClientManager,
        Routes routes)
    {
        this.logger = logger;
        this.ccfClientManager = ccfClientManager;
        this.routes = routes;
    }

    internal WebContext WebContext =>
        (WebContext)this.ControllerContext.HttpContext.Items[WebContext.WebContextIdentifer]!;

    [HttpPut("/events")]
    public async Task<IActionResult> PutEvent(
    [FromQuery] string? id,
    [FromQuery] string? scope,
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

        string? query = this.Request.QueryString.Value;
        using (HttpRequestMessage request = new(
            HttpMethod.Put,
            this.routes.Events(this.WebContext) + query))
        {
            request.Content = new StringContent(
                content.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await appClient.SendAsync(request);
            this.Response.CopyHeaders(response.Headers);
            await response.ValidateStatusCodeAsync(this.logger);
            await response.WaitAppTransactionCommittedAsync(this.ccfClientManager);
        }

        return this.Ok();
    }
}
