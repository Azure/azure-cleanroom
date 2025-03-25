// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using CoseUtils;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class DeploymentSpecController : ClientControllerBase
{
    public DeploymentSpecController(
        ILogger<DeploymentSpecController> logger,
        IHttpContextAccessor httpContextAccessor)
        : base(logger, httpContextAccessor)
    {
    }

    [HttpGet("/contracts/{contractId}/deploymentspec")]
    public async Task<JsonObject> GetDeploymentSpec([FromRoute] string contractId)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using HttpResponseMessage response =
            await appClient.GetAsync($"app/contracts/{contractId}/deploymentspec");
        await response.ValidateStatusCodeAsync(this.Logger);
        this.Response.CopyHeaders(response.Headers);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }

    [HttpPost("/contracts/{contractId}/deploymentspec/propose")]
    public async Task<IActionResult> CreateProposal(
        [FromRoute] string contractId,
        [FromBody] JsonObject content)
    {
        var proposalContent = new JsonObject
        {
            ["actions"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "set_deployment_spec",
                        ["args"] = new JsonObject
                        {
                            ["contractId"] = contractId,
                            ["spec"] = new JsonObject
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
