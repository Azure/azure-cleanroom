// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Test;

[TestClass]
public class UserDocumentTests : TestBase
{
    private enum UserDocumentState
    {
        Draft,
        Proposed,
        Accepted
    }

    [TestMethod]
    public async Task CreateAndAcceptUserDocument()
    {
        string contractId = this.ContractId;
        await this.ProposeAndAcceptContract(contractId);

        string documentId = Guid.NewGuid().ToString().Substring(0, 8);
        string userdocumentUrl = $"userdocuments/{documentId}";

        // UserDocument should not be found as we have not added it yet.
        using (HttpRequestMessage request = new(HttpMethod.Get, userdocumentUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("UserDocumentNotFound", error.Code);
            Assert.AreEqual(
                "A document with the specified id was not found.",
                error.Message);
        }

        var userdocumentContent = new JsonObject
        {
            ["contractId"] = contractId,
            ["data"] = "hello world"
        };

        // Add a userdocument to start with.
        string txnId;
        using (HttpRequestMessage request = new(HttpMethod.Put, userdocumentUrl))
        {
            request.Content = new StringContent(
                userdocumentContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.TryGetValues("x-ms-ccf-transaction-id", out var values));
            Assert.IsNotNull(values);
            txnId = values.First().ToString()!;
        }

        var userdocument =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(userdocumentUrl))!;
        Assert.AreEqual(nameof(UserDocumentState.Draft), userdocument[StateKey]!.ToString());
        Assert.AreEqual(userdocumentContent["data"]!.ToString(), userdocument["data"]!.ToString());
        Assert.AreEqual(
            userdocumentContent["contractId"]!.ToString(),
            userdocument["contractId"]!.ToString());
        var version = userdocument[VersionKey]!.ToString();
        Assert.AreEqual(version, txnId, "Version value should have matched the transactionId.");

        // Create a proposal for the above userdocument.
        string proposalId;
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"userdocuments/{documentId}/propose"))
        {
            var proposalContent = new JsonObject
            {
                [VersionKey] = version
            };

            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            proposalId = responseBody[ProposalIdKey]!.ToString();
        }

        userdocument = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(userdocumentUrl))!;
        Assert.AreEqual(nameof(UserDocumentState.Proposed), userdocument[StateKey]!.ToString());
        Assert.AreEqual(proposalId, userdocument[ProposalIdKey]!.ToString());
        Assert.AreEqual(userdocumentContent["data"]!.ToString(), userdocument["data"]!.ToString());
        Assert.AreEqual(
            userdocumentContent["contractId"]!.ToString(),
            userdocument["contractId"]!.ToString());

        // Member0: Vote on the above proposal by accepting it.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"userdocuments/{documentId}/vote_accept"))
        {
            var proposalContent = new JsonObject
            {
                [ProposalIdKey] = proposalId
            };

            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // As its a N member system the userdocument should remain in proposed.
        userdocument = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(userdocumentUrl))!;
        Assert.AreEqual(nameof(UserDocumentState.Proposed), userdocument[StateKey]!.ToString());
        Assert.AreEqual(userdocumentContent["data"]!.ToString(), userdocument["data"]!.ToString());
        Assert.AreEqual(
            userdocumentContent["contractId"]!.ToString(),
            userdocument["contractId"]!.ToString());

        // All remaining members vote on the above userdocument by accepting it.
        foreach (var client in this.CgsClients[1..])
        {
            await this.MemberAcceptUserDocument(client, documentId, proposalId);
        }

        // As all members voted accepted the userdocument should move to accepted.
        userdocument = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(userdocumentUrl))!;
        Assert.AreEqual(nameof(UserDocumentState.Accepted), userdocument[StateKey]!.ToString());
        Assert.AreEqual(userdocumentContent["data"]!.ToString(), userdocument["data"]!.ToString());
        Assert.AreEqual(
            userdocumentContent["contractId"]!.ToString(),
            userdocument["contractId"]!.ToString());

        var finalVotes = userdocument["finalVotes"]?.AsArray();
        var fv = JsonSerializer.Deserialize<List<FinalVote>>(finalVotes)!;
        foreach (var client in this.CgsClients)
        {
            var info = await client.GetFromJsonAsync<JsonObject>("/show");
            string memberId = info!["memberId"]!.ToString();
            var vote = fv.Find(v => v.ApproverId == memberId);
            Assert.IsNotNull(vote);
            Assert.AreEqual("accepted", vote.Ballot);
        }

        // Fetching the accepted userdocument before setting the clean room policy should fail.
        using (HttpRequestMessage request = new(HttpMethod.Post, $"userdocuments/{documentId}"))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifySnpAttestationFailed", error.Code);
            Assert.AreEqual(
                "The clean room policy is missing. Please propose a new clean room policy.",
                error.Message);
        }

        // Set a clean room policy so that fetching the accepted userdocument via the governance
        // sidecar succeeds.
        await this.ProposeAndAcceptAllowAllCleanRoomPolicy(contractId);

        // Fetching the accepted userdocument via the governance sidecar should succeed.
        using (HttpRequestMessage request = new(HttpMethod.Post, $"userdocuments/{documentId}"))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(userdocument.ToJsonString(), responseBody.ToJsonString());
        }

        // Updating an accepted userdocument should fail.
        using (HttpRequestMessage request = new(HttpMethod.Put, userdocumentUrl))
        {
            request.Content = new StringContent(
                userdocumentContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("UserDocumentAlreadyAccepted", error.Code);
            Assert.AreEqual("The specified document has already been accepted.", error.Message);
        }
    }

    [TestMethod]
    public async Task CreateAndAcceptUserDocumentByUsers()
    {
        // Create 3 user identities and set them as approvers for a userdocument. Then vote
        // on the userdocument proposal using JWT tokens for each user.
        List<(string id, HttpClient userClient)> users = await this.CreateAndAcceptUsers(3);

        string contractId = this.ContractId;
        await this.ProposeAndAcceptContract(contractId);

        string documentId = Guid.NewGuid().ToString().Substring(0, 8);
        string userdocumentUrl = $"userdocuments/{documentId}";

        // UserDocument should not be found as we have not added it yet.
        using (HttpRequestMessage request = new(HttpMethod.Get, userdocumentUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("UserDocumentNotFound", error.Code);
            Assert.AreEqual(
                "A document with the specified id was not found.",
                error.Message);
        }

        var approvers = new JsonArray();
        foreach (var (id, userClient) in users)
        {
            approvers.Add(new JsonObject
            {
                ["approverId"] = id,
                ["approverIdType"] = "user"
            });
        }

        var userdocumentContent = new JsonObject
        {
            ["contractId"] = contractId,
            ["data"] = "hello world",
            ["approvers"] = approvers
        };

        // Add a userdocument to start with.
        string txnId;
        using (HttpRequestMessage request = new(HttpMethod.Put, userdocumentUrl))
        {
            request.Content = new StringContent(
                userdocumentContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.TryGetValues("x-ms-ccf-transaction-id", out var values));
            Assert.IsNotNull(values);
            txnId = values.First().ToString()!;
        }

        var userdocument =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(userdocumentUrl))!;
        Assert.AreEqual(nameof(UserDocumentState.Draft), userdocument[StateKey]!.ToString());
        Assert.AreEqual(userdocumentContent["data"]!.ToString(), userdocument["data"]!.ToString());
        Assert.AreEqual(
            userdocumentContent["contractId"]!.ToString(),
            userdocument["contractId"]!.ToString());
        var version = userdocument[VersionKey]!.ToString();
        Assert.AreEqual(version, txnId, "Version value should have matched the transactionId.");

        // Create a proposal for the above userdocument.
        string proposalId;
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"userdocuments/{documentId}/propose"))
        {
            var proposalContent = new JsonObject
            {
                [VersionKey] = version
            };

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
            var proposedApprovers = JsonSerializer.Deserialize<List<Approver>>(
                proposal["approvers"]?.AsArray())!;
            foreach (var (id, _) in users)
            {
                var vote = proposedApprovers.Find(v => v.ApproverId == id);
                Assert.IsNotNull(vote);
            }
        }

        userdocument = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(userdocumentUrl))!;
        Assert.AreEqual(nameof(UserDocumentState.Proposed), userdocument[StateKey]!.ToString());
        Assert.AreEqual(proposalId, userdocument[ProposalIdKey]!.ToString());
        Assert.AreEqual(userdocumentContent["data"]!.ToString(), userdocument["data"]!.ToString());
        Assert.AreEqual(
            userdocumentContent["contractId"]!.ToString(),
            userdocument["contractId"]!.ToString());

        // User0: Vote on the above proposal by accepting it.
        await SubmitUserVote(
            users[0].userClient,
            proposalId,
            new JsonObject { ["ballot"] = "accepted" });

        // As its a N user system the userdocument should remain in proposed.
        userdocument = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(userdocumentUrl))!;
        Assert.AreEqual(nameof(UserDocumentState.Proposed), userdocument[StateKey]!.ToString());
        Assert.AreEqual(userdocumentContent["data"]!.ToString(), userdocument["data"]!.ToString());
        Assert.AreEqual(
            userdocumentContent["contractId"]!.ToString(),
            userdocument["contractId"]!.ToString());

        // All remaining users vote on the above userdocument by accepting it.
        foreach (var (id, userClient) in users[1..])
        {
            await SubmitUserVote(
                userClient,
                proposalId,
                new JsonObject { ["ballot"] = "accepted" });
        }

        // As all users voted accepted the userdocument should move to accepted.
        userdocument = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(userdocumentUrl))!;
        Assert.AreEqual(nameof(UserDocumentState.Accepted), userdocument[StateKey]!.ToString());
        Assert.AreEqual(userdocumentContent["data"]!.ToString(), userdocument["data"]!.ToString());
        Assert.AreEqual(
            userdocumentContent["contractId"]!.ToString(),
            userdocument["contractId"]!.ToString());

        var finalVotes = userdocument["finalVotes"]?.AsArray();

        var fv = JsonSerializer.Deserialize<List<FinalVote>>(finalVotes)!;
        foreach (var (id, _) in users)
        {
            var vote = fv.Find(v => v.ApproverId == id);
            Assert.IsNotNull(vote);
            Assert.AreEqual("accepted", vote.Ballot);
        }

        // Fetching the accepted userdocument before setting the clean room policy should fail.
        using (HttpRequestMessage request = new(HttpMethod.Post, $"userdocuments/{documentId}"))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifySnpAttestationFailed", error.Code);
            Assert.AreEqual(
                "The clean room policy is missing. Please propose a new clean room policy.",
                error.Message);
        }

        // Set a clean room policy so that fetching the accepted userdocument via the governance
        // sidecar succeeds.
        await this.ProposeAndAcceptAllowAllCleanRoomPolicy(contractId);

        // Fetching the accepted userdocument via the governance sidecar should succeed.
        using (HttpRequestMessage request = new(HttpMethod.Post, $"userdocuments/{documentId}"))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(userdocument.ToJsonString(), responseBody.ToJsonString());
        }

        // Updating an accepted userdocument should fail.
        using (HttpRequestMessage request = new(HttpMethod.Put, userdocumentUrl))
        {
            request.Content = new StringContent(
                userdocumentContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("UserDocumentAlreadyAccepted", error.Code);
            Assert.AreEqual("The specified document has already been accepted.", error.Message);
        }

        async Task<JsonObject> SubmitUserVote(
            HttpClient userClient,
            string proposalId,
            JsonObject ballot)
        {
            using (HttpRequestMessage request =
                new(HttpMethod.Post, $"app/users/proposals/{proposalId}/ballots/submit"))
            {
                request.Content = new StringContent(
                    ballot.ToJsonString(),
                    Encoding.UTF8,
                    "application/json");

                using HttpResponseMessage response = await userClient.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                await response.WaitAppTransactionCommittedAsync(this.Logger, userClient);

                var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
                return jsonResponse!;
            }
        }
    }

    [TestMethod]
    public async Task CreateUserDocumentByUnauthorizedUser()
    {
        // Create user identity that is not added in CCF and attempt to make a userdocument using it.
        string userId = Guid.NewGuid().ToString().Substring(0, 8);
        using var tokenResponse = await this.IdpClient.PostAsync(
            $"oauth/token?oid={userId}",
            content: null);
        Assert.AreEqual(HttpStatusCode.OK, tokenResponse.StatusCode);
        var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonObject>())!;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
            {
                return true;
            }
        };

        var userClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(this.Configuration["ccfEndpoint"]!),
        };
        userClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token["accessToken"]!.ToString());

        // As this user is not proposed and accepted in CCF the call below should fail.
        using (HttpRequestMessage request = new(HttpMethod.Get, $"app/userdocuments"))
        {
            using HttpResponseMessage response = await userClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("CallerNotAuthorized", error.Code);
        }
    }

    [TestMethod]
    public async Task CreateAndRejectUserDocument()
    {
        string contractId = this.ContractId;
        await this.ProposeAndAcceptContract(contractId);

        string documentId = Guid.NewGuid().ToString().Substring(0, 8);
        string userdocumentUrl = $"userdocuments/{documentId}";
        var userdocumentContent = new JsonObject
        {
            ["contractId"] = contractId,
            ["data"] = "hello world"
        };

        // Add a userdocument to start with.
        using (HttpRequestMessage request = new(HttpMethod.Put, userdocumentUrl))
        {
            request.Content = new StringContent(
                userdocumentContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var userdocument =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(userdocumentUrl))!;
        Assert.AreEqual(nameof(UserDocumentState.Draft), userdocument[StateKey]!.ToString());
        var version = userdocument[VersionKey]!.ToString();

        // Create a proposal for the above userdocument.
        string proposalId;
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"userdocuments/{documentId}/propose"))
        {
            var proposalContent = new JsonObject
            {
                [VersionKey] = version
            };

            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            proposalId = responseBody[ProposalIdKey]!.ToString();
        }

        userdocument = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(userdocumentUrl))!;
        Assert.AreEqual(nameof(UserDocumentState.Proposed), userdocument[StateKey]!.ToString());
        Assert.AreEqual(proposalId, userdocument[ProposalIdKey]!.ToString());

        // Member0: Vote on the above proposal by reject it.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"userdocuments/{documentId}/vote_reject"))
        {
            var proposalContent = new JsonObject
            {
                [ProposalIdKey] = proposalId
            };

            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // UserDocument should again go back to draft state.
        userdocument = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(userdocumentUrl))!;
        Assert.AreEqual(nameof(UserDocumentState.Draft), userdocument[StateKey]!.ToString());
    }

    [TestMethod]
    public async Task ProposeUserDocumentVersionChecks()
    {
        string contractId = this.ContractId;
        await this.ProposeAndAcceptContract(contractId);

        string documentId = Guid.NewGuid().ToString().Substring(0, 8);
        string userdocumentUrl = $"userdocuments/{documentId}";
        var userdocumentContent = new JsonObject
        {
            ["contractId"] = contractId,
            ["data"] = "hello world"
        };

        // Add a userdocument to start with.
        using (HttpRequestMessage request = new(HttpMethod.Put, userdocumentUrl))
        {
            request.Content = new StringContent(
                userdocumentContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var userdocument =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(userdocumentUrl))!;
        Assert.AreEqual(nameof(UserDocumentState.Draft), userdocument[StateKey]!.ToString());

        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"userdocuments/{documentId}/propose"))
        {
            var incorrectVersionValueContent = new JsonObject
            {
                [VersionKey] = "bar"
            };

            request.Content = new StringContent(
                incorrectVersionValueContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("UserDocumentModified", error.Code);
        }

        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"userdocuments/{documentId}/propose"))
        {
            var noVersionContent = new JsonObject();

            request.Content = new StringContent(
                noVersionContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VersionMissing", error.Code);
        }
    }

    [TestMethod]
    public async Task VoteUserDocumentChecks()
    {
        string contractId = this.ContractId;
        await this.ProposeAndAcceptContract(contractId);

        string documentId = Guid.NewGuid().ToString().Substring(0, 8);
        string userdocumentUrl = $"userdocuments/{documentId}";
        var userdocumentContent = new JsonObject
        {
            ["contractId"] = contractId,
            ["data"] = "hello world"
        };

        // Add a userdocument to start with.
        using (HttpRequestMessage request = new(HttpMethod.Put, userdocumentUrl))
        {
            request.Content = new StringContent(
                userdocumentContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var userdocument =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(userdocumentUrl))!;
        Assert.AreEqual(nameof(UserDocumentState.Draft), userdocument[StateKey]!.ToString());
        var version = userdocument[VersionKey]!.ToString();

        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"userdocuments/{documentId}/vote_accept"))
        {
            var content = new JsonObject();

            request.Content = new StringContent(
                content.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("ProposalIdMissing", error.Code);
        }

        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"userdocuments/{documentId}/vote_accept"))
        {
            var content = new JsonObject
            {
                ["proposalId"] = "foobar"
            };

            request.Content = new StringContent(
                content.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("UserDocumentNotProposed", error.Code);
        }

        // Create a proposal for the above userdocument.
        string proposalId;
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"userdocuments/{documentId}/propose"))
        {
            var proposalContent = new JsonObject
            {
                [VersionKey] = version
            };

            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            proposalId = responseBody[ProposalIdKey]!.ToString();
        }

        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"userdocuments/{documentId}/vote_accept"))
        {
            var content = new JsonObject
            {
                ["proposalId"] = "foobar"
            };

            request.Content = new StringContent(
                content.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("ProposalIdMismatch", error.Code);
        }
    }

    [TestMethod]
    public async Task UpdateUserDocumentPreconditionFailure()
    {
        string contractId = this.ContractId;
        await this.ProposeAndAcceptContract(contractId);

        string documentId = Guid.NewGuid().ToString().Substring(0, 8);
        string userdocumentUrl = $"userdocuments/{documentId}";
        var userdocumentContent = new JsonObject
        {
            ["contractId"] = contractId,
            ["data"] = "hello world"
        };

        // Add a userdocument to start with.
        using (HttpRequestMessage request = new(HttpMethod.Put, userdocumentUrl))
        {
            request.Content = new StringContent(
                userdocumentContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var userdocument =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(userdocumentUrl))!;
        string currentVersion = userdocument[VersionKey]!.ToString();
        Assert.AreEqual(nameof(UserDocumentState.Draft), userdocument[StateKey]!.ToString());
        Assert.IsTrue(!string.IsNullOrEmpty(currentVersion));

        // Any subsequent updates with no Version should fail.
        using (HttpRequestMessage request = new(HttpMethod.Put, userdocumentUrl))
        {
            request.Content = new StringContent(
                userdocumentContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            StringAssert.Contains(
                error.Message,
                "The specified document already exists.");
            Assert.AreEqual("UserDocumentAlreadyExists", error.Code);
        }

        // Any subsequent updates with a random Version should fail.
        using (HttpRequestMessage request = new(HttpMethod.Put, userdocumentUrl))
        {
            userdocumentContent[VersionKey] = "randomvalue";
            request.Content = new StringContent(
                userdocumentContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual(HttpStatusCode.PreconditionFailed, response.StatusCode);
            StringAssert.Contains(
                error.Message,
                "The operation specified a version that is different from " +
                "the version available at the server");
            Assert.AreEqual("PreconditionFailed", error.Code);
        }

        // Any subsequent update with correct Version value should pass.
        using (HttpRequestMessage request = new(HttpMethod.Put, userdocumentUrl))
        {
            userdocumentContent[VersionKey] = currentVersion;
            request.Content = new StringContent(
                userdocumentContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        userdocument = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(userdocumentUrl))!;
        string newVersion = userdocument[VersionKey]!.ToString();
        Assert.IsTrue(!string.IsNullOrEmpty(newVersion));
        Assert.AreEqual(nameof(UserDocumentState.Draft), userdocument[StateKey]!.ToString());
        Assert.AreNotEqual(currentVersion, newVersion);
    }

    [TestMethod]
    public async Task UserDocumentIdAlreadyUnderAnOpenProposal()
    {
        string contractId = this.ContractId;
        await this.ProposeAndAcceptContract(contractId);

        string documentId = Guid.NewGuid().ToString().Substring(0, 8);
        string userdocumentUrl = $"userdocuments/{documentId}";
        var userdocumentContent = new JsonObject
        {
            ["contractId"] = contractId,
            ["data"] = "hello world"
        };

        // Add a userdocument to start with.
        using (HttpRequestMessage request = new(HttpMethod.Put, userdocumentUrl))
        {
            request.Content = new StringContent(
                userdocumentContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var userdocument =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(userdocumentUrl))!;
        Assert.AreEqual(nameof(UserDocumentState.Draft), userdocument[StateKey]!.ToString());
        var version = userdocument[VersionKey]!.ToString();

        // Create a proposal for the above userdocument.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"userdocuments/{documentId}/propose"))
        {
            var proposalContent = new JsonObject
            {
                [VersionKey] = version
            };

            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            var firstProposalId = responseBody[ProposalIdKey]!.ToString();

            userdocument =
                (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(userdocumentUrl))!;
            Assert.AreEqual(nameof(UserDocumentState.Proposed), userdocument[StateKey]!.ToString());
            Assert.AreEqual(firstProposalId, userdocument[ProposalIdKey]!.ToString());

            // A second proposal with the same documentId should get auto-rejected while the
            // first remains open.
            using (HttpRequestMessage request2 =
                new(HttpMethod.Post, $"userdocuments/{documentId}/propose"))
            {
                request2.Content = new StringContent(
                    proposalContent.ToJsonString(),
                    Encoding.UTF8,
                    "application/json");

                using HttpResponseMessage response2 =
                    await this.CgsClient_Member0.SendAsync(request2);
                Assert.AreEqual(HttpStatusCode.Conflict, response2.StatusCode);
                var error = (await response2.Content.ReadFromJsonAsync<ODataError>())!.Error;
                Assert.AreEqual("UserDocumentAlreadyProposed", error.Code);
                Assert.AreEqual(
                    $"Proposal [\"{firstProposalId}\"] for the specified documentId " +
                    $"already exists.",
                    error.Message);

                var proposalResponse =
                    (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                        $"users/proposals/{firstProposalId}/status"))!;
                Assert.AreEqual("Open", proposalResponse["state"]!.ToString());

                userdocument =
                    (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(userdocumentUrl))!;
                Assert.AreEqual(
                    nameof(UserDocumentState.Proposed),
                    userdocument[StateKey]!.ToString());
                Assert.AreEqual(firstProposalId, userdocument[ProposalIdKey]!.ToString());
            }
        }
    }

    [TestMethod]
    public async Task ListUserDocuments()
    {
        string contractId = this.ContractId;
        await this.ProposeAndAcceptContract(contractId);

        List<string> userdocumentsId = new();
        for (int i = 0; i < 5; i++)
        {
            userdocumentsId.Add(Guid.NewGuid().ToString().Substring(0, 8));
        }

        // Add a few userdocuments to start with.
        foreach (var documentId in userdocumentsId)
        {
            string userdocumentUrl = $"userdocuments/{documentId}";
            var userdocumentContent = new JsonObject
            {
                ["contractId"] = contractId,
                ["data"] = "hello world"
            };

            using (HttpRequestMessage request = new(HttpMethod.Put, userdocumentUrl))
            {
                request.Content = new StringContent(
                    userdocumentContent.ToJsonString(),
                    Encoding.UTF8,
                    "application/json");

                using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }
        }

        var userdocumentsJson =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>("userdocuments"))!;
        var userdocuments = userdocumentsJson["value"]?.AsArray() ?? new JsonArray();
        foreach (var documentId in userdocumentsId)
        {
            Assert.IsTrue(
                userdocuments.Any(item => item!["id"]!.ToString() == documentId),
                $"Did not find userdocument {documentId} in the incoming " +
                $"userdocument list {userdocuments}");
        }
    }

    [TestMethod]
    public async Task AlreadyAcceptedUserDocumentProposalChecks()
    {
        string contractId = this.ContractId;
        await this.ProposeAndAcceptContract(contractId);

        string documentId_1 = Guid.NewGuid().ToString().Substring(0, 8);

        await AddAndAcceptUserDocument(documentId_1);

        // Re-proposing documentId_1 should fail as submit proposal logic should catch this.
        var userdocument = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
            $"userdocuments/{documentId_1}"))!;
        Assert.AreEqual(nameof(UserDocumentState.Accepted), userdocument[StateKey]!.ToString());
        var version = userdocument[VersionKey]!.ToString();

        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"userdocuments/{documentId_1}/propose"))
        {
            var proposalContent = new JsonObject
            {
                [VersionKey] = version
            };

            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("UserDocumentAlreadyAccepted", error.Code);
            Assert.AreEqual(
                $"The specified document has already been accepted.",
                error.Message);
        }

        async Task AddAndAcceptUserDocument(string documentId)
        {
            string userdocumentUrl = $"userdocuments/{documentId}";

            var userdocumentContent = new JsonObject
            {
                ["contractId"] = contractId,
                ["data"] = "hello world"
            };

            // Add a userdocument to start with.
            using (HttpRequestMessage request = new(HttpMethod.Put, userdocumentUrl))
            {
                request.Content = new StringContent(
                    userdocumentContent.ToJsonString(),
                    Encoding.UTF8,
                    "application/json");

                using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }

            var userdocument =
                (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(userdocumentUrl))!;
            Assert.AreEqual(nameof(UserDocumentState.Draft), userdocument[StateKey]!.ToString());
            Assert.AreEqual(
                userdocumentContent["data"]!.ToString(),
                userdocument["data"]!.ToString());
            Assert.AreEqual(
                userdocumentContent["contractId"]!.ToString(),
                userdocument["contractId"]!.ToString());
            var version = userdocument[VersionKey]!.ToString();

            // Create a proposal for the above userdocument.
            string proposalId;
            using (HttpRequestMessage request =
                new(HttpMethod.Post, $"userdocuments/{documentId}/propose"))
            {
                var proposalContent = new JsonObject
                {
                    [VersionKey] = version
                };

                request.Content = new StringContent(
                    proposalContent.ToJsonString(),
                    Encoding.UTF8,
                    "application/json");

                using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
                proposalId = responseBody[ProposalIdKey]!.ToString();
            }

            userdocument =
                (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(userdocumentUrl))!;
            Assert.AreEqual(nameof(UserDocumentState.Proposed), userdocument[StateKey]!.ToString());
            Assert.AreEqual(proposalId, userdocument[ProposalIdKey]!.ToString());
            Assert.AreEqual(
                userdocumentContent["data"]!.ToString(),
                userdocument["data"]!.ToString());
            Assert.AreEqual(
                userdocumentContent["contractId"]!.ToString(),
                userdocument["contractId"]!.ToString());

            // Member0: Vote on the above proposal by accepting it.
            using (HttpRequestMessage request =
                new(HttpMethod.Post, $"userdocuments/{documentId}/vote_accept"))
            {
                var proposalContent = new JsonObject
                {
                    [ProposalIdKey] = proposalId
                };

                request.Content = new StringContent(
                    proposalContent.ToJsonString(),
                    Encoding.UTF8,
                    "application/json");

                using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }

            // All remaining members vote on the above userdocument by accepting it.
            foreach (var client in this.CgsClients[1..])
            {
                await this.MemberAcceptUserDocument(client, documentId, proposalId);
            }

            // As all members voted accepted the userdocument should move to accepted.
            userdocument =
                (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(userdocumentUrl))!;
            Assert.AreEqual(nameof(UserDocumentState.Accepted), userdocument[StateKey]!.ToString());
            Assert.AreEqual(
                userdocumentContent["data"]!.ToString(),
                userdocument["data"]!.ToString());
            Assert.AreEqual(
                userdocumentContent["contractId"]!.ToString(),
                userdocument["contractId"]!.ToString());
        }
    }

    [TestMethod]
    public async Task EnableDisableUserDocumentExecution()
    {
        await this.EnableDisableUserDocumentRuntimeOption("execution");
    }

    [TestMethod]
    public async Task EnableDisableUserDocumentTelemetry()
    {
        await this.EnableDisableUserDocumentRuntimeOption("telemetry");
    }

    private async Task EnableDisableUserDocumentRuntimeOption(string option)
    {
        string contractId = this.ContractId;
        string documentId = Guid.NewGuid().ToString().Substring(0, 8);
        string userdocumentUrl = $"userdocuments/{documentId}";
        string checkStatusUrl = userdocumentUrl + $"/checkstatus/{option}";
        string enableUrl = userdocumentUrl + $"/runtimeoptions/{option}/enable";
        string disableUrl = userdocumentUrl + $"/runtimeoptions/{option}/disable";
        string govSidecarConsentCheckUrl = userdocumentUrl + $"/consentcheck/{option}";

        // Check status of a non-existent userdocument.
        using (HttpRequestMessage request = new(HttpMethod.Post, checkStatusUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("disabled", statusResponse.Status);
            Assert.AreEqual("UserDocumentNotAccepted", statusResponse.Reason.Code);
            Assert.AreEqual(
                "UserDocument does not exist or has not been accepted.",
                statusResponse.Reason.Message);
        }

        // Check consent for a non-existent userdocument.
        using (HttpRequestMessage request = new(HttpMethod.Post, govSidecarConsentCheckUrl))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error =
                (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifySnpAttestationFailed", error.Code);
            Assert.AreEqual(
                "The clean room policy is missing. Please propose a new clean room policy.",
                error.Message);
        }

        // Attempt enabling/disabling a non-existent userdocument.
        using (HttpRequestMessage request = new(HttpMethod.Post, enableUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("UserDocumentNotAccepted", error.Code);
            Assert.AreEqual(
                "UserDocument does not exist or has not been accepted.",
                error.Message);
        }

        using (HttpRequestMessage request = new(HttpMethod.Post, disableUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("UserDocumentNotAccepted", error.Code);
            Assert.AreEqual(
                "UserDocument does not exist or has not been accepted.",
                error.Message);
        }

        await this.ProposeAndAcceptContract(contractId);

        // Propose and accept a clean room policy so that consent check api starts to work.
        using (HttpRequestMessage request = new(HttpMethod.Post, govSidecarConsentCheckUrl))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error =
                (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifySnpAttestationFailed", error.Code);
            Assert.AreEqual(
                "The clean room policy is missing. Please propose a new clean room policy.",
                error.Message);
        }

        await this.ProposeAndAcceptAllowAllCleanRoomPolicy(contractId);

        await this.ProposeAndAcceptUserDocument(contractId, documentId);

        // As all members voted accepted the consent check api should now succeed.
        using (HttpRequestMessage request = new(HttpMethod.Post, govSidecarConsentCheckUrl))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("enabled", statusResponse.Status);
            Assert.IsNull(statusResponse.Reason);
        }

        // Disabling the accepted userdocument as member0.
        using (HttpRequestMessage request = new(HttpMethod.Post, disableUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // Status should now report a failure.
        using (HttpRequestMessage request = new(HttpMethod.Post, checkStatusUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("disabled", statusResponse.Status);
            Assert.AreEqual("UserDocumentRuntimeOptionDisabled", statusResponse.Reason.Code);
            Assert.IsTrue(
                statusResponse.Reason.Message.StartsWith(
                    $"UserDocument runtime option '{option}' has been disabled by the " +
                    $"following approver(s): "),
                statusResponse.Reason.Message);
        }

        // Consent check should now report a failure.
        using (HttpRequestMessage request = new(HttpMethod.Post, govSidecarConsentCheckUrl))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("disabled", statusResponse.Status);
            Assert.AreEqual("UserDocumentRuntimeOptionDisabled", statusResponse.Reason.Code);
            Assert.IsTrue(
                statusResponse.Reason.Message.StartsWith(
                    $"UserDocument runtime option '{option}' has been disabled by the " +
                    $"following approver(s): "),
                statusResponse.Reason.Message);
        }

        // Disabling the userdocument again as member0 should pass.
        using (HttpRequestMessage request = new(HttpMethod.Post, disableUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // Enabling the accepted userdocument as member1 should pass but overall the userdocument
        // remains disabled as member0 has it disabled.
        using (HttpRequestMessage request = new(HttpMethod.Post, enableUrl))
        {
            using HttpResponseMessage response =
                await this.CgsClients[Members.Member1].SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // Overall the userdocument remains disabled as member0 has it disabled so check should
        // fail.
        using (HttpRequestMessage request = new(HttpMethod.Post, checkStatusUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("disabled", statusResponse.Status);
            Assert.AreEqual("UserDocumentRuntimeOptionDisabled", statusResponse.Reason.Code);
            Assert.IsTrue(
                statusResponse.Reason.Message.StartsWith(
                    $"UserDocument runtime option '{option}' has been disabled by the " +
                    $"following approver(s): "),
                statusResponse.Reason.Message);
        }

        // Overall the userdocument remains disabled as member0 has it disabled so check should
        // fail.
        using (HttpRequestMessage request = new(HttpMethod.Post, govSidecarConsentCheckUrl))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("disabled", statusResponse.Status);
            Assert.AreEqual("UserDocumentRuntimeOptionDisabled", statusResponse.Reason.Code);
            Assert.IsTrue(
                statusResponse.Reason.Message.StartsWith(
                    $"UserDocument runtime option '{option}' has been disabled by the " +
                    $"following approver(s): "),
                statusResponse.Reason.Message);
        }

        // Disabling the userdocument as member1 should pass.
        using (HttpRequestMessage request = new(HttpMethod.Post, disableUrl))
        {
            using HttpResponseMessage response =
                await this.CgsClients[Members.Member1].SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // Enabling the disabled userdocument as member0 should pass.
        using (HttpRequestMessage request = new(HttpMethod.Post, enableUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // Enabling the accepted userdocument as member1 should also.
        using (HttpRequestMessage request = new(HttpMethod.Post, enableUrl))
        {
            using HttpResponseMessage response =
                await this.CgsClients[Members.Member1].SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // UserDocument status API should now succeed.
        using (HttpRequestMessage request = new(HttpMethod.Post, checkStatusUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("enabled", statusResponse.Status);
            Assert.IsNull(statusResponse.Reason);
        }

        // Consent check API should now succeed.
        using (HttpRequestMessage request = new(HttpMethod.Post, govSidecarConsentCheckUrl))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("enabled", statusResponse.Status);
            Assert.IsNull(statusResponse.Reason);
        }
    }

    internal class FinalVote
    {
        [JsonPropertyName("approverId")]
        public string ApproverId { get; set; } = default!;

        [JsonPropertyName("ballot")]
        public string Ballot { get; set; } = default!;
    }

    internal class Approver
    {
        [JsonPropertyName("approverId")]
        public string ApproverId { get; set; } = default!;

        [JsonPropertyName("approverIdType")]
        public string ApproverIdType { get; set; } = default!;
    }
}