// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Test;

[TestClass]
public class UserProposalTests : TestBase
{
    private enum ProposalState
    {
        Open,
        Accepted,
        Withdrawn
    }

    [TestMethod]
    public async Task CreateAndAcceptUserProposal()
    {
        string proposalId;
        string documentId = Guid.NewGuid().ToString();
        string member0Id = await this.GetMemberId(this.CgsClient_Member0);
        string member1Id = await this.GetMemberId(this.CgsClients[Members.Member1]);
        var proposalContent = new JsonObject
        {
            ["name"] = "set_user_document",
            ["args"] = new JsonObject
            {
                ["documentId"] = documentId,
                ["document"] = new JsonObject()
            },
            ["approvers"] = new JsonArray
            {
                new JsonObject()
                {
                    ["approverId"] = member0Id,
                    ["approverIdType"] = "member"
                },
                new JsonObject()
                {
                    ["approverId"] = member1Id,
                    ["approverIdType"] = "member"
                }
            }
        };
        using (HttpRequestMessage request = new(HttpMethod.Post, $"users/proposals/create"))
        {
            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            proposalId = responseBody[ProposalIdKey]!.ToString();

            var proposal = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"users/proposals/{proposalId}"))!;
            Assert.AreEqual("set_user_document", proposal["name"]!.ToString());
        }

        var proposalStatus = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
            $"users/proposals/{proposalId}/status"))!;
        Assert.AreEqual(nameof(ProposalState.Open), proposalStatus[StateKey]!.ToString());

        Assert.AreEqual(member0Id, proposalStatus["proposerId"]!.ToString());

        using (HttpRequestMessage request = new(HttpMethod.Post, $"users/proposals/create"))
        {
            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            // Re-proposing the same documentId should result in an error.
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("UserDocumentAlreadyProposed", error.Code);
            Assert.AreEqual(
                $"Proposal [\"{proposalId}\"] for the specified documentId already exists.",
                error.Message);
        }

        // Vote by member2 should fail.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"users/proposals/{proposalId}/ballots/vote_accept"))
        {
            using HttpResponseMessage response =
                await this.CgsClients[Members.Member2].SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("NotProposalApprover", error.Code);
            Assert.AreEqual($"The caller is not an approver for this proposal.", error.Message);
        }

        // Vote by member0 should get accepted but proposal remains open.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"users/proposals/{proposalId}/ballots/vote_accept"))
        {
            using HttpResponseMessage response =
                await this.CgsClients[Members.Member0].SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            proposalStatus = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(nameof(ProposalState.Open), proposalStatus[StateKey]!.ToString());
        }

        // Voting twice should cause an error.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"users/proposals/{proposalId}/ballots/vote_accept"))
        {
            using HttpResponseMessage response =
                await this.CgsClients[Members.Member0].SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("BallotAlreadySubmitted", error.Code);
            Assert.AreEqual($"The ballot has already been submitted.", error.Message);
        }

        // Vote by member1 should get accepted and proposal should also get accepted.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"users/proposals/{proposalId}/ballots/vote_accept"))
        {
            using HttpResponseMessage response =
                await this.CgsClients[Members.Member1].SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            proposalStatus = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(nameof(ProposalState.Accepted), proposalStatus[StateKey]!.ToString());
            var ballots = proposalStatus["ballots"]!.AsArray().ToList();
            Assert.AreEqual(2, ballots.Count);
            Assert.IsTrue(ballots.Any(b => b!["approverId"]!.ToString() == member0Id));
            Assert.IsTrue(ballots.Any(b => b!["approverId"]!.ToString() == member1Id));
        }
    }

    [TestMethod]
    public async Task CreateAndAcceptUserProposalWithDefaultApprovers()
    {
        string proposalId;
        string documentId = Guid.NewGuid().ToString();
        string member0Id = await this.GetMemberId(this.CgsClient_Member0);
        string member1Id = await this.GetMemberId(this.CgsClients[Members.Member1]);
        string member2Id = await this.GetMemberId(this.CgsClients[Members.Member2]);
        var proposalContent = new JsonObject
        {
            ["name"] = "set_user_document",
            ["args"] = new JsonObject
            {
                ["documentId"] = documentId,
                ["document"] = new JsonObject()
            }

            // No "approvers" set so cgs-client should end up setting all 3 members as approvers.
        };
        using (HttpRequestMessage request = new(HttpMethod.Post, $"users/proposals/create"))
        {
            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            proposalId = responseBody[ProposalIdKey]!.ToString();

            var proposal = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"users/proposals/{proposalId}"))!;
            Assert.AreEqual("set_user_document", proposal["name"]!.ToString());
        }

        var proposalStatus = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
            $"users/proposals/{proposalId}/status"))!;
        Assert.AreEqual(nameof(ProposalState.Open), proposalStatus[StateKey]!.ToString());

        Assert.AreEqual(member0Id, proposalStatus["proposerId"]!.ToString());

        using (HttpRequestMessage request = new(HttpMethod.Post, $"users/proposals/create"))
        {
            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            // Re-proposing the same documentId should result in an error.
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("UserDocumentAlreadyProposed", error.Code);
            Assert.AreEqual(
                $"Proposal [\"{proposalId}\"] for the specified documentId already exists.",
                error.Message);
        }

        // Vote by member2 should get accepted but proposal remains open.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"users/proposals/{proposalId}/ballots/vote_accept"))
        {
            using HttpResponseMessage response =
                await this.CgsClients[Members.Member2].SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            proposalStatus = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(nameof(ProposalState.Open), proposalStatus[StateKey]!.ToString());
        }

        // Vote by member0 should get accepted but proposal remains open.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"users/proposals/{proposalId}/ballots/vote_accept"))
        {
            using HttpResponseMessage response =
                await this.CgsClients[Members.Member0].SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            proposalStatus = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(nameof(ProposalState.Open), proposalStatus[StateKey]!.ToString());
        }

        // Vote by member1 should get accepted and proposal should also get accepted.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"users/proposals/{proposalId}/ballots/vote_accept"))
        {
            using HttpResponseMessage response =
                await this.CgsClients[Members.Member1].SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            proposalStatus = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(nameof(ProposalState.Accepted), proposalStatus[StateKey]!.ToString());
            var ballots = proposalStatus["ballots"]!.AsArray().ToList();
            Assert.AreEqual(3, ballots.Count);
            Assert.IsTrue(ballots.Any(b => b!["approverId"]!.ToString() == member0Id));
            Assert.IsTrue(ballots.Any(b => b!["approverId"]!.ToString() == member1Id));
            Assert.IsTrue(ballots.Any(b => b!["approverId"]!.ToString() == member2Id));
        }
    }

    public async Task CreateUnsupportedUserProposal()
    {
        string proposalName = "invalid_proposal_name";
        var proposalContent = new JsonObject
        {
            ["name"] = proposalName,
            ["args"] = new JsonObject()
        };

        using (HttpRequestMessage request = new(HttpMethod.Post, $"users/proposals/create"))
        {
            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("InvalidProposalType", error.Code);
            Assert.AreEqual(
                $"A proposal of type '{proposalName}' is not supported.",
                error.Message);
        }
    }

    [TestMethod]
    public async Task CreateAndWithdrawUserProposal()
    {
        string proposalId;
        string documentId = Guid.NewGuid().ToString();
        var proposalContent = new JsonObject
        {
            ["name"] = "set_user_document",
            ["args"] = new JsonObject
            {
                ["documentId"] = documentId,
                ["document"] = new JsonObject()
            },
            ["approvers"] = new JsonArray
                {
                    new JsonObject()
                    {
                        ["approverId"] = "m1",
                        ["approverIdType"] = "member"
                    }
                }
        };
        using (HttpRequestMessage request = new(HttpMethod.Post, $"users/proposals/create"))
        {
            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            proposalId = responseBody[ProposalIdKey]!.ToString();

            var proposal = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"users/proposals/{proposalId}"))!;
            Assert.AreEqual("set_user_document", proposal["name"]!.ToString());
        }

        var proposalStatus = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
            $"users/proposals/{proposalId}/status"))!;
        Assert.AreEqual(nameof(ProposalState.Open), proposalStatus[StateKey]!.ToString());

        // Another member cannot withdraw.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"users/proposals/{proposalId}/withdraw"))
        {
            using HttpResponseMessage response =
                await this.CgsClients[Members.Member1].SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("NotProposalOwner", error.Code);
            Assert.AreEqual("Only the proposal owner can withdraw the proposal.", error.Message);
        }

        // Proposer can withdraw.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"users/proposals/{proposalId}/withdraw"))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            proposalStatus = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(nameof(ProposalState.Withdrawn), proposalStatus[StateKey]!.ToString());
        }

        // Withdrawing twice should fail.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"users/proposals/{proposalId}/withdraw"))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("ProposalNotOpen", error.Code);
            Assert.AreEqual(
                $"The proposal is not in an open state. " +
                $"State is: '{nameof(ProposalState.Withdrawn)}'.",
                error.Message);
        }
    }
}