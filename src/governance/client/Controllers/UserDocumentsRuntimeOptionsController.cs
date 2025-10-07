// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class UserDocumentsRuntimeOptionsController : ClientControllerBase
{
    public UserDocumentsRuntimeOptionsController(
        ILogger<UserDocumentsRuntimeOptionsController> logger,
        IHttpContextAccessor httpContextAccessor)
        : base(logger, httpContextAccessor)
    {
    }

    [HttpPost("/userdocuments/{documentId}/runtimeoptions/{runtimeOption}/enable")]
    public async Task EnableRuntimeOption(
        [FromRoute] string documentId,
        [FromRoute] string runtimeOption)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using HttpResponseMessage response =
            await appClient.PostAsync(
                $"app/userdocuments/{documentId}/runtimeoptions/{runtimeOption}/enable",
                content: null);
        await response.ValidateStatusCodeAsync(this.Logger);
        this.Response.CopyHeaders(response.Headers);
        await response.WaitAppTransactionCommittedAsync(this.Logger, this.CcfClientManager);
    }

    [HttpPost("/userdocuments/{documentId}/runtimeoptions/{runtimeOption}/disable")]
    public async Task DisableExecution(
        [FromRoute] string documentId,
        [FromRoute] string runtimeOption)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using HttpResponseMessage response =
            await appClient.PostAsync(
                $"app/userdocuments/{documentId}/runtimeoptions/{runtimeOption}/disable",
                content: null);
        await response.ValidateStatusCodeAsync(this.Logger);
        this.Response.CopyHeaders(response.Headers);
        await response.WaitAppTransactionCommittedAsync(this.Logger, this.CcfClientManager);
    }

    [HttpPost("/userdocuments/{documentId}/checkstatus/{runtimeOption}")]
    public async Task<JsonObject> RuntimeOptionStatus(
        [FromRoute] string documentId,
        [FromRoute] string runtimeOption)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using HttpResponseMessage response = await appClient.PostAsync(
            $"app/userdocuments/{documentId}/checkstatus/{runtimeOption}",
            content: null);
        await response.ValidateStatusCodeAsync(this.Logger);
        this.Response.CopyHeaders(response.Headers);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }
}
