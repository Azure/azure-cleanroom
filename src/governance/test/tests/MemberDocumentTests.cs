// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Test;

[TestClass]
public class MemberDocumentTests : TestBase
{
    private enum DocumentState
    {
        Draft,
        Proposed,
        Accepted
    }

    [TestMethod]
    public async Task GetPutDocumentWithoutAuthentication()
    {
        string documentId = Guid.NewGuid().ToString().Substring(0, 8);
        string documentUrl = $"app/memberdocuments/{documentId}";
        var documentContent = new JsonObject
        {
            ["data"] = "hello world"
        };

        // As no client cert is configured on CcfClient endpoint client these should fail.
        using (HttpRequestMessage request = new(HttpMethod.Put, documentUrl))
        {
            request.Content = new StringContent(
                documentContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (HttpRequestMessage request = new(HttpMethod.Get, documentUrl))
        {
            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [TestMethod]
    public async Task CreateAndAcceptDocument()
    {
        string contractId = this.ContractId;
        await this.ProposeAndAcceptContract(contractId);

        string documentId = Guid.NewGuid().ToString().Substring(0, 8);
        string documentUrl = $"memberdocuments/{documentId}";

        // Document should not be found as we have not added it yet.
        using (HttpRequestMessage request = new(HttpMethod.Get, documentUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("DocumentNotFound", error.Code);
            Assert.AreEqual(
                "A document with the specified id was not found.",
                error.Message);
        }

        var documentContent = new JsonObject
        {
            ["contractId"] = contractId,
            ["data"] = "hello world"
        };

        // Add a document to start with.
        string txnId;
        using (HttpRequestMessage request = new(HttpMethod.Put, documentUrl))
        {
            request.Content = new StringContent(
                documentContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.TryGetValues("x-ms-ccf-transaction-id", out var values));
            Assert.IsNotNull(values);
            txnId = values.First().ToString()!;
        }

        var document = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(documentUrl))!;
        Assert.AreEqual(nameof(DocumentState.Draft), document[StateKey]!.ToString());
        Assert.AreEqual(documentContent["data"]!.ToString(), document["data"]!.ToString());
        Assert.AreEqual(
            documentContent["contractId"]!.ToString(),
            document["contractId"]!.ToString());
        var version = document[VersionKey]!.ToString();
        Assert.AreEqual(version, txnId, "Version value should have matched the transactionId.");

        // Create a proposal for the above document.
        string proposalId;
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"memberdocuments/{documentId}/propose"))
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

        document = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(documentUrl))!;
        Assert.AreEqual(nameof(DocumentState.Proposed), document[StateKey]!.ToString());
        Assert.AreEqual(proposalId, document[ProposalIdKey]!.ToString());
        Assert.AreEqual(documentContent["data"]!.ToString(), document["data"]!.ToString());
        Assert.AreEqual(
            documentContent["contractId"]!.ToString(),
            document["contractId"]!.ToString());

        // Member0: Vote on the above proposal by accepting it.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"memberdocuments/{documentId}/vote_accept"))
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

        // As its a N member system the document should remain in proposed.
        document = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(documentUrl))!;
        Assert.AreEqual(nameof(DocumentState.Proposed), document[StateKey]!.ToString());
        Assert.AreEqual(documentContent["data"]!.ToString(), document["data"]!.ToString());
        Assert.AreEqual(
            documentContent["contractId"]!.ToString(),
            document["contractId"]!.ToString());

        // All remaining members vote on the above document by accepting it.
        foreach (var client in this.CgsClients[1..])
        {
            await this.MemberAcceptDocument(client, documentId, proposalId);
        }

        // As all members voted accepted the document should move to accepted.
        document = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(documentUrl))!;
        Assert.AreEqual(nameof(DocumentState.Accepted), document[StateKey]!.ToString());
        Assert.AreEqual(documentContent["data"]!.ToString(), document["data"]!.ToString());
        Assert.AreEqual(
            documentContent["contractId"]!.ToString(),
            document["contractId"]!.ToString());

        var finalVotes = document["finalVotes"]?.AsArray();
        var fv = JsonSerializer.Deserialize<List<FinalVote>>(finalVotes)!;
        foreach (var client in this.CgsClients)
        {
            var info = await client.GetFromJsonAsync<JsonObject>("/show");
            string memberId = info!["memberId"]!.ToString();
            var vote = fv.Find(v => v.MemberId == memberId);
            Assert.IsNotNull(vote);
            Assert.IsTrue(vote.Vote);
        }

        // Fetching the accepted document before setting the clean room policy should fail.
        using (HttpRequestMessage request = new(HttpMethod.Post, $"memberdocuments/{documentId}"))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifySnpAttestationFailed", error.Code);
            Assert.AreEqual(
                "The clean room policy is missing. Please propose a new clean room policy.",
                error.Message);
        }

        // Set a clean room policy so that fetching the accepted document via the governance
        // sidecar succeeds.
        await this.ProposeAndAcceptAllowAllCleanRoomPolicy(contractId);

        // Fetching the accepted document via the governance sidecar should succeed.
        using (HttpRequestMessage request = new(HttpMethod.Post, $"memberdocuments/{documentId}"))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(document.ToJsonString(), responseBody.ToJsonString());
        }

        // Updating an accepted document should fail.
        using (HttpRequestMessage request = new(HttpMethod.Put, documentUrl))
        {
            request.Content = new StringContent(
                documentContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("DocumentAlreadyAccepted", error.Code);
            Assert.AreEqual(
                "An accepted document cannot be changed.",
                error.Message);
        }
    }

    [TestMethod]
    public async Task CreateAndRejectDocument()
    {
        string contractId = this.ContractId;
        await this.ProposeAndAcceptContract(contractId);

        string documentId = Guid.NewGuid().ToString().Substring(0, 8);
        string documentUrl = $"memberdocuments/{documentId}";
        var documentContent = new JsonObject
        {
            ["contractId"] = contractId,
            ["data"] = "hello world"
        };

        // Add a document to start with.
        using (HttpRequestMessage request = new(HttpMethod.Put, documentUrl))
        {
            request.Content = new StringContent(
                documentContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var document = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(documentUrl))!;
        Assert.AreEqual(nameof(DocumentState.Draft), document[StateKey]!.ToString());
        var version = document[VersionKey]!.ToString();

        // Create a proposal for the above document.
        string proposalId;
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"memberdocuments/{documentId}/propose"))
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

        document = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(documentUrl))!;
        Assert.AreEqual(nameof(DocumentState.Proposed), document[StateKey]!.ToString());
        Assert.AreEqual(proposalId, document[ProposalIdKey]!.ToString());

        // Member0: Vote on the above proposal by reject it.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"memberdocuments/{documentId}/vote_reject"))
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

        // Document should again go back to draft state.
        document = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(documentUrl))!;
        Assert.AreEqual(nameof(DocumentState.Draft), document[StateKey]!.ToString());
    }

    [TestMethod]
    public async Task ProposeDocumentVersionChecks()
    {
        string contractId = this.ContractId;
        await this.ProposeAndAcceptContract(contractId);

        string documentId = Guid.NewGuid().ToString().Substring(0, 8);
        string documentUrl = $"memberdocuments/{documentId}";
        var documentContent = new JsonObject
        {
            ["contractId"] = contractId,
            ["data"] = "hello world"
        };

        // Add a document to start with.
        using (HttpRequestMessage request = new(HttpMethod.Put, documentUrl))
        {
            request.Content = new StringContent(
                documentContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var document = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(documentUrl))!;
        Assert.AreEqual(nameof(DocumentState.Draft), document[StateKey]!.ToString());

        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"memberdocuments/{documentId}/propose"))
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
            Assert.AreEqual("DocumentModified", error.Code);
        }

        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"memberdocuments/{documentId}/propose"))
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
    public async Task VoteDocumentChecks()
    {
        string contractId = this.ContractId;
        await this.ProposeAndAcceptContract(contractId);

        string documentId = Guid.NewGuid().ToString().Substring(0, 8);
        string documentUrl = $"memberdocuments/{documentId}";
        var documentContent = new JsonObject
        {
            ["contractId"] = contractId,
            ["data"] = "hello world"
        };

        // Add a document to start with.
        using (HttpRequestMessage request = new(HttpMethod.Put, documentUrl))
        {
            request.Content = new StringContent(
                documentContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var document = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(documentUrl))!;
        Assert.AreEqual(nameof(DocumentState.Draft), document[StateKey]!.ToString());
        var version = document[VersionKey]!.ToString();

        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"memberdocuments/{documentId}/vote_accept"))
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
            new(HttpMethod.Post, $"memberdocuments/{documentId}/vote_accept"))
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
            Assert.AreEqual("DocumentNotProposed", error.Code);
        }

        // Create a proposal for the above document.
        string proposalId;
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"memberdocuments/{documentId}/propose"))
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
            new(HttpMethod.Post, $"memberdocuments/{documentId}/vote_accept"))
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
    public async Task UpdateDocumentPreconditionFailure()
    {
        string contractId = this.ContractId;
        await this.ProposeAndAcceptContract(contractId);

        string documentId = Guid.NewGuid().ToString().Substring(0, 8);
        string documentUrl = $"memberdocuments/{documentId}";
        var documentContent = new JsonObject
        {
            ["contractId"] = contractId,
            ["data"] = "hello world"
        };

        // Add a document to start with.
        using (HttpRequestMessage request = new(HttpMethod.Put, documentUrl))
        {
            request.Content = new StringContent(
                documentContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var document = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(documentUrl))!;
        string currentVersion = document[VersionKey]!.ToString();
        Assert.AreEqual(nameof(DocumentState.Draft), document[StateKey]!.ToString());
        Assert.IsTrue(!string.IsNullOrEmpty(currentVersion));

        // Any subsequent updates with no Version should fail.
        using (HttpRequestMessage request = new(HttpMethod.Put, documentUrl))
        {
            request.Content = new StringContent(
                documentContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            StringAssert.Contains(
                error.Message,
                "The specified document already exists.");
            Assert.AreEqual("DocumentAlreadyExists", error.Code);
        }

        // Any subsequent updates with a random Version should fail.
        using (HttpRequestMessage request = new(HttpMethod.Put, documentUrl))
        {
            documentContent[VersionKey] = "randomvalue";
            request.Content = new StringContent(
                documentContent.ToJsonString(),
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
        using (HttpRequestMessage request = new(HttpMethod.Put, documentUrl))
        {
            documentContent[VersionKey] = currentVersion;
            request.Content = new StringContent(
                documentContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        document = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(documentUrl))!;
        string newVersion = document[VersionKey]!.ToString();
        Assert.IsTrue(!string.IsNullOrEmpty(newVersion));
        Assert.AreEqual(nameof(DocumentState.Draft), document[StateKey]!.ToString());
        Assert.AreNotEqual(currentVersion, newVersion);
    }

    [TestMethod]
    public async Task DocumentIdAlreadyUnderAnOpenProposal()
    {
        string contractId = this.ContractId;
        await this.ProposeAndAcceptContract(contractId);

        string documentId = Guid.NewGuid().ToString().Substring(0, 8);
        string documentUrl = $"memberdocuments/{documentId}";
        var documentContent = new JsonObject
        {
            ["contractId"] = contractId,
            ["data"] = "hello world"
        };

        // Add a document to start with.
        using (HttpRequestMessage request = new(HttpMethod.Put, documentUrl))
        {
            request.Content = new StringContent(
                documentContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var document = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(documentUrl))!;
        Assert.AreEqual(nameof(DocumentState.Draft), document[StateKey]!.ToString());
        var version = document[VersionKey]!.ToString();

        // Create a proposal for the above document.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"memberdocuments/{documentId}/propose"))
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

            document = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(documentUrl))!;
            Assert.AreEqual(nameof(DocumentState.Proposed), document[StateKey]!.ToString());
            Assert.AreEqual(firstProposalId, document[ProposalIdKey]!.ToString());

            // A second proposal with the same documentId should get auto-rejected while the first
            // remains open.
            using (HttpRequestMessage request2 =
                new(HttpMethod.Post, $"memberdocuments/{documentId}/propose"))
            {
                request2.Content = new StringContent(
                    proposalContent.ToJsonString(),
                    Encoding.UTF8,
                    "application/json");

                using HttpResponseMessage response2 =
                    await this.CgsClient_Member0.SendAsync(request2);
                Assert.AreEqual(HttpStatusCode.OK, response2.StatusCode);
                responseBody = (await response2.Content.ReadFromJsonAsync<JsonObject>())!;
                var secondProposalId = responseBody[ProposalIdKey]!.ToString();

                // Use the historical view API as proposal that is not in an open state gets
                // pruned out by CCF so a simple GET may return not found.
                var proposalResponse =
                    (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                        $"proposals/{secondProposalId}/historical"))!;
                var finalOutcome = proposalResponse["value"]!.AsArray().Last()!.AsObject();
                Assert.AreEqual("Rejected", finalOutcome["proposalState"]!.ToString());

                proposalResponse =
                    (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                        $"proposals/{firstProposalId}"))!;
                Assert.AreEqual("Open", proposalResponse["proposalState"]!.ToString());

                document = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(documentUrl))!;
                Assert.AreEqual(nameof(DocumentState.Proposed), document[StateKey]!.ToString());
                Assert.AreEqual(firstProposalId, document[ProposalIdKey]!.ToString());
            }
        }
    }

    [TestMethod]
    public async Task ListDocuments()
    {
        string contractId = this.ContractId;
        await this.ProposeAndAcceptContract(contractId);

        List<string> documentsId = new();
        for (int i = 0; i < 5; i++)
        {
            documentsId.Add(Guid.NewGuid().ToString().Substring(0, 8));
        }

        // Add a few documents to start with.
        foreach (var documentId in documentsId)
        {
            string documentUrl = $"memberdocuments/{documentId}";
            var documentContent = new JsonObject
            {
                ["contractId"] = contractId,
                ["data"] = "hello world"
            };

            using (HttpRequestMessage request = new(HttpMethod.Put, documentUrl))
            {
                request.Content = new StringContent(
                    documentContent.ToJsonString(),
                    Encoding.UTF8,
                    "application/json");

                using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }
        }

        var documents =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonArray>("memberdocuments"))!;
        foreach (var documentId in documentsId)
        {
            Assert.IsTrue(
                documents.Any(item => item!["id"]!.ToString() == documentId),
                $"Did not find document {documentId} in the incoming document list {documents}");
        }
    }

    [TestMethod]
    public async Task AlreadyAcceptedMemberDocumentProposalChecks()
    {
        string contractId = this.ContractId;
        await this.ProposeAndAcceptContract(contractId);

        string documentId_1 = Guid.NewGuid().ToString().Substring(0, 8);
        string documentId_2 = Guid.NewGuid().ToString().Substring(0, 8);

        await AddAndAcceptDocument(documentId_1);
        await AddAndAcceptDocument(documentId_2);

        // Re-proposing documentId_1 should fail as resolve() logic should catch this.
        // By accepting documentId_2 before attempting this the proposals kv would have removed
        // documentId_1 from its map (as it only keeps the accepted proposal in the map at any
        // time). Still resolve() should handle this.
        var document = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
            $"memberdocuments/{documentId_1}"))!;
        Assert.AreEqual(nameof(DocumentState.Accepted), document[StateKey]!.ToString());
        var version = document[VersionKey]!.ToString();

        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"memberdocuments/{documentId_1}/propose"))
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
            var proposalId = responseBody[ProposalIdKey]!.ToString();

            // Use the historical view API as proposal that is not in an open state gets
            // pruned out by CCF so a simple GET may return not found.
            var proposalResponse =
                (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                    $"proposals/{proposalId}/historical"))!;
            var finalOutcome = proposalResponse["value"]!.AsArray().Last()!.AsObject();
            Assert.AreEqual("Rejected", finalOutcome["proposalState"]!.ToString());
        }

        async Task AddAndAcceptDocument(string documentId)
        {
            string documentUrl = $"memberdocuments/{documentId}";

            var documentContent = new JsonObject
            {
                ["contractId"] = contractId,
                ["data"] = "hello world"
            };

            // Add a document to start with.
            using (HttpRequestMessage request = new(HttpMethod.Put, documentUrl))
            {
                request.Content = new StringContent(
                    documentContent.ToJsonString(),
                    Encoding.UTF8,
                    "application/json");

                using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }

            var document = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(documentUrl))!;
            Assert.AreEqual(nameof(DocumentState.Draft), document[StateKey]!.ToString());
            Assert.AreEqual(documentContent["data"]!.ToString(), document["data"]!.ToString());
            Assert.AreEqual(
                documentContent["contractId"]!.ToString(),
                document["contractId"]!.ToString());
            var version = document[VersionKey]!.ToString();

            // Create a proposal for the above document.
            string proposalId;
            using (HttpRequestMessage request =
                new(HttpMethod.Post, $"memberdocuments/{documentId}/propose"))
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

            document = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(documentUrl))!;
            Assert.AreEqual(nameof(DocumentState.Proposed), document[StateKey]!.ToString());
            Assert.AreEqual(proposalId, document[ProposalIdKey]!.ToString());
            Assert.AreEqual(documentContent["data"]!.ToString(), document["data"]!.ToString());
            Assert.AreEqual(
                documentContent["contractId"]!.ToString(),
                document["contractId"]!.ToString());

            // Member0: Vote on the above proposal by accepting it.
            using (HttpRequestMessage request =
                new(HttpMethod.Post, $"memberdocuments/{documentId}/vote_accept"))
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

            // All remaining members vote on the above document by accepting it.
            foreach (var client in this.CgsClients[1..])
            {
                await this.MemberAcceptDocument(client, documentId, proposalId);
            }

            // As all members voted accepted the document should move to accepted.
            document = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(documentUrl))!;
            Assert.AreEqual(nameof(DocumentState.Accepted), document[StateKey]!.ToString());
            Assert.AreEqual(documentContent["data"]!.ToString(), document["data"]!.ToString());
            Assert.AreEqual(
                documentContent["contractId"]!.ToString(),
                document["contractId"]!.ToString());
        }
    }

    internal class FinalVote
    {
        [JsonPropertyName("memberId")]
        public string MemberId { get; set; } = default!;

        [JsonPropertyName("vote")]
        public bool Vote { get; set; } = default!;
    }
}