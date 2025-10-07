// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using AttestationClient;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class ConsentCheckController : ControllerBase
{
    private readonly ILogger<ConsentCheckController> logger;
    private readonly CcfClientManager ccfClientManager;
    private readonly Routes routes;

    public ConsentCheckController(
        ILogger<ConsentCheckController> logger,
        CcfClientManager ccfClientManager,
        Routes routes)
    {
        this.logger = logger;
        this.ccfClientManager = ccfClientManager;
        this.routes = routes;
    }

    internal WebContext WebContext =>
        (WebContext)this.ControllerContext.HttpContext.Items[WebContext.WebContextIdentifer]!;

    [HttpPost("/consentcheck/execution")]
    public async Task<JsonObject> ExecutionConsentCheck()
    {
        return await this.ConsentCheck(this.routes.ConsentCheckExecution(this.WebContext));
    }

    [HttpPost("/consentcheck/logging")]
    public async Task<JsonObject> LoggingConsentCheck()
    {
        return await this.ConsentCheck(this.routes.ConsentCheckLogging(this.WebContext));
    }

    [HttpPost("/consentcheck/telemetry")]
    public async Task<JsonObject> TelemetryConsentCheck()
    {
        return await this.ConsentCheck(this.routes.ConsentCheckTelemetry(this.WebContext));
    }

    [HttpPost("/userdocuments/{documentId}/consentcheck/execution")]
    public async Task<JsonObject> UserDocumentExecutionConsentCheck(
        [FromRoute] string documentId)
    {
        return await this.ConsentCheck(this.routes.ConsentCheckUserDocumentExecution(
            documentId,
            this.WebContext));
    }

    [HttpPost("/userdocuments/{documentId}/consentcheck/telemetry")]
    public async Task<JsonObject> UserDocumentTelemetryConsentCheck(
        [FromRoute] string documentId)
    {
        return await this.ConsentCheck(this.routes.ConsentCheckUserDocumentTelemetry(
            documentId,
            this.WebContext));
    }

    private async Task<JsonObject> ConsentCheck(string url)
    {
        var appClient = await this.ccfClientManager.GetAppClient();
        var wsConfig = await this.ccfClientManager.GetWsConfig();
        var content = Attestation.PrepareRequestContent(wsConfig.Attestation.Report);

        using (HttpRequestMessage request = new(HttpMethod.Post, url))
        {
            request.Content = new StringContent(
                content.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await appClient.SendAsync(request);
            this.Response.CopyHeaders(response.Headers);
            await response.ValidateStatusCodeAsync(this.logger);
            var jsonResponse = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            return jsonResponse!;
        }
    }
}
