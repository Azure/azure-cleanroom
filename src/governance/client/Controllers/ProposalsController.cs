// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using CoseUtils;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class ProposalsController : ClientControllerBase
{
    public ProposalsController(
        ILogger<ProposalsController> logger,
        IHttpContextAccessor httpContextAccessor)
        : base(logger, httpContextAccessor)
    {
    }

    [HttpGet("/proposals")]
    public async Task<JsonObject> GetProposals()
    {
        var ccfClient = await this.CcfClientManager.GetGovClient();
        using HttpResponseMessage response = await ccfClient.GetAsync(
            $"gov/members/proposals?api-version={this.CcfClientManager.GetGovApiVersion()}");
        await response.ValidateStatusCodeAsync(this.Logger);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }

    [HttpGet("/proposals/{proposalId}")]
    public async Task<JsonObject> GetProposals([FromRoute] string proposalId)
    {
        var ccfClient = await this.CcfClientManager.GetGovClient();
        using HttpResponseMessage response =
            await ccfClient.GetAsync($"gov/members/proposals/{proposalId}" +
            $"?api-version={this.CcfClientManager.GetGovApiVersion()}");
        await response.ValidateStatusCodeAsync(this.Logger);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }

    [HttpGet("/proposals/{proposalId}/votes")]
    public async Task<JsonArray> GetVotes([FromRoute] string proposalId)
    {
        var ccfClient = await this.CcfClientManager.GetGovClient();
        using HttpResponseMessage proposalResponse =
            await ccfClient.GetAsync($"gov/members/proposals/{proposalId}" +
            $"?api-version={this.CcfClientManager.GetGovApiVersion()}");
        await proposalResponse.ValidateStatusCodeAsync(this.Logger);
        var proposalJson = (await proposalResponse.Content.ReadFromJsonAsync<JsonObject>())!;
        var ballotSubmitters = proposalJson["ballotSubmitters"]?.AsArray();
        var votes = new JsonArray();
        if (ballotSubmitters != null)
        {
            string voteYes = "export function vote (proposal, proposerId) { return true }";
            string voteNo = "export function vote (proposal, proposerId) { return false }";
            foreach (var submitter in ballotSubmitters)
            {
                string memberId = submitter!.ToString();
                using HttpResponseMessage ballotResponse =
                    await ccfClient.GetAsync(
                        $"gov/members/proposals/{proposalId}/ballots/{memberId}" +
                        $"?api-version={this.CcfClientManager.GetGovApiVersion()}");
                await ballotResponse.ValidateStatusCodeAsync(this.Logger);
                string script = await ballotResponse.Content.ReadAsStringAsync();
                string? vote = script == voteYes ? "accepted" :
                    script == voteNo ? "rejected" : script;
                votes.Add(new JsonObject { ["memberId"] = memberId, ["vote"] = vote });
            }
        }

        return votes;
    }

    [HttpGet("/proposals/{proposalId}/historical")]
    public async Task<IActionResult> GetProposalsHistorical([FromRoute] string proposalId)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        string? query = this.Request.QueryString.Value;
        JsonObject? proposals = null;
        int maxAttempts = 12;
        for (int attempt = 0; attempt <= maxAttempts; attempt++)
        {
            using HttpResponseMessage response =
                await appClient.GetAsync($"app/proposals/{proposalId}/historical{query}");
            if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                // Intervals: 1s, 5s, 10s, 15s,....
                var delta = TimeSpan.FromSeconds(5);
                var backoff = attempt == 0 ? TimeSpan.FromSeconds(1) :
                    TimeSpan.FromSeconds(delta.TotalSeconds * attempt);
                await Task.Delay(backoff);
                continue;
            }

            await response.ValidateStatusCodeAsync(this.Logger);
            this.Response.CopyHeaders(response.Headers);
            if (response.StatusCode == System.Net.HttpStatusCode.OK)
            {
                proposals = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
                var nextLink = proposals["nextLink"]?.ToString();
                if (!string.IsNullOrEmpty(nextLink))
                {
                    if (!nextLink.StartsWith($"/app/proposals/{proposalId}/historical"))
                    {
                        throw new Exception($"Unexpected nextLink prefix of {nextLink}");
                    }

                    // Remove "/app" prefix from the link so that the URL matches the path
                    // for this method.
                    proposals["nextLink"] = nextLink.Remove(0, "/app".Length);
                }
            }

            break;
        }

        return proposals != null ? this.Ok(proposals) : this.Accepted();
    }

    [HttpGet("/proposals/{proposalId}/actions")]
    public async Task<JsonObject> GetProposalActions([FromRoute] string proposalId)
    {
        var ccfClient = await this.CcfClientManager.GetGovClient();
        using HttpResponseMessage response =
            await ccfClient.GetAsync($"gov/members/proposals/{proposalId}/actions" +
            $"?api-version={this.CcfClientManager.GetGovApiVersion()}");
        await response.ValidateStatusCodeAsync(this.Logger);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }

    [HttpPost("/proposals/{proposalId}/withdraw")]
    public async Task<JsonObject> WithdrawProposal([FromRoute] string proposalId)
    {
        var ccfClient = await this.CcfClientManager.GetGovClient();
        var coseSignKey = this.CcfClientManager.GetCoseSignKey();
        var payload =
            await GovernanceCose.CreateGovCoseSign1Message(
                coseSignKey,
                GovMessageType.Withdrawal,
                null,
                proposalId);
        using HttpRequestMessage request = Cose.CreateHttpRequestMessage(
            $"gov/members/proposals/{proposalId}:withdraw" +
            $"?api-version={this.CcfClientManager.GetGovApiVersion()}",
            payload);
        using HttpResponseMessage response = await ccfClient.SendAsync(request);
        this.Response.CopyHeaders(response.Headers);
        await response.ValidateStatusCodeAsync(this.Logger);
        await response.WaitGovTransactionCommittedAsync(this.Logger, this.CcfClientManager);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }

    [HttpPost("/proposals/create")]
    public async Task<JsonObject> CreateProposal([FromBody] JsonObject content)
    {
        var ccfClient = await this.CcfClientManager.GetGovClient();
        var coseSignKey = this.CcfClientManager.GetCoseSignKey();
        var payload = await GovernanceCose.CreateGovCoseSign1Message(
            coseSignKey,
            GovMessageType.Proposal,
            content.ToJsonString());
        using HttpRequestMessage request = Cose.CreateHttpRequestMessage(
            $"gov/members/proposals:create" +
            $"?api-version={this.CcfClientManager.GetGovApiVersion()}",
            payload);
        using HttpResponseMessage response = await ccfClient.SendAsync(request);
        this.Response.CopyHeaders(response.Headers);
        await response.ValidateStatusCodeAsync(this.Logger);
        await response.WaitGovTransactionCommittedAsync(this.Logger, this.CcfClientManager);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }

    [HttpPost("/proposals/{proposalId}/ballots/vote_accept")]
    public Task<IActionResult> VoteAccept([FromRoute] string proposalId)
    {
        var ballot = new JsonObject
        {
            ["ballot"] = "export function vote (proposal, proposerId) { return true }"
        };

        return this.Vote(proposalId, ballot);
    }

    [HttpPost("/proposals/{proposalId}/ballots/vote_reject")]
    public Task<IActionResult> VoteReject([FromRoute] string proposalId)
    {
        var ballot = new JsonObject
        {
            ["ballot"] = "export function vote (proposal, proposerId) { return false }"
        };

        return this.Vote(proposalId, ballot);
    }

    [HttpPost("/proposals/{proposalId}/ballots/vote")]
    public async Task<IActionResult> VoteBallot(
        [FromRoute] string proposalId,
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
        return await this.Vote(proposalId, ballot);
    }

    private async Task<IActionResult> Vote(
        string proposalId,
        JsonObject ballot)
    {
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
        return this.Ok(jsonResponse);
    }
}
