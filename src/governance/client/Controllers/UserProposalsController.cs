// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class UserProposalsController : UserProposalControllerBase
{
    public UserProposalsController(
        ILogger<UserProposalsController> logger,
        IHttpContextAccessor httpContextAccessor)
        : base(logger, httpContextAccessor)
    {
    }

    [HttpPost("/users/proposals/create")]
    public async Task<IActionResult> CreateUserProposal([FromBody] JsonObject content)
    {
        return await this.SubmitUserProposal(content);
    }

    [HttpGet("/users/proposals/{proposalId}")]
    public async Task<IActionResult> GetUserProposal([FromRoute] string proposalId)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using (HttpRequestMessage request = new(HttpMethod.Get, $"app/users/proposals/{proposalId}"))
        {
            using HttpResponseMessage response = await appClient.SendAsync(request);
            this.Response.CopyHeaders(response.Headers);
            await response.ValidateStatusCodeAsync(this.Logger);

            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
            return this.Ok(jsonResponse!);
        }
    }

    [HttpGet("/users/proposals/{proposalId}/status")]
    public async Task<IActionResult> GetUserProposalStatus([FromRoute] string proposalId)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using (HttpRequestMessage request =
            new(HttpMethod.Get, $"app/users/proposals/{proposalId}/status"))
        {
            using HttpResponseMessage response = await appClient.SendAsync(request);
            this.Response.CopyHeaders(response.Headers);
            await response.ValidateStatusCodeAsync(this.Logger);

            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
            return this.Ok(jsonResponse!);
        }
    }

    [HttpPost("/users/proposals/{proposalId}/withdraw")]
    public async Task<IActionResult> WithdrawUserProposal([FromRoute] string proposalId)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"app/users/proposals/{proposalId}/withdraw"))
        {
            using HttpResponseMessage response = await appClient.SendAsync(request);
            this.Response.CopyHeaders(response.Headers);
            await response.ValidateStatusCodeAsync(this.Logger);
            await response.WaitAppTransactionCommittedAsync(this.Logger, this.CcfClientManager);

            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
            return this.Ok(jsonResponse!);
        }
    }

    [HttpPost("/users/proposals/{proposalId}/ballots/vote_accept")]
    public Task<IActionResult> VoteAccept([FromRoute] string proposalId)
    {
        var ballot = new JsonObject
        {
            ["ballot"] = "accepted"
        };

        return this.SubmitVote(proposalId, ballot);
    }

    [HttpPost("/users/proposals/{proposalId}/ballots/vote_reject")]
    public Task<IActionResult> VoteReject([FromRoute] string proposalId)
    {
        var ballot = new JsonObject
        {
            ["ballot"] = "rejected"
        };

        return this.SubmitVote(proposalId, ballot);
    }
}
