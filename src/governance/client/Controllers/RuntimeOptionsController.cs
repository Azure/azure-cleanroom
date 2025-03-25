// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using CoseUtils;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class RuntimeOptionsController : ClientControllerBase
{
    public RuntimeOptionsController(
        ILogger<RuntimeOptionsController> logger,
        IHttpContextAccessor httpContextAccessor)
        : base(logger, httpContextAccessor)
    {
    }

    [HttpPost("/runtimeoptions/{option}/propose-{action}")]
    public async Task<IActionResult> ProposeRuntimeOption(
        [FromRoute] string option,
        [FromRoute] string action)
    {
        if (action.ToLower() != "enable" && action.ToLower() != "disable")
        {
            return this.BadRequest(new ODataError(
                code: "InvalidActionValue",
                message: "Valid values are propose-enable or propose-disable."));
        }

        return await this.Propose(option, action);
    }

    [HttpPost("/runtimeoptions/checkstatus/{option}")]
    public async Task<JsonObject> ExecutionStatus(
        [FromRoute] string option)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using HttpResponseMessage response = await appClient.PostAsync(
            $"app/runtimeoptions/checkstatus/{option}",
            content: null);
        await response.ValidateStatusCodeAsync(this.Logger);
        this.Response.CopyHeaders(response.Headers);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }

    private async Task<IActionResult> Propose(
        string optionName,
        string action)
    {
        string value = action.ToLower() == "enable" ? "enabled" : "disabled";
        var proposalContent = new JsonObject
        {
            ["actions"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "set_cgs_runtime_options",
                        ["args"] = new JsonObject
                        {
                            ["option"] = optionName,
                            ["status"] = value
                        }
                    }
                }
        };

        var ccfClient = await this.CcfClientManager.GetGovClient();
        var coseSignKey = this.CcfClientManager.GetCoseSignKey();
        var payload =
            await GovernanceCose.CreateGovCoseSign1Message(
                coseSignKey,
                GovMessageType.Proposal,
                proposalContent.ToJsonString());
        using HttpRequestMessage request = Cose.CreateHttpRequestMessage(
            $"gov/members/proposals:create" +
            $"?api-version={this.CcfClientManager.GetGovApiVersion()}",
            payload);
        using HttpResponseMessage response = await ccfClient.SendAsync(request);
        this.Response.CopyHeaders(response.Headers);
        await response.ValidateStatusCodeAsync(this.Logger);
        await response.WaitGovTransactionCommittedAsync(this.Logger, this.CcfClientManager);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return this.Ok(jsonResponse!);
    }
}
