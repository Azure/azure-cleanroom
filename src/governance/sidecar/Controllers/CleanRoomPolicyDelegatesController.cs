// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AttestationClient;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class CleanRoomPolicyDelegatesController : ControllerBase
{
    private readonly ILogger<CleanRoomPolicyDelegatesController> logger;
    private readonly CcfClientManager ccfClientManager;
    private readonly Routes routes;

    public CleanRoomPolicyDelegatesController(
        ILogger<CleanRoomPolicyDelegatesController> logger,
        CcfClientManager ccfClientManager,
        Routes routes)
    {
        this.logger = logger;
        this.ccfClientManager = ccfClientManager;
        this.routes = routes;
    }

    internal WebContext WebContext =>
        (WebContext)this.ControllerContext.HttpContext.Items[WebContext.WebContextIdentifer]!;

    [HttpPost("/cleanroompolicy/delegates/{delegateType}/{delegateId}")]
    public async Task<IActionResult> SetCleanRoomDelegatePolicy(
        [FromRoute] string delegateType,
        [FromRoute] string delegateId,
        [FromBody] JsonObject data)
    {
        var appClient = await this.ccfClientManager.GetAppClient();
        var wsConfig = await this.ccfClientManager.GetWsConfig();
        var paddingMode = RSASignaturePaddingMode.Pss;

        var dataBytes = Encoding.UTF8.GetBytes(data.ToJsonString());
        var signature = Signing.SignData(dataBytes, wsConfig.KeyPair.PrivateKey, paddingMode);

        var content = Attestation.PrepareSignedDataRequestContent(
            dataBytes,
            signature,
            wsConfig.KeyPair.PublicKey,
            wsConfig.Report);

        using (HttpRequestMessage request = new(
            HttpMethod.Put,
            this.routes.DelegateCleanRoomPolicy(this.WebContext, delegateType, delegateId)))
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
