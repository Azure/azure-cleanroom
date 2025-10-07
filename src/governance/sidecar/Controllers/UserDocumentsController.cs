// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AttestationClient;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class UserDocuments : ControllerBase
{
    private readonly ILogger<UserDocuments> logger;
    private readonly CcfClientManager ccfClientManager;
    private readonly Routes routes;

    public UserDocuments(
        ILogger<UserDocuments> logger,
        CcfClientManager ccfClientManager,
        Routes routes)
    {
        this.logger = logger;
        this.ccfClientManager = ccfClientManager;
        this.routes = routes;
    }

    internal WebContext WebContext =>
        (WebContext)this.ControllerContext.HttpContext.Items[WebContext.WebContextIdentifer]!;

    [HttpPost("/userdocuments/{documentId}")]
    public async Task<IActionResult> GetUserDocument([FromRoute] string documentId)
    {
        var appClient = await this.ccfClientManager.GetAppClient();
        var wsConfig = await this.ccfClientManager.GetWsConfig();
        var content = Attestation.PrepareRequestContent(
            wsConfig.Attestation.PublicKey,
            wsConfig.Attestation.Report);

        using (HttpRequestMessage request = new(
            HttpMethod.Post,
            this.routes.UserDocuments(this.WebContext, documentId)))
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
            string json = Encoding.UTF8.GetString(unwrappedValue);
            var userdocument = JsonSerializer.Deserialize<JsonObject>(json);
            return this.Ok(userdocument);
        }
    }
}
