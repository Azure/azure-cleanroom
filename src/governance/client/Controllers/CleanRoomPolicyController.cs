// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using CoseUtils;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class CleanRoomPolicyController : ClientControllerBase
{
    public CleanRoomPolicyController(
        ILogger<CleanRoomPolicyController> logger,
        IHttpContextAccessor httpContextAccessor)
        : base(logger, httpContextAccessor)
    {
    }

    [HttpGet("/contracts/{contractId}/cleanroompolicy")]
    public async Task<JsonObject> GetPolicy([FromRoute] string contractId)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using HttpResponseMessage response =
            await appClient.GetAsync($"app/contracts/{contractId}/cleanroompolicy");
        await response.ValidateStatusCodeAsync(this.Logger);
        this.Response.CopyHeaders(response.Headers);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }

    [HttpPost("/contracts/{contractId}/cleanroompolicy/propose")]
    public async Task<IActionResult> CreateProposal(
        [FromRoute] string contractId,
        [FromBody] JsonObject content)
    {
        string? type = content["type"]?.ToString();
        if (type == null)
        {
            return this.BadRequest(new ODataError(
                code: "TypeMissing",
                message: "Musty specify the type value as 'add' or 'remove'."));
        }

        JsonObject? claims = content["claims"]?.AsObject();
        if (claims == null)
        {
            return this.BadRequest(new ODataError(
                code: "ClaimsMissing",
                message: "Musty specify the claims to add/remove."));
        }

        var proposalContent = new JsonObject
        {
            ["actions"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "set_clean_room_policy",
                        ["args"] = new JsonObject
                        {
                            ["contractId"] = contractId,
                            ["type"] = type,
                            ["claims"] = JsonNode.Parse(claims.ToJsonString())
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
