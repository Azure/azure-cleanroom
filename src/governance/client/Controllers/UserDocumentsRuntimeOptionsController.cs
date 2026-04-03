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

        // TODO (phanic): Temporary workaround to avoid breaking callers that would now need to pass
        // in the contract ID.
        using HttpResponseMessage fetchResponse =
            await appClient.GetAsync($"app/userdocuments/{documentId}");
        await fetchResponse.ValidateStatusCodeAsync(this.Logger);
        var jsonResponse = await fetchResponse.Content.ReadFromJsonAsync<JsonObject>();
        string contractId = jsonResponse!["contractId"]!.GetValue<string>();

        using HttpResponseMessage response = await appClient.PostAsync(
            $"app/contracts/{contractId}/userdocuments/{documentId}/" +
            $"runtimeoptions/{runtimeOption}/enable",
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

        // TODO (phanic): Temporary workaround to avoid breaking callers that would now need to pass
        // in the contract ID.
        using HttpResponseMessage fetchResponse =
            await appClient.GetAsync($"app/userdocuments/{documentId}");
        await fetchResponse.ValidateStatusCodeAsync(this.Logger);
        var jsonResponse = await fetchResponse.Content.ReadFromJsonAsync<JsonObject>();
        string contractId = jsonResponse!["contractId"]!.GetValue<string>();

        using HttpResponseMessage response = await appClient.PostAsync(
            $"app/contracts/{contractId}/userdocuments/{documentId}/" +
            $"runtimeoptions/{runtimeOption}/disable",
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

        // TODO (phanic): Temporary workaround to avoid breaking callers that would now need to pass
        // in the contract ID.
        using HttpResponseMessage fetchResponse =
            await appClient.GetAsync($"app/userdocuments/{documentId}");
        await fetchResponse.ValidateStatusCodeAsync(this.Logger);
        var jsonResponse = await fetchResponse.Content.ReadFromJsonAsync<JsonObject>();
        string contractId = jsonResponse!["contractId"]!.GetValue<string>();

        using HttpResponseMessage response = await appClient.PostAsync(
            $"app/contracts/{contractId}/userdocuments/{documentId}/" +
            $"runtimeoptions/{runtimeOption}/status",
            content: null);
        await response.ValidateStatusCodeAsync(this.Logger);
        this.Response.CopyHeaders(response.Headers);
        jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }
}
