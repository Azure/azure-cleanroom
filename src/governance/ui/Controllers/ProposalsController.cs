// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using CgsUI.Models;
using Microsoft.AspNetCore.Mvc;

namespace CgsUI.Controllers;

public class ProposalsController : Controller
{
    private readonly ILogger<ProposalsController> logger;
    private readonly IConfiguration configuration;

    public ProposalsController(
        ILogger<ProposalsController> logger,
        IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
    }

    [Route("Proposals/{proposalId}")]
    public async Task<IActionResult> Detail(string proposalId)
    {
        using var client = new HttpClient();
        string proposalUrl =
            $"{this.configuration.GetEndpoint()}/proposals/{proposalId}";
        var t1 = client.GetFromJsonAsync<JsonObject>(proposalUrl);
        var t2 = client.GetFromJsonAsync<JsonObject>(proposalUrl + "/actions");
        var t3 = client.GetFromJsonAsync<JsonArray>(proposalUrl + "/votes");
        var t4 = client.GetFromJsonAsync<JsonObject>($"{this.configuration.GetEndpoint()}/members");
        var tasks = new List<Task> { t1, t2, t3, t4 };
        await Task.WhenAll(tasks);
        var item = (await t1)!;
        var actionsItem = (await t2)!;
        var votes = (await t3)!;
        var members = (await t4)!;

        bool isOpen = item!["proposalState"]!.ToString() == "Open";
        bool canWithdraw = isOpen && item!["proposerId"]!.ToString() == Common.MemberId;
        bool canVote = true;
        string? disableVoteReason = null;

        var votesModel = new List<ProposalViewModel.VotesViewModel>();
        Dictionary<string, JsonNode?> currentMembers =
            members["value"]!.AsArray()
            .ToDictionary(m => m!["memberId"]!.ToString(), m => m);
        var votedMembers = votes.ToDictionary((v) => v!["memberId"]!.ToString());
        foreach (var member in currentMembers)
        {
            if (member.Value?["memberData"]?["isOperator"]?.ToString() == "true" ||
                member.Value?["memberData"]?["isRecoveryOperator"]?.ToString() == "true")
            {
                continue;
            }

            string voteValue = "not voted";
            if (votedMembers.TryGetValue(member.Key, out var vote))
            {
                if (Common.MemberId == member.Key)
                {
                    canVote = false;
                    disableVoteReason = "You have already voted";
                }

                voteValue = vote!["vote"]!.ToString();
            }

            votesModel.Add(new()
            {
                MemberId = member.Key,
                MemberName = member.Value?["membeData"]?["identifier"]?.ToString() ?? "Not set",
                Vote = voteValue
            });
        }

        votesModel = votesModel.OrderBy(v => v.MemberName).ToList();
        return this.View(new ProposalViewModel
        {
            ProposalId = proposalId,
            IsOpen = isOpen,
            CanWithdraw = canWithdraw,
            CanVote = canVote,
            DisableVoteReason = disableVoteReason,
            Votes = votesModel,
            Proposal = item!.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            Actions = actionsItem!.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
        });
    }

    [Route("Proposals/{proposalId}/Withdraw")]
    public async Task<IActionResult> Withdraw(string proposalId)
    {
        using var client = new HttpClient();
        string proposalUrl =
            $"{this.configuration.GetEndpoint()}/proposals/{proposalId}/withdraw";
        await client.PostAsync(proposalUrl, null);
        return this.RedirectToAction(nameof(this.Detail), new { proposalId });
    }

    [Route("Proposal/{proposalId}/VoteAccept")]
    public async Task<IActionResult> VoteAccept(string proposalId)
    {
        string contractUrl =
            $"{this.configuration.GetEndpoint()}/proposals/{proposalId}/ballots/vote_accept";
        using (HttpRequestMessage request = new(HttpMethod.Post, contractUrl))
        {
            var payload = new JsonObject
            {
                ["proposalId"] = proposalId
            };

            request.Content = new StringContent(
                payload.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using var client = new HttpClient();
            using HttpResponseMessage response = await client.SendAsync(request);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                var error = await response.Content.ReadAsStringAsync();
                return this.View("Error", new ErrorViewModel
                {
                    Content = error
                });
            }

            return this.RedirectToAction(nameof(this.Detail), new { proposalId });
        }
    }

    [Route("Proposal/{proposalId}/VoteReject")]
    public async Task<IActionResult> VoteReject(string proposalId)
    {
        string contractUrl =
            $"{this.configuration.GetEndpoint()}/proposals/{proposalId}/ballots/vote_reject";
        using (HttpRequestMessage request = new(HttpMethod.Post, contractUrl))
        {
            var payload = new JsonObject
            {
                ["proposalId"] = proposalId
            };

            request.Content = new StringContent(
                payload.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using var client = new HttpClient();
            using HttpResponseMessage response = await client.SendAsync(request);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                var error = await response.Content.ReadAsStringAsync();
                return this.View("Error", new ErrorViewModel
                {
                    Content = error
                });
            }

            return this.RedirectToAction(nameof(this.Detail), new { proposalId });
        }
    }
}
