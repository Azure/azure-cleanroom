// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using CoseUtils;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class ContractsController : ClientControllerBase
{
    private const string VersionKey = "version";
    private const string ProposalIdKey = "proposalId";
    private const string StateKey = "state";

    public ContractsController(
        ILogger<ContractsController> logger,
        IHttpContextAccessor httpContextAccessor)
        : base(logger, httpContextAccessor)
    {
    }

    [HttpGet("/contracts/{contractId}")]
    public async Task<JsonObject> GetContract([FromRoute] string contractId)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using HttpResponseMessage response =
            await appClient.GetAsync($"app/contracts/{contractId}");
        await response.ValidateStatusCodeAsync(this.Logger);
        this.Response.CopyHeaders(response.Headers);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }

    [HttpGet("/contracts")]
    public async Task<JsonObject> ListContracts([FromQuery] string? state = null)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        string path = "app/contracts";
        if (!string.IsNullOrEmpty(state))
        {
            path += $"?state={state}";
        }

        using HttpResponseMessage response = await appClient.GetAsync(path);
        this.Response.CopyHeaders(response.Headers);
        await response.ValidateStatusCodeAsync(this.Logger);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }

    [HttpPut("/contracts/{contractId}")]
    public async Task PutContract([FromRoute] string contractId, [FromBody] JsonObject content)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using (HttpRequestMessage request = new(HttpMethod.Put, $"app/contracts/{contractId}"))
        {
            request.Content = new StringContent(
                content.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await appClient.SendAsync(request);
            this.Response.CopyHeaders(response.Headers);
            await response.ValidateStatusCodeAsync(this.Logger);
            await response.WaitAppTransactionCommittedAsync(this.Logger, this.CcfClientManager);
        }
    }

    [HttpPost("/contracts/{contractId}/propose")]
    public async Task<IActionResult> CreateProposal(
        [FromRoute] string contractId,
        [FromBody] JsonObject content)
    {
        var incomingVersion = content[VersionKey];
        if (incomingVersion == null)
        {
            return this.BadRequest(new ODataError(
                code: "VersionMissing",
                message: "Musty specify a Version value."));
        }

        JsonObject currentContract;
        var appClient = this.CcfClientManager.GetAppClient();
        using (HttpResponseMessage gr =
            await appClient.GetAsync($"app/contracts/{contractId}"))
        {
            await gr.ValidateStatusCodeAsync(this.Logger);
            currentContract = (await gr.Content.ReadFromJsonAsync<JsonObject>())!;
        }

        var currentVersion = currentContract[VersionKey];
        if (currentVersion?.ToString() != incomingVersion.ToString())
        {
            return this.BadRequest(new ODataError(
                code: "ContractModified",
                message: "The version value specified in the input does not match the current" +
                    " version value."));
        }

        var proposalContent = new JsonObject
        {
            ["actions"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "set_contract",
                        ["args"] = new JsonObject
                        {
                            ["contractId"] = contractId,
                            ["contract"] = currentContract
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

    [HttpPost("/contracts/{contractId}/vote_accept")]
    public Task<IActionResult> VoteAccept(
        [FromRoute] string contractId,
        [FromBody] JsonObject content)
    {
        var ballot = new JsonObject
        {
            ["ballot"] = "export function vote (proposal, proposerId) { return true }"
        };
        return this.Vote(contractId, content, ballot);
    }

    [HttpPost("/contracts/{contractId}/vote_reject")]
    public Task<IActionResult> VoteReject(
        [FromRoute] string contractId,
        [FromBody] JsonObject content)
    {
        var ballot = new JsonObject
        {
            ["ballot"] = "export function vote (proposal, proposerId) { return false }"
        };
        return this.Vote(contractId, content, ballot);
    }

    [HttpPost("/contracts/{contractId}/vote")]
    public async Task<IActionResult> Vote(
        [FromRoute] string contractId,
        [FromBody] JsonObject content)
    {
        var ballotNode = content["ballot"];
        if (ballotNode == null)
        {
            return this.BadRequest(new ODataError(
                code: "BallotKeyMissing",
                message: "Musty specify the ballot to vote with."));
        }

        JsonObject ballot = ballotNode.AsObject();
        return await this.Vote(contractId, content, ballot);
    }

    private async Task<IActionResult> Vote(
        string contractId,
        JsonObject content,
        JsonObject ballot)
    {
        var proposalId = content[ProposalIdKey];
        if (proposalId == null)
        {
            return this.BadRequest(new ODataError(
                code: "ProposalIdMissing",
                message: "Musty specify the proposalId to vote on."));
        }

        JsonObject currentContract;
        var appClient = this.CcfClientManager.GetAppClient();
        using (HttpResponseMessage gr =
            await appClient.GetAsync($"app/contracts/{contractId}"))
        {
            await gr.ValidateStatusCodeAsync(this.Logger);
            currentContract = (await gr.Content.ReadFromJsonAsync<JsonObject>())!;
        }

        var currentProposalId = currentContract[ProposalIdKey];
        if (currentProposalId == null || string.IsNullOrEmpty(currentProposalId.ToString()))
        {
            return this.BadRequest(new ODataError(
                code: "ContractNotProposed",
                message: $"Cannot vote on the contract as it is currently not proposed. " +
                    $"Contract state is: {currentContract[StateKey]?.ToString()}"));
        }

        if (currentProposalId.ToString() != proposalId.ToString())
        {
            return this.BadRequest(new ODataError(
                code: "ProposalIdMismatch",
                message: $"The proposalId '{proposalId}' specified in the input " +
                    $"does not match the current proposalId '{currentProposalId}' value."));
        }

        var ccfClient = await this.CcfClientManager.GetGovClient();
        var coseSignKey = this.CcfClientManager.GetCoseSignKey();
        var payload = await GovernanceCose.CreateGovCoseSign1Message(
            coseSignKey,
            GovMessageType.Ballot,
            ballot.ToJsonString(),
            proposalId.ToString());
        using HttpRequestMessage request = Cose.CreateHttpRequestMessage(
            $"gov/members/proposals/{proposalId}/ballots/" +
            $"{this.CcfClientManager.GetMemberId()}:submit" +
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
