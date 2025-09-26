// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class OAuthController : ClientControllerBase
{
    public OAuthController(
        ILogger<OAuthController> logger,
        IHttpContextAccessor httpContextAccessor)
        : base(logger, httpContextAccessor)
    {
    }

    [HttpGet("/contracts/{contractId}/oauth/federation/subjects/{subjectName}/cleanroompolicy")]
    public async Task<JsonObject> GetTokenSubjectCleanRoomPolicy(
        [FromRoute] string contractId,
        [FromRoute] string subjectName)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using (HttpRequestMessage request = new(
            HttpMethod.Get,
            $"app/contracts/{contractId}/oauth/federation/subjects/{subjectName}/cleanroompolicy"))
        {
            using HttpResponseMessage response = await appClient.SendAsync(request);
            this.Response.CopyHeaders(response.Headers);
            await response.ValidateStatusCodeAsync(this.Logger);
            await response.WaitAppTransactionCommittedAsync(this.Logger, this.CcfClientManager);
            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
            return jsonResponse!;
        }
    }
}
