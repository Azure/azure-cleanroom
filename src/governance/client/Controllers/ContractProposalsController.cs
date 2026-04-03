// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using CoseUtils;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class ContractProposalsController : ClientControllerBase
{
    public ContractProposalsController(
        ILogger<ContractProposalsController> logger,
        IHttpContextAccessor httpContextAccessor)
        : base(logger, httpContextAccessor)
    {
    }

    [HttpGet("/contracts/{contractId}/{proposalType}")]
    public async Task<JsonObject> GetProposal(
        [FromRoute] string contractId,
        [FromRoute] string proposalType)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using HttpResponseMessage response =
            await appClient.PostAsync($"app/contracts/{contractId}/{proposalType}", content: null);
        await response.ValidateStatusCodeAsync(this.Logger);
        this.Response.CopyHeaders(response.Headers);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }

    [HttpPost("/contracts/{contractId}/{proposalType}/propose")]
    public async Task<IActionResult> CreateProposal(
        [FromRoute] string contractId,
        [FromRoute] string proposalType,
        [FromBody] JsonObject content)
    {
        if (proposalType != "deploymentspec" && proposalType != "deploymentinfo")
        {
            throw new Exception(
                $"Unexpected proposalType of {proposalType}. Only 'deploymentspec' and " +
                "'deploymentinfo' proposalType are supported.");
        }

        var proposalName = proposalType == "deploymentspec"
            ? "set_deployment_spec"
            : "set_deployment_info";
        var propertyName = proposalType == "deploymentspec"
            ? "spec"
            : "info";
        var proposalContent = new JsonObject
        {
            ["actions"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = proposalName,
                        ["args"] = new JsonObject
                        {
                            ["contractId"] = contractId,
                            [propertyName] = new JsonObject
                            {
                                ["data"] = content
                            }
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
