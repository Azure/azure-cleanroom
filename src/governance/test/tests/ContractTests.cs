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
public class ContractTests : TestBase
{
    internal enum ContractState
    {
        /// <summary>
        /// Draft.
        /// </summary>
        Draft,

        /// <summary>
        /// Proposed.
        /// </summary>
        Proposed,

        /// <summary>
        /// Accepted.
        /// </summary>
        Accepted
    }

    [TestMethod]
    public async Task GetPutContractWithoutAuthentication()
    {
        string contractId = Guid.NewGuid().ToString().Substring(0, 8);
        string contractUrl = $"app/contracts/{contractId}";
        var contractContent = new JsonObject
        {
            ["data"] = "hello world"
        };

        // As no client cert is configured on CcfClient endpoint client these should fail.
        using (HttpRequestMessage request = new(HttpMethod.Put, contractUrl))
        {
            request.Content = new StringContent(
                contractContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (HttpRequestMessage request = new(HttpMethod.Get, contractUrl))
        {
            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [TestMethod]
    public async Task CreateAndAcceptContract()
    {
        string contractId = Guid.NewGuid().ToString().Substring(0, 8);
        string contractUrl = $"contracts/{contractId}";
        string checkStatusUrl = contractUrl + "/checkstatus/execution";

        // Contract should not be found as we have not added it yet.
        using (HttpRequestMessage request = new(HttpMethod.Get, contractUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("ContractNotFound", error.Code);
            Assert.AreEqual(
                "A contract with the specified id was not found.",
                error.Message);
        }

        using (HttpRequestMessage request = new(HttpMethod.Post, checkStatusUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("disabled", statusResponse.Status);
            Assert.AreEqual("ContractNotAccepted", statusResponse.Reason.Code);
            Assert.AreEqual(
                "Contract does not exist or has not been accepted.",
                statusResponse.Reason.Message);
        }

        var contractContent = new JsonObject
        {
            ["data"] = "hello world"
        };

        // Add a contract to start with.
        string txnId;
        using (HttpRequestMessage request = new(HttpMethod.Put, contractUrl))
        {
            request.Content = new StringContent(
                contractContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.TryGetValues("x-ms-ccf-transaction-id", out var values));
            Assert.IsNotNull(values);
            txnId = values.First().ToString()!;
        }

        var contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
        Assert.AreEqual(nameof(ContractState.Draft), contract[StateKey]!.ToString());
        Assert.AreEqual(contractContent["data"]!.ToString(), contract["data"]!.ToString());
        var version = contract[VersionKey]!.ToString();
        Assert.AreEqual(version, txnId, "Version value should have matched the transactionId.");

        // Create a proposal for the above contract.
        string proposalId;
        using (HttpRequestMessage request = new(HttpMethod.Post, $"contracts/{contractId}/propose"))
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

        contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
        Assert.AreEqual(nameof(ContractState.Proposed), contract[StateKey]!.ToString());
        Assert.AreEqual(proposalId, contract[ProposalIdKey]!.ToString());
        Assert.AreEqual(contractContent["data"]!.ToString(), contract["data"]!.ToString());

        // Member0: Vote on the above proposal by accepting it.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"contracts/{contractId}/vote_accept"))
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

        // As its a N member system the contract should remain in proposed.
        contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
        Assert.AreEqual(nameof(ContractState.Proposed), contract[StateKey]!.ToString());
        Assert.AreEqual(contractContent["data"]!.ToString(), contract["data"]!.ToString());

        using (HttpRequestMessage request = new(HttpMethod.Post, checkStatusUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("disabled", statusResponse.Status);
            Assert.AreEqual("ContractNotAccepted", statusResponse.Reason.Code);
            Assert.AreEqual(
                "Contract does not exist or has not been accepted.",
                statusResponse.Reason.Message);
        }

        // All remaining members vote on the above contract by accepting it.
        foreach (var client in this.CgsClients[1..])
        {
            await this.MemberAcceptContract(client, contractId, proposalId);
        }

        // As all members voted accepted the contract should move to accepted.
        contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
        Assert.AreEqual(nameof(ContractState.Accepted), contract[StateKey]!.ToString());
        Assert.AreEqual(contractContent["data"]!.ToString(), contract["data"]!.ToString());

        var finalVotes = contract["finalVotes"]?.AsArray();

        // Remove the null check once CCF v4.0 support is removed.
        if (finalVotes != null && finalVotes.Any())
        {
            var fv = JsonSerializer.Deserialize<List<FinalVote>>(finalVotes)!;
            foreach (var client in this.CgsClients)
            {
                var info = await client.GetFromJsonAsync<JsonObject>("/show");
                string memberId = info!["memberId"]!.ToString();
                var vote = fv.Find(v => v.MemberId == memberId);
                Assert.IsNotNull(vote);
                Assert.IsTrue(vote.Vote);
            }
        }

        using (HttpRequestMessage request = new(HttpMethod.Post, checkStatusUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("enabled", statusResponse.Status);
            Assert.IsNull(statusResponse.Reason);
        }

        // Updating an accepted contract should fail.
        using (HttpRequestMessage request = new(HttpMethod.Put, contractUrl))
        {
            request.Content = new StringContent(
                contractContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("ContractAlreadyAccepted", error.Code);
            Assert.AreEqual(
                "An accepted contract cannot be changed.",
                error.Message);
        }
    }

    [TestMethod]
    public async Task CreateAndRejectContract()
    {
        string contractId = Guid.NewGuid().ToString().Substring(0, 8);
        string contractUrl = $"contracts/{contractId}";
        var contractContent = new JsonObject
        {
            ["data"] = "hello world"
        };

        // Add a contract to start with.
        using (HttpRequestMessage request = new(HttpMethod.Put, contractUrl))
        {
            request.Content = new StringContent(
                contractContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
        Assert.AreEqual(nameof(ContractState.Draft), contract[StateKey]!.ToString());
        var version = contract[VersionKey]!.ToString();

        // Create a proposal for the above contract.
        string proposalId;
        using (HttpRequestMessage request = new(HttpMethod.Post, $"contracts/{contractId}/propose"))
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

        contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
        Assert.AreEqual(nameof(ContractState.Proposed), contract[StateKey]!.ToString());
        Assert.AreEqual(proposalId, contract[ProposalIdKey]!.ToString());

        // Member0: Vote on the above proposal by reject it.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"contracts/{contractId}/vote_reject"))
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

        // Contract should again go back to draft state.
        contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
        Assert.AreEqual(nameof(ContractState.Draft), contract[StateKey]!.ToString());
    }

    [TestMethod]
    public async Task ProposeContractVersionChecks()
    {
        string contractId = Guid.NewGuid().ToString().Substring(0, 8);
        string contractUrl = $"contracts/{contractId}";
        var contractContent = new JsonObject
        {
            ["data"] = "hello world"
        };

        // Add a contract to start with.
        using (HttpRequestMessage request = new(HttpMethod.Put, contractUrl))
        {
            request.Content = new StringContent(
                contractContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
        Assert.AreEqual(nameof(ContractState.Draft), contract[StateKey]!.ToString());

        using (HttpRequestMessage request = new(HttpMethod.Post, $"contracts/{contractId}/propose"))
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
            Assert.AreEqual("ContractModified", error.Code);
        }

        using (HttpRequestMessage request = new(HttpMethod.Post, $"contracts/{contractId}/propose"))
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
    public async Task VoteContractChecks()
    {
        string contractId = Guid.NewGuid().ToString().Substring(0, 8);
        string contractUrl = $"contracts/{contractId}";
        var contractContent = new JsonObject
        {
            ["data"] = "hello world"
        };

        // Add a contract to start with.
        using (HttpRequestMessage request = new(HttpMethod.Put, contractUrl))
        {
            request.Content = new StringContent(
                contractContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
        Assert.AreEqual(nameof(ContractState.Draft), contract[StateKey]!.ToString());
        var version = contract[VersionKey]!.ToString();

        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"contracts/{contractId}/vote_accept"))
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
            new(HttpMethod.Post, $"contracts/{contractId}/vote_accept"))
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
            Assert.AreEqual("ContractNotProposed", error.Code);
        }

        // Create a proposal for the above contract.
        string proposalId;
        using (HttpRequestMessage request = new(HttpMethod.Post, $"contracts/{contractId}/propose"))
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
            new(HttpMethod.Post, $"contracts/{contractId}/vote_accept"))
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
    public async Task UpdateContractPreconditionFailure()
    {
        string contractId = Guid.NewGuid().ToString().Substring(0, 8);
        string contractUrl = $"contracts/{contractId}";
        var contractContent = new JsonObject
        {
            ["data"] = "hello world"
        };

        // Add a contract to start with.
        using (HttpRequestMessage request = new(HttpMethod.Put, contractUrl))
        {
            request.Content = new StringContent(
                contractContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
        string currentVersion = contract[VersionKey]!.ToString();
        Assert.AreEqual(nameof(ContractState.Draft), contract[StateKey]!.ToString());
        Assert.IsTrue(!string.IsNullOrEmpty(currentVersion));

        // Any subsequent updates with no Version should fail.
        using (HttpRequestMessage request = new(HttpMethod.Put, contractUrl))
        {
            request.Content = new StringContent(
                contractContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            StringAssert.Contains(
                error.Message,
                "The specified contract already exists.");
            Assert.AreEqual("ContractAlreadyExists", error.Code);
        }

        // Any subsequent updates with a random Version should fail.
        using (HttpRequestMessage request = new(HttpMethod.Put, contractUrl))
        {
            contractContent[VersionKey] = "randomvalue";
            request.Content = new StringContent(
                contractContent.ToJsonString(),
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
        using (HttpRequestMessage request = new(HttpMethod.Put, contractUrl))
        {
            contractContent[VersionKey] = currentVersion;
            request.Content = new StringContent(
                contractContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
        string newVersion = contract[VersionKey]!.ToString();
        Assert.IsTrue(!string.IsNullOrEmpty(newVersion));
        Assert.AreEqual(nameof(ContractState.Draft), contract[StateKey]!.ToString());
        Assert.AreNotEqual(currentVersion, newVersion);
    }

    [TestMethod]
    public async Task ContractIdAlreadyUnderAnOpenProposal()
    {
        string contractId = Guid.NewGuid().ToString().Substring(0, 8);
        string contractUrl = $"contracts/{contractId}";
        var contractContent = new JsonObject
        {
            ["data"] = "hello world"
        };

        // Add a contract to start with.
        using (HttpRequestMessage request = new(HttpMethod.Put, contractUrl))
        {
            request.Content = new StringContent(
                contractContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
        Assert.AreEqual(nameof(ContractState.Draft), contract[StateKey]!.ToString());
        var version = contract[VersionKey]!.ToString();

        // Create a proposal for the above contract.
        using (HttpRequestMessage request = new(HttpMethod.Post, $"contracts/{contractId}/propose"))
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

            contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
            Assert.AreEqual(nameof(ContractState.Proposed), contract[StateKey]!.ToString());
            Assert.AreEqual(firstProposalId, contract[ProposalIdKey]!.ToString());

            // A second proposal with the same contractId should get auto-rejected while the first
            // remains open.
            using (HttpRequestMessage request2 =
                new(HttpMethod.Post, $"contracts/{contractId}/propose"))
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

                contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
                Assert.AreEqual(nameof(ContractState.Proposed), contract[StateKey]!.ToString());
                Assert.AreEqual(firstProposalId, contract[ProposalIdKey]!.ToString());
            }
        }
    }

    [TestMethod]
    public async Task DuplicateContractIdInActions()
    {
        string contractId = Guid.NewGuid().ToString().Substring(0, 8);
        string contractUrl = $"contracts/{contractId}";
        var contractContent = new JsonObject
        {
            ["data"] = "hello world"
        };

        // Add a contract to start with.
        using (HttpRequestMessage request = new(HttpMethod.Put, contractUrl))
        {
            request.Content = new StringContent(
                contractContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
        Assert.AreEqual(nameof(ContractState.Draft), contract[StateKey]!.ToString());

        // Create a proposal with duplicate contractId value.
        using (HttpRequestMessage request = new(HttpMethod.Post, "proposals/create"))
        {
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
                            ["contract"] = new JsonObject()
                        }
                    },
                    {
                        new JsonObject
                        {
                            ["name"] = "set_contract",
                            ["args"] = new JsonObject
                            {
                                ["contractId"] = contractId,
                                ["contract"] = new JsonObject()
                            }
                        }
                    }
                }
            };

            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            var firstProposalId = responseBody[ProposalIdKey]!.ToString();

            // Use the historical view API as proposal that is not in an open state gets
            // pruned out by CCF so a simple GET may return not found.
            var proposalResponse =
                (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                    $"proposals/{firstProposalId}/historical"))!;
            var finalOutcome = proposalResponse["value"]!.AsArray().Last()!.AsObject();
            Assert.AreEqual("Rejected", finalOutcome["proposalState"]!.ToString());

            contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
            Assert.AreEqual(nameof(ContractState.Draft), contract[StateKey]!.ToString());
        }
    }

    [TestMethod]
    public async Task SetContractMixedWithOtherActions()
    {
        string contractId = Guid.NewGuid().ToString().Substring(0, 8);
        string contractUrl = $"contracts/{contractId}";
        var contractContent = new JsonObject
        {
            ["data"] = "hello world"
        };

        // Add a contract to start with.
        using (HttpRequestMessage request = new(HttpMethod.Put, contractUrl))
        {
            request.Content = new StringContent(
                contractContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
        Assert.AreEqual(nameof(ContractState.Draft), contract[StateKey]!.ToString());

        // Create a proposal with set_contract and set_member. Mixing is not allowed.
        using (HttpRequestMessage request = new(HttpMethod.Post, "proposals/create"))
        {
#pragma warning disable MEN002 // Line is too long
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
                            ["contract"] = new JsonObject()
                        }
                    },
                    {
                        new JsonObject
                        {
                            ["name"] = "set_member",
                            ["args"] = new JsonObject
                            {
                                ["cert"] = "-----BEGIN CERTIFICATE-----\nMIIBtTCCATygAwIBAgIUNf1fqzJ1TIf7yf306HOIQxLSVsYwCgYIKoZIzj0EAwMw\nEjEQMA4GA1UEAwwHbWVtYmVyMTAeFw0yNDAyMDcwOTAyMjNaFw0yNTAyMDYwOTAy\nMjNaMBIxEDAOBgNVBAMMB21lbWJlcjEwdjAQBgcqhkjOPQIBBgUrgQQAIgNiAASl\nQVQQXmiNNN5kFQlC6PYsNgZUd7dI3RViGAotrK8MB6p+WUCDDRGT+XZqyOHyRuPj\nDqhrcxFzym8uho1P+pT3ApO8sGqJSwem7SD6MkAbrQXuY0b8VJbM6KvfZeU9fxWj\nUzBRMB0GA1UdDgQWBBT6dGGfJTZszXvcuuTLqAT9UUeJcjAfBgNVHSMEGDAWgBT6\ndGGfJTZszXvcuuTLqAT9UUeJcjAPBgNVHRMBAf8EBTADAQH/MAoGCCqGSM49BAMD\nA2cAMGQCMGfLTOE/0PA27VgrrDgXJuNMGDmE+pquW93kyQ96nNkDuQyhnCzaUO2H\nvKJ5mVSdnQIwe20kMDeLvcDc1z55eH4tQY2gtHeLlv/FKmNkIx4yZmIcC0BkRAu1\nUyA+UU+jKDcf\n-----END CERTIFICATE-----\n"
                            }
                        }
                    }
                }
            };
#pragma warning restore MEN002 // Line is too long

            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            var firstProposalId = responseBody[ProposalIdKey]!.ToString();

            // Use the historical view API as proposal that is not in an open state gets
            // pruned out by CCF so a simple GET may return not found.
            var proposalResponse =
                (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                    $"proposals/{firstProposalId}/historical"))!;
            var finalOutcome = proposalResponse["value"]!.AsArray().Last()!.AsObject();
            Assert.AreEqual("Rejected", finalOutcome["proposalState"]!.ToString());

            contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
            Assert.AreEqual(nameof(ContractState.Draft), contract[StateKey]!.ToString());
        }
    }

    [TestMethod]
    public async Task ListContracts()
    {
        List<string> contractsId = new();
        for (int i = 0; i < 5; i++)
        {
            contractsId.Add(Guid.NewGuid().ToString().Substring(0, 8));
        }

        // Add a few contracts to start with.
        foreach (var contractId in contractsId)
        {
            string contractUrl = $"contracts/{contractId}";
            var contractContent = new JsonObject
            {
                ["data"] = "hello world"
            };

            using (HttpRequestMessage request = new(HttpMethod.Put, contractUrl))
            {
                request.Content = new StringContent(
                    contractContent.ToJsonString(),
                    Encoding.UTF8,
                    "application/json");

                using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }
        }

        var contracts = (await this.CgsClient_Member0.GetFromJsonAsync<JsonArray>("contracts"))!;
        foreach (var contractId in contractsId)
        {
            Assert.IsTrue(
                contracts.Any(item => item!["id"]!.ToString() == contractId),
                $"Did not find contract {contractId} in the incoming contract list {contracts}");
        }
    }

    [TestMethod]
    public async Task AlreadyAcceptedContractProposalChecks()
    {
        string contractId_1 = Guid.NewGuid().ToString().Substring(0, 8);
        string contractId_2 = Guid.NewGuid().ToString().Substring(0, 8);

        await AddAndAcceptContract(contractId_1);
        await AddAndAcceptContract(contractId_2);

        // Re-proposing contractId_1 should fail as resolve() logic should catch this.
        // By accepting contractId_2 before attempting this the proposals kv would have removed
        // contractId_1 from its map (as it only keeps the accepted proposal in the map at any
        // time). Still resolve() should handle this.
        var contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
            $"contracts/{contractId_1}"))!;
        Assert.AreEqual(nameof(ContractState.Accepted), contract[StateKey]!.ToString());
        var version = contract[VersionKey]!.ToString();

        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"contracts/{contractId_1}/propose"))
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

        async Task AddAndAcceptContract(string contractId)
        {
            string contractUrl = $"contracts/{contractId}";

            var contractContent = new JsonObject
            {
                ["data"] = "hello world"
            };

            // Add a contract to start with.
            using (HttpRequestMessage request = new(HttpMethod.Put, contractUrl))
            {
                request.Content = new StringContent(
                    contractContent.ToJsonString(),
                    Encoding.UTF8,
                    "application/json");

                using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }

            var contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
            Assert.AreEqual(nameof(ContractState.Draft), contract[StateKey]!.ToString());
            Assert.AreEqual(contractContent["data"]!.ToString(), contract["data"]!.ToString());
            var version = contract[VersionKey]!.ToString();

            // Create a proposal for the above contract.
            string proposalId;
            using (HttpRequestMessage request =
                new(HttpMethod.Post, $"contracts/{contractId}/propose"))
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

            contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
            Assert.AreEqual(nameof(ContractState.Proposed), contract[StateKey]!.ToString());
            Assert.AreEqual(proposalId, contract[ProposalIdKey]!.ToString());
            Assert.AreEqual(contractContent["data"]!.ToString(), contract["data"]!.ToString());

            // Member0: Vote on the above proposal by accepting it.
            using (HttpRequestMessage request =
                new(HttpMethod.Post, $"contracts/{contractId}/vote_accept"))
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

            // All remaining members vote on the above contract by accepting it.
            foreach (var client in this.CgsClients[1..])
            {
                await this.MemberAcceptContract(client, contractId, proposalId);
            }

            // As all members voted accepted the contract should move to accepted.
            contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
            Assert.AreEqual(nameof(ContractState.Accepted), contract[StateKey]!.ToString());
            Assert.AreEqual(contractContent["data"]!.ToString(), contract["data"]!.ToString());
        }
    }

    [TestMethod]
    public async Task EnableDisableContractExecution()
    {
        string contractId = this.ContractId;
        string contractUrl = $"contracts/{contractId}";
        string checkExecutionStatusUrl = contractUrl + "/checkstatus/execution";
        string enableUrl = contractUrl + "/execution/enable";
        string disableUrl = contractUrl + "/execution/disable";
        string consentCheckUrl = "/consentcheck/execution";

        // Check status of a non-existent contract.
        using (HttpRequestMessage request = new(HttpMethod.Post, checkExecutionStatusUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("disabled", statusResponse.Status);
            Assert.AreEqual("ContractNotAccepted", statusResponse.Reason.Code);
            Assert.AreEqual(
                "Contract does not exist or has not been accepted.",
                statusResponse.Reason.Message);
        }

        // Check consent for a non-existent contract.
        using (HttpRequestMessage request = new(HttpMethod.Post, consentCheckUrl))
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

        // Attempt enabling/disabling a non-existent contract.
        using (HttpRequestMessage request = new(HttpMethod.Post, enableUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("ContractNotAccepted", error.Code);
            Assert.AreEqual(
                "Contract does not exist or has not been accepted.",
                error.Message);
        }

        using (HttpRequestMessage request = new(HttpMethod.Post, disableUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("ContractNotAccepted", error.Code);
            Assert.AreEqual(
                "Contract does not exist or has not been accepted.",
                error.Message);
        }

        var contractContent = new JsonObject
        {
            ["data"] = "hello world"
        };

        // Add a contract to start with.
        string txnId;
        using (HttpRequestMessage request = new(HttpMethod.Put, contractUrl))
        {
            request.Content = new StringContent(
                contractContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.TryGetValues("x-ms-ccf-transaction-id", out var values));
            Assert.IsNotNull(values);
            txnId = values.First().ToString()!;
        }

        var contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
        Assert.AreEqual(nameof(ContractState.Draft), contract[StateKey]!.ToString());
        Assert.AreEqual(contractContent["data"]!.ToString(), contract["data"]!.ToString());
        var version = contract[VersionKey]!.ToString();
        Assert.AreEqual(version, txnId, "Version value should have matched the transactionId.");

        // Create a proposal for the above contract.
        string proposalId;
        using (HttpRequestMessage request = new(HttpMethod.Post, $"contracts/{contractId}/propose"))
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

        contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
        Assert.AreEqual(nameof(ContractState.Proposed), contract[StateKey]!.ToString());
        Assert.AreEqual(proposalId, contract[ProposalIdKey]!.ToString());
        Assert.AreEqual(contractContent["data"]!.ToString(), contract["data"]!.ToString());

        // Member0: Vote on the above proposal by accepting it.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"contracts/{contractId}/vote_accept"))
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

        // As its a N member system the contract status check should still fail as contract is not
        // yet accepted.
        using (HttpRequestMessage request = new(HttpMethod.Post, checkExecutionStatusUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("disabled", statusResponse.Status);
            Assert.AreEqual("ContractNotAccepted", statusResponse.Reason.Code);
            Assert.AreEqual(
                "Contract does not exist or has not been accepted.",
                statusResponse.Reason.Message);
        }

        // All remaining members vote on the above proposal by accepting it.
        foreach (var client in this.CgsClients[1..])
        {
            await this.MemberAcceptContract(client, contractId, proposalId);
        }

        // As all members voted accepted the contract status api should now succeed.
        using (HttpRequestMessage request = new(HttpMethod.Post, checkExecutionStatusUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("enabled", statusResponse.Status);
            Assert.IsNull(statusResponse.Reason);
        }

        // Propose and accept a clean room policy so that consent check api starts to work.
        using (HttpRequestMessage request = new(HttpMethod.Post, consentCheckUrl))
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

        // As all members voted accepted the consent check api should now succeed.
        using (HttpRequestMessage request = new(HttpMethod.Post, consentCheckUrl))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("enabled", statusResponse.Status);
            Assert.IsNull(statusResponse.Reason);
        }

        // Disabling the accepted contract as member0.
        using (HttpRequestMessage request = new(HttpMethod.Post, disableUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // Status should now report a failure.
        using (HttpRequestMessage request = new(HttpMethod.Post, checkExecutionStatusUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("disabled", statusResponse.Status);
            Assert.AreEqual("ContractDisabled", statusResponse.Reason.Code);
            Assert.IsTrue(
                statusResponse.Reason.Message.StartsWith(
                    $"Contract has been disabled by the following member(s): "));
        }

        // Consent check should now report a failure.
        using (HttpRequestMessage request = new(HttpMethod.Post, consentCheckUrl))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("disabled", statusResponse.Status);
            Assert.AreEqual("ContractDisabled", statusResponse.Reason.Code);
            Assert.IsTrue(
                statusResponse.Reason.Message.StartsWith(
                    $"Contract has been disabled by the following member(s): "));
        }

        // Disabling the contract again as member0 should pass.
        using (HttpRequestMessage request = new(HttpMethod.Post, disableUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // Enabling the accepted contract as member1 should pass but overall the contract remains
        // disabled as member0 has it disabled.
        using (HttpRequestMessage request = new(HttpMethod.Post, enableUrl))
        {
            using HttpResponseMessage response =
                await this.CgsClients[Members.Member1].SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // Overall the contract remains disabled as member0 has it disabled so check should fail.
        using (HttpRequestMessage request = new(HttpMethod.Post, checkExecutionStatusUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("disabled", statusResponse.Status);
            Assert.AreEqual("ContractDisabled", statusResponse.Reason.Code);
            Assert.IsTrue(
                statusResponse.Reason.Message.StartsWith(
                    $"Contract has been disabled by the following member(s): "));
        }

        // Overall the contract remains disabled as member0 has it disabled so check should fail.
        using (HttpRequestMessage request = new(HttpMethod.Post, consentCheckUrl))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("disabled", statusResponse.Status);
            Assert.AreEqual("ContractDisabled", statusResponse.Reason.Code);
            Assert.IsTrue(
                statusResponse.Reason.Message.StartsWith(
                    $"Contract has been disabled by the following member(s): "));
        }

        // Disabling the contract as member1 should pass.
        using (HttpRequestMessage request = new(HttpMethod.Post, disableUrl))
        {
            using HttpResponseMessage response =
                await this.CgsClients[Members.Member1].SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // Enabling the disabled contract as member0 should pass.
        using (HttpRequestMessage request = new(HttpMethod.Post, enableUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // Enabling the accepted contract as member1 should also.
        using (HttpRequestMessage request = new(HttpMethod.Post, enableUrl))
        {
            using HttpResponseMessage response =
                await this.CgsClients[Members.Member1].SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // Contract status API should now succeed.
        using (HttpRequestMessage request = new(HttpMethod.Post, checkExecutionStatusUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("enabled", statusResponse.Status);
            Assert.IsNull(statusResponse.Reason);
        }

        // Consent check API should now succeed.
        using (HttpRequestMessage request = new(HttpMethod.Post, consentCheckUrl))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("enabled", statusResponse.Status);
            Assert.IsNull(statusResponse.Reason);
        }
    }

    [TestMethod]
    public async Task EnableDisableLoggingOption()
    {
        await this.EnableDisableRuntimeOption("logging");
    }

    [TestMethod]
    public async Task EnableDisableTelemetryOption()
    {
        await this.EnableDisableRuntimeOption("telemetry");
    }

    public async Task EnableDisableRuntimeOption(string optionName)
    {
        string contractId = this.ContractId;
        string contractUrl = $"contracts/{contractId}";
        string checkOptionStatusUrl = contractUrl + $"/checkstatus/{optionName}";
        string enableUrl = contractUrl + $"/{optionName}/propose-enable";
        string disableUrl = contractUrl + $"/{optionName}/propose-disable";
        string consentCheckUrl = $"/consentcheck/{optionName}";

        // Check status of a non-existent contract.
        using (HttpRequestMessage request = new(HttpMethod.Post, checkOptionStatusUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("disabled", statusResponse.Status);
            Assert.AreEqual("ContractNotAccepted", statusResponse.Reason.Code);
            Assert.AreEqual(
                "Contract does not exist or has not been accepted.",
                statusResponse.Reason.Message);
        }

        // Check consent for a non-existent contract.
        using (HttpRequestMessage request = new(HttpMethod.Post, consentCheckUrl))
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

        // Attempt enabling/disabling a non-existent contract.
        using (HttpRequestMessage request = new(HttpMethod.Post, enableUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("ProposalFailedToValidate", error.Code);
            Assert.IsTrue(error.Message.StartsWith(
                $"Proposal failed to validate: set_contract_runtime_options_enable_{optionName} " +
                $"at position 0 failed validation: Error: Action can only be proposed once " +
                $"contract '{contractId}' has been accepted."));
        }

        using (HttpRequestMessage request = new(HttpMethod.Post, disableUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("ProposalFailedToValidate", error.Code);
            Assert.IsTrue(error.Message.StartsWith(
                $"Proposal failed to validate: set_contract_runtime_options_disable_{optionName} " +
                $"at position 0 failed validation: Error: Action can only be proposed once " +
                $"contract '{contractId}' has been accepted."));
        }

        var contractContent = new JsonObject
        {
            ["data"] = "hello world"
        };

        // Add a contract to start with.
        using (HttpRequestMessage request = new(HttpMethod.Put, contractUrl))
        {
            request.Content = new StringContent(
                contractContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
        Assert.AreEqual(nameof(ContractState.Draft), contract[StateKey]!.ToString());
        Assert.AreEqual(contractContent["data"]!.ToString(), contract["data"]!.ToString());
        var version = contract[VersionKey]!.ToString();

        // Create a proposal for the above contract.
        string proposalId;
        using (HttpRequestMessage request = new(HttpMethod.Post, $"contracts/{contractId}/propose"))
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

        contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
        Assert.AreEqual(nameof(ContractState.Proposed), contract[StateKey]!.ToString());
        Assert.AreEqual(proposalId, contract[ProposalIdKey]!.ToString());
        Assert.AreEqual(contractContent["data"]!.ToString(), contract["data"]!.ToString());

        // Member0: Vote on the above proposal by accepting it.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"contracts/{contractId}/vote_accept"))
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

        // As its a 3 member system the option status check should still fail as contract is not
        // yet accepted.
        using (HttpRequestMessage request = new(HttpMethod.Post, checkOptionStatusUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("disabled", statusResponse.Status);
            Assert.AreEqual("ContractNotAccepted", statusResponse.Reason.Code);
            Assert.AreEqual(
                "Contract does not exist or has not been accepted.",
                statusResponse.Reason.Message);
        }

        // All remaining members vote on the above contract by accepting it.
        foreach (var client in this.CgsClients[1..])
        {
            await this.MemberAcceptContract(client, contractId, proposalId);
        }

        // As all members accepted the contract the status api should now succeed. By default
        // the behavior is 'disabled' unless explicitly enabled.
        using (HttpRequestMessage request = new(HttpMethod.Post, checkOptionStatusUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("disabled", statusResponse.Status);
            Assert.IsNull(statusResponse.Reason);
        }

        // Propose and accept a clean room policy so that consent check api starts to work.
        using (HttpRequestMessage request = new(HttpMethod.Post, consentCheckUrl))
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

        // As all members accepted the contract consent check api should now succeed. By default
        // the behavior is 'disabled' unless explicitly enabled.
        using (HttpRequestMessage request = new(HttpMethod.Post, consentCheckUrl))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("disabled", statusResponse.Status);
            Assert.IsNull(statusResponse.Reason);
        }

        // Enabling as member0.
        using (HttpRequestMessage request = new(HttpMethod.Post, enableUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            proposalId = responseBody[ProposalIdKey]!.ToString();
        }

        // Member0: Vote on the above proposal by accepting it.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"proposals/{proposalId}/ballots/vote_accept"))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // Status should now remain disabled as all members need to vote.
        using (HttpRequestMessage request = new(HttpMethod.Post, checkOptionStatusUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("disabled", statusResponse.Status);
        }

        // Consent check should continue to report disabled.
        using (HttpRequestMessage request = new(HttpMethod.Post, consentCheckUrl))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("disabled", statusResponse.Status);
        }

        // All remaining members vote on the above proposal by accepting it.
        foreach (var client in this.CgsClients[1..])
        {
            await this.MemberAcceptProposal(client, proposalId);
        }

        // As all members voted accepted the status should become enabled.
        using (HttpRequestMessage request = new(HttpMethod.Post, checkOptionStatusUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("enabled", statusResponse.Status);
            Assert.IsNull(statusResponse.Reason);
        }

        // As all members voted accepted the consent check status should become enabled.
        using (HttpRequestMessage request = new(HttpMethod.Post, consentCheckUrl))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("enabled", statusResponse.Status);
            Assert.IsNull(statusResponse.Reason);
        }

        // Disabling the option by any one member should pass. No voting is required.
        using (HttpRequestMessage request = new(HttpMethod.Post, disableUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // status API should report disabled.
        using (HttpRequestMessage request = new(HttpMethod.Post, checkOptionStatusUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("disabled", statusResponse.Status);
            Assert.IsNull(statusResponse.Reason);
        }

        // Consent check API should report disabled.
        using (HttpRequestMessage request = new(HttpMethod.Post, consentCheckUrl))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("disabled", statusResponse.Status);
            Assert.IsNull(statusResponse.Reason);
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