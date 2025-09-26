// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

public abstract class UserProposalControllerBase : ClientControllerBase
{
    public UserProposalControllerBase(
        ILogger logger,
        IHttpContextAccessor httpContextAccessor)
        : base(logger, httpContextAccessor)
    {
    }

    protected async Task<IActionResult> SubmitUserProposal(JsonObject content)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        if (content["approvers"] == null || content["approvers"]?.AsArray()?.Count == 0)
        {
            // If no approvers as specified then default to setting all active members as the
            // approvers.
            var ccfClient = this.CcfClientManager.GetNoAuthClient();
            using HttpResponseMessage response = await ccfClient.GetAsync(
                $"gov/service/members?api-version={this.CcfClientManager.GetGovApiVersion()}");
            await response.ValidateStatusCodeAsync(this.Logger);
            var members = (await response.Content.ReadFromJsonAsync<MemberList>())!;
            var activeMembers = members.Value.Where(m =>
                m.Status == "Active" &&
                (m.MemberData == null || m.MemberData.IsNormalMember())).Select(m => m.MemberId);
            if (!activeMembers.Any())
            {
                throw new Exception(
                    "Atleast one regular active member is needed for setting approvers.");
            }

            var approvers = new JsonArray();
            foreach (var item in activeMembers)
            {
                approvers.Add(
                    new JsonObject { ["approverId"] = item, ["approverIdType"] = "member" });
            }

            content["approvers"] = approvers;
        }

        var proposalId = Guid.NewGuid().ToString("N");
        using (HttpRequestMessage request = new(HttpMethod.Put, $"app/users/proposals/{proposalId}"))
        {
            request.Content = new StringContent(
                content.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await appClient.SendAsync(request);
            this.Response.CopyHeaders(response.Headers);
            await response.ValidateStatusCodeAsync(this.Logger);
            await response.WaitAppTransactionCommittedAsync(this.Logger, this.CcfClientManager);

            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
            return this.Ok(jsonResponse!);
        }
    }

    protected async Task<IActionResult> SubmitVote(string proposalId, JsonObject ballot)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"app/users/proposals/{proposalId}/ballots/submit"))
        {
            request.Content = new StringContent(
                ballot.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await appClient.SendAsync(request);
            this.Response.CopyHeaders(response.Headers);
            await response.ValidateStatusCodeAsync(this.Logger);
            await response.WaitAppTransactionCommittedAsync(this.Logger, this.CcfClientManager);

            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
            return this.Ok(jsonResponse!);
        }
    }

    internal class Member
    {
        [JsonPropertyName("memberId")]
        public string MemberId { get; set; } = default!;

        [JsonPropertyName("status")]
        public string Status { get; set; } = default!;

        [JsonPropertyName("memberData")]
        public MemberData MemberData { get; set; } = default!;
    }

    internal class MemberData
    {
        [JsonPropertyName("isOperator")]
        public bool IsOperator { get; set; } = default!;

        [JsonPropertyName("isRecoveryOperator")]
        public bool IsRecoveryOperator { get; set; } = default!;

        [JsonPropertyName("cgsRoles")]
        public CgsRoles CgsRoles { get; set; } = default!;

        public bool IsNormalMember()
        {
            if (this.IsOperator || this.IsRecoveryOperator)
            {
                return false;
            }

            if (this.CgsRoles != null)
            {
                if (this.CgsRoles.CgsOperator || this.CgsRoles.ContractOperator)
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal class CgsRoles
    {
        [JsonPropertyName("cgsOperator")]
        public bool CgsOperator { get; set; } = default!;

        [JsonPropertyName("contractOperator")]
        public bool ContractOperator { get; set; } = default!;
    }

    internal class MemberList
    {
        [JsonPropertyName("value")]
        public List<Member> Value { get; set; } = default!;
    }
}
