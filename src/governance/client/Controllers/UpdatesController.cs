// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class UpdatesController : ClientControllerBase
{
    public UpdatesController(
        ILogger<UpdatesController> logger,
        IHttpContextAccessor httpContextAccessor)
        : base(logger, httpContextAccessor)
    {
    }

    [HttpGet("/checkUpdates")]
    public async Task<CheckUpdateResponse> CheckUpdates()
    {
        var ccfClient = this.CcfClientManager.GetNoAuthClient();
        using HttpResponseMessage response =
            await ccfClient.GetAsync($"gov/members/proposals" +
            $"?api-version={this.CcfClientManager.GetGovApiVersion()}");
        await response.ValidateStatusCodeAsync(this.Logger);
        var proposals = await response.Content.ReadFromJsonAsync<ListProposalResponse>();
        List<ListProposalResponse.Proposal> possibleUpdates = new();
        if (proposals?.Value != null && proposals.Value.Any())
        {
            possibleUpdates = proposals.Value.Where(p => p.ProposalState == "Open").ToList();
        }

        var result = new CheckUpdateResponse { Proposals = new() };
        foreach (var proposal in possibleUpdates)
        {
            using HttpResponseMessage response2 =
                await ccfClient.GetAsync($"gov/members/proposals/{proposal.ProposalId}/actions" +
                $"?api-version={this.CcfClientManager.GetGovApiVersion()}");
            await response2.ValidateStatusCodeAsync(this.Logger);
            var actions = await response2.Content.ReadFromJsonAsync<ActionsResponse>();
            if (actions?.Actions != null)
            {
                var updateAction = actions.Actions.Find(a =>
                    a.Name == "set_constitution" ||
                    a.Name == "set_js_app" ||
                    a.Name == "add_snp_host_data" ||
                    a.Name == "remove_snp_host_data");
                if (updateAction != null)
                {
                    result.Proposals.Add(new()
                    {
                        ProposalId = proposal.ProposalId,
                        ActionName = updateAction.Name
                    });
                }
            }
        }

        return result;
    }

    public class CheckUpdateResponse
    {
        public List<PendingProposal> Proposals { get; set; } = default!;

        public class PendingProposal
        {
            public string ProposalId { get; set; } = default!;

            public string ActionName { get; set; } = default!;
        }
    }

    internal class ListProposalResponse
    {
        [JsonPropertyName("value")]
        public List<Proposal>? Value { get; set; }

        public class Proposal
        {
            [JsonPropertyName("proposalState")]
            public string ProposalState { get; set; } = default!;

            [JsonPropertyName("proposalId")]
            public string ProposalId { get; set; } = default!;
        }
    }

    internal class ActionsResponse
    {
        [JsonPropertyName("actions")]
        public List<ActionItem>? Actions { get; set; }

        public class ActionItem
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = default!;
        }
    }
}
