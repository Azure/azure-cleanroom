// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Test;

[TestClass]
public class EventTests : TestBase
{
    [TestMethod]
    public async Task InsertAndCheckEvents()
    {
        string contractId = this.ContractId;
        string scope = "contracts";
        string getEventsUrl = $"contracts/{contractId}/events?id={contractId}&scope={scope}";
        string putEventsUrl = $"/events?id={contractId}&scope={scope}";

        // Event should not be found as we have not added it yet.
        using (HttpRequestMessage request = new(HttpMethod.Get, getEventsUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(0, responseBody["value"]!.AsArray().Count);
        }

        var eventsToInsert = new List<JsonObject>
        {
            new()
            {
                ["message"] = $"Contract {contractId} passed consent check."
            },
            new()
            {
                ["message"] = $"foo container started for contract {contractId}."
            },
            new()
            {
                ["message"] = $"Key was released under contract {contractId}."
            },
            new()
            {
                ["message"] = $"foo container finished execution."
            },
        };

        // Adding an event directly using the CCF client without presenting any attestation report
        // should fail.
        using (HttpRequestMessage request = new(HttpMethod.Put, "app/" + getEventsUrl))
        {
            request.Content = new StringContent(
                eventsToInsert[0].ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("AttestationMissing", error.Code);
            Assert.AreEqual(
                "Attestation payload must be supplied.",
                error.Message);
        }

        // Before we can insert events a clean room policy for the contract must exist.
        // Event insertion should fail till we add that first.
        using (HttpRequestMessage request = new(HttpMethod.Put, putEventsUrl))
        {
            request.Content = new StringContent(
                eventsToInsert[0].ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifySnpAttestationFailed", error.Code);
            Assert.AreEqual(
                "The clean room policy is missing. Please propose a new clean room policy.",
                error.Message);
        }

        await this.ProposeContractAndAcceptAllowAllCleanRoomPolicy(contractId);

        // As the contract and clean room policy was proposed and accepted above event insertion
        // should now succeed.
        using (HttpRequestMessage request = new(HttpMethod.Put, putEventsUrl))
        {
            request.Content = new StringContent(
                eventsToInsert[0].ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.TryGetValues("x-ms-ccf-transaction-id", out var values));
            Assert.IsNotNull(values);
        }

        using (HttpRequestMessage request = new(HttpMethod.Get, getEventsUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(1, responseBody["value"]!.AsArray().Count);
            Assert.AreEqual(
                eventsToInsert[0].ToJsonString(),
                responseBody["value"]!.AsArray()[0]!["data"]!.ToJsonString());

            // Check that timestamp value is valid.
            var timestamp = responseBody["value"]!.AsArray()[0]!["timestamp"]!.ToString();
            DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(timestamp));
            var timestamp_iso = responseBody["value"]!.AsArray()[0]!["timestamp_iso"]!.ToString();
            var isoTs = DateTimeOffset.Parse(timestamp_iso);
            Assert.AreEqual(DateTime.UtcNow.Date, isoTs.Date);
        }

        // Add all the remaining events.
        for (int index = 1; index < eventsToInsert.Count; index++)
        {
            using (HttpRequestMessage request = new(HttpMethod.Put, putEventsUrl))
            {
                request.Content = new StringContent(
                    eventsToInsert[index].ToJsonString(),
                    Encoding.UTF8,
                    "application/json");

                using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }
        }

        // A get with no sequence number filter should get the latest/last event.
        int lastSeqNo;
        using (HttpRequestMessage request = new(HttpMethod.Get, getEventsUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(1, responseBody["value"]!.AsArray().Count);
            Assert.AreEqual(
                eventsToInsert.Last().ToJsonString(),
                responseBody["value"]!.AsArray()[0]!["data"]!.ToJsonString());
            lastSeqNo = Convert.ToInt32(responseBody["value"]!.AsArray()[0]!["seqno"]!.ToString());
            Assert.IsGreaterThan(0, lastSeqNo);

            // Check that timestamp value is valid.
            var timestamp = responseBody["value"]!.AsArray()[0]!["timestamp"]!.ToString();
            DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(timestamp));
        }

        // A get with from_seqno as the lastSeqNo should get the latest event as we have not
        // added any more events yet.
        using (HttpRequestMessage request =
            new(HttpMethod.Get, getEventsUrl + $"&from_seqno={lastSeqNo}"))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(1, responseBody["value"]!.AsArray().Count);
            Assert.AreEqual(
                eventsToInsert.Last().ToJsonString(),
                responseBody["value"]!.AsArray()[0]!["data"]!.ToJsonString());

            // Check that timestamp value is valid.
            var timestamp = responseBody["value"]!.AsArray()[0]!["timestamp"]!.ToString();
            DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(timestamp));
        }

        // A get with from_seqno=1 should get all five events.
        using (HttpRequestMessage request = new(HttpMethod.Get, getEventsUrl + "&from_seqno=1"))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(eventsToInsert.Count, responseBody["value"]!.AsArray().Count);
            for (int index = 0; index < eventsToInsert.Count; index++)
            {
                Assert.AreEqual(
                    eventsToInsert[index].ToJsonString(),
                    responseBody["value"]!.AsArray()[index]!["data"]!.ToJsonString());

                // Check that timestamp value is valid.
                var timestamp = responseBody["value"]!.AsArray()[index]!["timestamp"]!.ToString();
                DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(timestamp));
            }
        }

        // A get with from_seqno=1 and max_seqno_per_page=2 should lead to pagination and
        // eventually all 5 should get returned.
        List<JsonObject> paginatedEvents = new();
        int numIterations = 0;
        string? nextLink = getEventsUrl + "&from_seqno=1";
        do
        {
            using (HttpRequestMessage request =
                new(HttpMethod.Get, nextLink + "&max_seqno_per_page=2"))
            {
                using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
                if (responseBody["value"]!.AsArray().Any())
                {
                    foreach (var item in responseBody["value"]!.AsArray())
                    {
                        paginatedEvents.Add(item!.AsObject());
                    }
                }

                nextLink = responseBody["nextLink"]?.ToString();
                numIterations++;
            }
        }
        while (nextLink != null);

        Assert.IsGreaterThan(1, numIterations);
        Assert.HasCount(eventsToInsert.Count, paginatedEvents);
        for (int index = 0; index < eventsToInsert.Count; index++)
        {
            Assert.AreEqual(
                eventsToInsert[index].ToJsonString(),
                paginatedEvents[index]!["data"]!.ToJsonString());
        }
    }

    [TestMethod]
    public async Task InsertAndCheckEventsUsingJwtLocalIdpAuthPolicy()
    {
        string contractId = this.ContractId;
        string scope = "contracts";
        string getEventsUrl = $"contracts/{contractId}/events?id={contractId}&scope={scope}";
        string putEventsUrl = $"/events?id={contractId}&scope={scope}";

        // Event should not be found as we have not added it yet.
        using (HttpRequestMessage request = new(HttpMethod.Get, getEventsUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(0, responseBody["value"]!.AsArray().Count);
        }

        var eventsToInsert = new List<JsonObject>
        {
            new()
            {
                ["message"] = $"Contract {contractId} passed consent check."
            }
        };

        // Adding an event directly using the CCF client without presenting any attestation report
        // should fail.
        using (HttpRequestMessage request = new(HttpMethod.Put, "app/" + getEventsUrl))
        {
            request.Content = new StringContent(
                eventsToInsert[0].ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("AttestationMissing", error.Code);
            Assert.AreEqual(
                "Attestation payload must be supplied.",
                error.Message);
        }

        // Before we can insert events a clean room policy for the contract must exist.
        // Event insertion should fail till we add that first.
        using (HttpRequestMessage request = new(HttpMethod.Put, putEventsUrl))
        {
            request.Content = new StringContent(
                eventsToInsert[0].ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response =
                await this.GovSidecarJwtLocalIdpClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifyJwtAttestationFailed", error.Code);
            Assert.AreEqual(
                "The clean room policy is missing. Please propose a new clean room policy.",
                error.Message);
        }

        await this.ProposeContractAndAcceptLocalIdpJwtCleanRoomPolicy(contractId);

        // As the contract and clean room policy was proposed and accepted above event insertion
        // should now succeed.
        using (HttpRequestMessage request = new(HttpMethod.Put, putEventsUrl))
        {
            request.Content = new StringContent(
                eventsToInsert[0].ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response =
                await this.GovSidecarJwtLocalIdpClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.TryGetValues("x-ms-ccf-transaction-id", out var values));
            Assert.IsNotNull(values);
        }

        using (HttpRequestMessage request = new(HttpMethod.Get, getEventsUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(1, responseBody["value"]!.AsArray().Count);
            Assert.AreEqual(
                eventsToInsert[0].ToJsonString(),
                responseBody["value"]!.AsArray()[0]!["data"]!.ToJsonString());

            // Check that timestamp value is valid.
            var timestamp = responseBody["value"]!.AsArray()[0]!["timestamp"]!.ToString();
            DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(timestamp));
            var timestamp_iso = responseBody["value"]!.AsArray()[0]!["timestamp_iso"]!.ToString();
            var isoTs = DateTimeOffset.Parse(timestamp_iso);
            Assert.AreEqual(DateTime.UtcNow.Date, isoTs.Date);
        }

        // Replacing the oid/tid in the policy should cause event insertion to fail.
        await this.ProposeAndAcceptRemoveLocalIdpJwtCleanRoomPolicy(contractId);
        await this.ProposeAndAcceptJwtCleanRoomPolicy(contractId, "1234", "5678");
        using (HttpRequestMessage request = new(HttpMethod.Put, putEventsUrl))
        {
            request.Content = new StringContent(
                eventsToInsert[0].ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response =
                await this.GovSidecarJwtLocalIdpClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifyJwtAttestationFailed", error.Code);
            Assert.AreEqual(
                "Attestation claims do not match the contract policy, or the delegated policies.",
                error.Message);
        }
    }

    [TestMethod]
    public async Task InsertAndCheckEventsUsingCvmSnpAuthPolicy()
    {
        string contractId = this.ContractId;
        string scope = "contracts";
        string getEventsUrl = $"contracts/{contractId}/events?id={contractId}&scope={scope}";
        string putEventsUrl = $"/events?id={contractId}&scope={scope}";

        // Event should not be found as we have not added it yet.
        using (HttpRequestMessage request = new(HttpMethod.Get, getEventsUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(0, responseBody["value"]!.AsArray().Count);
        }

        var eventsToInsert = new List<JsonObject>
        {
            new()
            {
                ["message"] = $"Contract {contractId} passed consent check."
            }
        };

        // Before we can insert events a clean room policy for the contract must exist.
        // Event insertion should fail till we add that first.
        using (HttpRequestMessage request = new(HttpMethod.Put, putEventsUrl))
        {
            request.Content = new StringContent(
                eventsToInsert[0].ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response =
                await this.GovSidecarCvmClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifySnpAttestationFailed", error.Code);
            Assert.AreEqual(
                "The clean room policy is missing. Please propose a new clean room policy.",
                error.Message);
        }

        // Propose and accept a CVM SNP clean room policy with PCR values 4 and 7.
        await this.ProposeContractAndAcceptCvmSnpCleanRoomPolicy(contractId);

        // As the contract and clean room policy was proposed and accepted above event insertion
        // should now succeed.
        using (HttpRequestMessage request = new(HttpMethod.Put, putEventsUrl))
        {
            request.Content = new StringContent(
                eventsToInsert[0].ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response =
                await this.GovSidecarCvmClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.TryGetValues("x-ms-ccf-transaction-id", out var values));
            Assert.IsNotNull(values);
        }

        using (HttpRequestMessage request = new(HttpMethod.Get, getEventsUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(1, responseBody["value"]!.AsArray().Count);
            Assert.AreEqual(
                eventsToInsert[0].ToJsonString(),
                responseBody["value"]!.AsArray()[0]!["data"]!.ToJsonString());

            // Check that timestamp value is valid.
            var timestamp = responseBody["value"]!.AsArray()[0]!["timestamp"]!.ToString();
            DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(timestamp));
            var timestamp_iso = responseBody["value"]!.AsArray()[0]!["timestamp_iso"]!.ToString();
            var isoTs = DateTimeOffset.Parse(timestamp_iso);
            Assert.AreEqual(DateTime.UtcNow.Date, isoTs.Date);
        }

        // Replacing the PCR claims with wrong values should cause event insertion to fail.
        await this.ProposeAndAcceptRemoveCvmSnpCleanRoomPolicy(contractId);
        var wrongPcrClaims = new JsonObject
        {
            ["type"] = "add",
            ["policyType"] = "snp-cvm",
            ["contractId"] = contractId,
            ["claims"] = new JsonObject
            {
                ["pcr4"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                ["pcr7"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
            }
        };
        await this.ProposeAndAcceptContractProposal(
            contractId,
            "cleanroompolicy",
            wrongPcrClaims);
        using (HttpRequestMessage request = new(HttpMethod.Put, putEventsUrl))
        {
            request.Content = new StringContent(
                eventsToInsert[0].ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response =
                await this.GovSidecarCvmClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifySnpAttestationFailed", error.Code);
            Assert.AreEqual(
                "Attestation claims do not match the contract policy, or the delegated policies.",
                error.Message);
        }
    }

    [TestMethod]
    public async Task InsertAndCheckEventsUsingJwtAzureLoginAuthPolicy()
    {
        string contractId = this.ContractId;
        string scope = "contracts";
        string getEventsUrl = $"contracts/{contractId}/events?id={contractId}&scope={scope}";
        string putEventsUrl = $"/events?id={contractId}&scope={scope}";

        // Event should not be found as we have not added it yet.
        using (HttpRequestMessage request = new(HttpMethod.Get, getEventsUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(0, responseBody["value"]!.AsArray().Count);
        }

        var eventsToInsert = new List<JsonObject>
        {
            new()
            {
                ["message"] = $"Contract {contractId} passed consent check."
            }
        };

        // Adding an event directly using the CCF client without presenting any attestation report
        // should fail.
        using (HttpRequestMessage request = new(HttpMethod.Put, "app/" + getEventsUrl))
        {
            request.Content = new StringContent(
                eventsToInsert[0].ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("AttestationMissing", error.Code);
            Assert.AreEqual(
                "Attestation payload must be supplied.",
                error.Message);
        }

        // Before we can insert events a clean room policy for the contract must exist.
        // Event insertion should fail till we add that first.
        using (HttpRequestMessage request = new(HttpMethod.Put, putEventsUrl))
        {
            request.Content = new StringContent(
                eventsToInsert[0].ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response =
                await this.GovSidecarJwtAzureLoginClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifyJwtAttestationFailed", error.Code);
            Assert.AreEqual(
                "The clean room policy is missing. Please propose a new clean room policy.",
                error.Message);
        }

        // Extract oid and tid from the Azure Login JWT used by the GovSidecarJwtAzureLoginClient.
        string resource = "https://management.azure.com/.default";
        string oid;
        string tid;
        using (var response = await this.CredsProxyClient.GetAsync($"/token?resource={resource}"))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var tokenResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
            string jwt = tokenResponse!["access_token"]!.ToString();
            var jwtParts = jwt.Split('.');
            var jwtPayloadObj =
                JsonSerializer.Deserialize<JsonObject>(Base64UrlEncoder.Decode(jwtParts[1]))!;
            oid = jwtPayloadObj["oid"]!.ToString();
            tid = jwtPayloadObj["tid"]!.ToString();
        }

        await this.ProposeContractAndAcceptJwtCleanRoomPolicy(contractId, oid, tid);

        // As the contract and clean room policy was proposed and accepted above event insertion
        // should now succeed.
        using (HttpRequestMessage request = new(HttpMethod.Put, putEventsUrl))
        {
            request.Content = new StringContent(
                eventsToInsert[0].ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response =
                await this.GovSidecarJwtAzureLoginClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.TryGetValues("x-ms-ccf-transaction-id", out var values));
            Assert.IsNotNull(values);
        }

        using (HttpRequestMessage request = new(HttpMethod.Get, getEventsUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(1, responseBody["value"]!.AsArray().Count);
            Assert.AreEqual(
                eventsToInsert[0].ToJsonString(),
                responseBody["value"]!.AsArray()[0]!["data"]!.ToJsonString());

            // Check that timestamp value is valid.
            var timestamp = responseBody["value"]!.AsArray()[0]!["timestamp"]!.ToString();
            DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(timestamp));
            var timestamp_iso = responseBody["value"]!.AsArray()[0]!["timestamp_iso"]!.ToString();
            var isoTs = DateTimeOffset.Parse(timestamp_iso);
            Assert.AreEqual(DateTime.UtcNow.Date, isoTs.Date);
        }

        // Replacing the oid/tid in the policy should cause event insertion to fail.
        await this.ProposeAndAcceptRemoveJwtCleanRoomPolicy(contractId, oid, tid);
        await this.ProposeAndAcceptJwtCleanRoomPolicy(contractId, "1234", "5678");
        using (HttpRequestMessage request = new(HttpMethod.Put, putEventsUrl))
        {
            request.Content = new StringContent(
                eventsToInsert[0].ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response =
                await this.GovSidecarJwtAzureLoginClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifyJwtAttestationFailed", error.Code);
            Assert.AreEqual(
                "Attestation claims do not match the contract policy, or the delegated policies.",
                error.Message);
        }
    }

    [TestMethod]
    public async Task InsertAndCheckEventsNoIdInQueryParam()
    {
        string contractId = this.ContractId;
        string scope = "contracts";

        // Not specifying "&id=<something>" should insert the event with id as
        // the contractId.
        string eventsUrl = $"contracts/{contractId}/events?scope={scope}";
        await this.InsertAndCheckEventsQueryParamVariations(
            contractId,
            expectedId: contractId,
            expectedScope: scope,
            eventsUrl);
    }

    [TestMethod]
    public async Task InsertAndCheckEventsNoIdNoScopeInQueryParam()
    {
        string contractId = this.ContractId;

        // Not specifying "&id=<something>&scope=<something>" should insert the event with id as
        // the contractId.
        string eventsUrl = $"contracts/{contractId}/events";
        await this.InsertAndCheckEventsQueryParamVariations(
            contractId,
            expectedId: contractId,
            expectedScope: string.Empty,
            eventsUrl);
    }

    [TestMethod]
    public async Task InsertAndCheckEventsContractIdDifferentFromIdInQueryParam()
    {
        string contractId = this.ContractId;
        string id = "SomethingDifferent";
        string eventsUrl = $"contracts/{contractId}/events?&id={id}";
        await this.InsertAndCheckEventsQueryParamVariations(
            contractId,
            expectedId: id,
            expectedScope: string.Empty,
            eventsUrl);
    }

    public async Task InsertAndCheckEventsQueryParamVariations(
        string contractId,
        string expectedId,
        string expectedScope,
        string eventsUrl)
    {
        var eventsToInsert = new List<JsonObject>
        {
            new()
            {
                ["message"] = $"Contract {contractId} passed consent check."
            },
            new()
            {
                ["message"] = $"foo container started for contract {contractId}."
            },
            new()
            {
                ["message"] = $"Key was released under contract {contractId}."
            },
            new()
            {
                ["message"] = $"foo container finished execution."
            },
        };

        await this.ProposeContractAndAcceptAllowAllCleanRoomPolicy(contractId);

        // As the contract and clean room policy was proposed and accepted above event insertion
        // should now succeed.
        string putEventsUrl = eventsUrl.Remove(0, $"contracts/{contractId}".Length);
        using (HttpRequestMessage request = new(HttpMethod.Put, putEventsUrl))
        {
            request.Content = new StringContent(
                eventsToInsert[0].ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.TryGetValues("x-ms-ccf-transaction-id", out var values));
            Assert.IsNotNull(values);
        }

        using (HttpRequestMessage request = new(HttpMethod.Get, eventsUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(1, responseBody["value"]!.AsArray().Count);
            Assert.AreEqual(
                eventsToInsert[0].ToJsonString(),
                responseBody["value"]!.AsArray()[0]!["data"]!.ToJsonString());
            Assert.AreEqual(
                expectedId,
                responseBody["value"]!.AsArray()[0]!["id"]!.ToString());
            Assert.AreEqual(
                expectedScope,
                responseBody["value"]!.AsArray()[0]!["scope"]!.ToString());

            // Check that timestamp value is valid.
            var timestamp = responseBody["value"]!.AsArray()[0]!["timestamp"]!.ToString();
            DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(timestamp));
            var timestamp_iso = responseBody["value"]!.AsArray()[0]!["timestamp_iso"]!.ToString();
            var isoTs = DateTimeOffset.Parse(timestamp_iso);
            Assert.AreEqual(DateTime.UtcNow.Date, isoTs.Date);
        }

        // Add all the remaining events.
        for (int index = 1; index < eventsToInsert.Count; index++)
        {
            using (HttpRequestMessage request = new(HttpMethod.Put, putEventsUrl))
            {
                request.Content = new StringContent(
                    eventsToInsert[index].ToJsonString(),
                    Encoding.UTF8,
                    "application/json");

                using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }
        }

        // A get with no sequence number filter should get the latest/last event.
        int lastSeqNo;
        using (HttpRequestMessage request = new(HttpMethod.Get, eventsUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(1, responseBody["value"]!.AsArray().Count);
            Assert.AreEqual(
                eventsToInsert.Last().ToJsonString(),
                responseBody["value"]!.AsArray()[0]!["data"]!.ToJsonString());
            lastSeqNo = Convert.ToInt32(responseBody["value"]!.AsArray()[0]!["seqno"]!.ToString());
            Assert.IsGreaterThan(0, lastSeqNo);

            // Check that timestamp value is valid.
            var timestamp = responseBody["value"]!.AsArray()[0]!["timestamp"]!.ToString();
            DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(timestamp));
        }

        // A get with from_seqno as the lastSeqNo should get the latest event as we have not
        // added any more events yet.
        using (HttpRequestMessage request =
            new(HttpMethod.Get, eventsUrl + QuestionMark(eventsUrl) + $"&from_seqno={lastSeqNo}"))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(1, responseBody["value"]!.AsArray().Count);
            Assert.AreEqual(
                eventsToInsert.Last().ToJsonString(),
                responseBody["value"]!.AsArray()[0]!["data"]!.ToJsonString());

            // Check that timestamp value is valid.
            var timestamp = responseBody["value"]!.AsArray()[0]!["timestamp"]!.ToString();
            DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(timestamp));
        }

        // A get with from_seqno=1 should get all five events.
        using (HttpRequestMessage request =
            new(HttpMethod.Get, eventsUrl + QuestionMark(eventsUrl) + "&from_seqno=1"))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(eventsToInsert.Count, responseBody["value"]!.AsArray().Count);
            for (int index = 0; index < eventsToInsert.Count; index++)
            {
                Assert.AreEqual(
                    eventsToInsert[index].ToJsonString(),
                    responseBody["value"]!.AsArray()[index]!["data"]!.ToJsonString());

                // Check that timestamp value is valid.
                var timestamp = responseBody["value"]!.AsArray()[index]!["timestamp"]!.ToString();
                DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(timestamp));
            }
        }

        // A get with from_seqno=1 and max_seqno_per_page=2 should lead to pagination and
        // eventually all 5 should get returned.
        List<JsonObject> paginatedEvents = new();
        int numIterations = 0;
        string? nextLink = eventsUrl + QuestionMark(eventsUrl) + "&from_seqno=1";
        do
        {
            using (HttpRequestMessage request =
                new(HttpMethod.Get, nextLink + "&max_seqno_per_page=20"))
            {
                using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
                if (responseBody["value"]!.AsArray().Any())
                {
                    foreach (var item in responseBody["value"]!.AsArray())
                    {
                        paginatedEvents.Add(item!.AsObject());
                    }
                }

                nextLink = responseBody["nextLink"]?.ToString();
                numIterations++;
            }
        }
        while (nextLink != null);

        Assert.IsGreaterThan(1, numIterations);
        Assert.HasCount(eventsToInsert.Count, paginatedEvents);
        for (int index = 0; index < eventsToInsert.Count; index++)
        {
            Assert.AreEqual(
                eventsToInsert[index].ToJsonString(),
                paginatedEvents[index]!["data"]!.ToJsonString());
        }

        static string QuestionMark(string url)
        {
            return url.Contains("?") ? string.Empty : "?";
        }
    }

    [TestMethod]
    public async Task InsertEventsWithoutAuthChecks()
    {
        string contractId = this.ContractId;
        string scope = "contracts";
        string ccfAppEventsUrl = $"app/contracts/{contractId}/events?id={contractId}&scope={scope}";

        // Adding an event directly using the CCF client without presenting expected auth info
        // should fail.
        using (HttpRequestMessage request = new(HttpMethod.Put, ccfAppEventsUrl))
        {
            // Payload does not contain attestation.
            request.Content = new StringContent(
                "{}",
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("AttestationMissing", error.Code);
            Assert.AreEqual(
                "Attestation payload must be supplied.",
                error.Message);
        }

        using (HttpRequestMessage request = new(HttpMethod.Put, ccfAppEventsUrl))
        {
            // Payload does not contain signature.
            request.Content = new StringContent(
                new JsonObject
                {
                    ["attestation"] = "doesnotmatter"
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("SignatureMissing", error.Code);
            Assert.AreEqual(
                "Signature payload must be supplied.",
                error.Message);
        }

        using (HttpRequestMessage request = new(HttpMethod.Put, ccfAppEventsUrl))
        {
            // Payload does not contain timestamp.
            request.Content = new StringContent(
                new JsonObject
                {
                    ["attestation"] = "invalidinput",
                    ["sign"] = "doesnotmatter"
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("TimestampMissing", error.Code);
        }

        using (HttpRequestMessage request = new(HttpMethod.Put, ccfAppEventsUrl))
        {
            // Payload contains timestamp that is too large.
            var ts = DateTimeOffset.FromUnixTimeMilliseconds(9999999999999)
                .AddMonths(1);
            Assert.AreEqual(2286, ts.Year);
            Assert.AreEqual(12, ts.Month);
            request.Content = new StringContent(
                new JsonObject
                {
                    ["attestation"] = "invalidinput",
                    ["sign"] = "doesnotmatter",
                    ["timestamp"] = ts.ToUnixTimeMilliseconds().ToString()
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("TimestampTooLarge", error.Code);
        }

        using (HttpRequestMessage request = new(HttpMethod.Put, ccfAppEventsUrl))
        {
            // Payload contains invalid attestation report.
            request.Content = new StringContent(
                new JsonObject
                {
                    ["attestation"] = "invalidinput",
                    ["sign"] = "doesnotmatter",
                    ["timestamp"] = ((DateTimeOffset)DateTime.UtcNow).ToUnixTimeMilliseconds()
                        .ToString()
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifySnpAttestationFailed", error.Code);
            Assert.AreEqual("cannot read property 'replace' of undefined", error.Message);
        }

        using (HttpRequestMessage request = new(HttpMethod.Put, ccfAppEventsUrl))
        {
            // Payload contains valid attestation report but no clean room policy has been proposed
            // yet so event insertion should fail.
            var attestationReport = JsonSerializer.Deserialize<JsonObject>(
                await File.ReadAllTextAsync(
                    "data/encryption/attestation.json"))!["report"]!["snpCACI"]!;
            var publicKey = CreateX509Certificate2("foo").PublicKey.ExportSubjectPublicKeyInfo();
            var publicKeyPem = PemEncoding.Write("PUBLIC KEY", publicKey);
            request.Content = new StringContent(
                new JsonObject
                {
                    ["attestation"] = new JsonObject
                    {
                        ["evidence"] = attestationReport["attestation"]!.ToString(),
                        ["endorsements"] = attestationReport["platformCertificates"]!.ToString(),
                        ["uvm_endorsements"] = attestationReport["uvmEndorsements"]!.ToString(),
                    },
                    ["sign"] = new JsonObject
                    {
                        ["publicKey"] =
                            Convert.ToBase64String(Encoding.UTF8.GetBytes(publicKeyPem))
                    },
                    ["timestamp"] = ((DateTimeOffset)DateTime.UtcNow).ToUnixTimeMilliseconds()
                        .ToString()
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifySnpAttestationFailed", error.Code);
            Assert.AreEqual(
                "The clean room policy is missing. Please propose a new clean room policy.",
                error.Message);
        }

        // Propose and accept the contract and cleanroom policy before testing the next set
        // of scenarios.
        await this.ProposeContractAndAcceptAllowAllCleanRoomPolicy(contractId);

        using (HttpRequestMessage request = new(HttpMethod.Put, ccfAppEventsUrl))
        {
            // Payload contains valid attestation report but certificate does not match reportdata.
            var attestationReport = JsonSerializer.Deserialize<JsonObject>(
                await File.ReadAllTextAsync(
                    "data/encryption/attestation.json"))!["report"]!["snpCACI"]!;
            var publicKey = CreateX509Certificate2("foo").PublicKey.ExportSubjectPublicKeyInfo();
            var publicKeyPem = PemEncoding.Write("PUBLIC KEY", publicKey);
            request.Content = new StringContent(
                new JsonObject
                {
                    ["attestation"] = new JsonObject
                    {
                        ["evidence"] = attestationReport["attestation"]!.ToString(),
                        ["endorsements"] = attestationReport["platformCertificates"]!.ToString(),
                        ["uvm_endorsements"] = attestationReport["uvmEndorsements"]!.ToString(),
                    },
                    ["sign"] = new JsonObject
                    {
                        ["publicKey"] =
                            Convert.ToBase64String(Encoding.UTF8.GetBytes(publicKeyPem))
                    },
                    ["timestamp"] = ((DateTimeOffset)DateTime.UtcNow).ToUnixTimeMilliseconds()
                        .ToString()
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("ReportDataMismatch", error.Code);
            Assert.AreEqual(
                "Attestation report_data value did not match calculated value.",
                error.Message);
        }

        using (HttpRequestMessage request = new(HttpMethod.Put, ccfAppEventsUrl))
        {
            // Payload contains valid report, certificate and signature but data is not
            // corresponding to the signature.
            var attestationReport = JsonSerializer.Deserialize<JsonObject>(
                await File.ReadAllTextAsync(
                    "data/encryption/attestation.json"))!["report"]!["snpCACI"]!;
            var publicKey = (await File.ReadAllTextAsync("data/encryption/pub_key.pem"))!;

            // Replace CR+LF with LF for test to pass if run on Windows otherwise we get
            // ReportDataMismatch instead of SignatureMismatch.
            publicKey = publicKey.Replace("\r\n", "\n");

            var signature = SignData(
                Encoding.UTF8.GetBytes("DataToBeSigned"),
                await File.ReadAllTextAsync("data/encryption/priv_key.pem")!);
            request.Content = new StringContent(
                new JsonObject
                {
                    ["attestation"] = new JsonObject
                    {
                        ["evidence"] = attestationReport["attestation"]!.ToString(),
                        ["endorsements"] = attestationReport["platformCertificates"]!.ToString(),
                        ["uvm_endorsements"] = attestationReport["uvmEndorsements"]!.ToString(),
                    },
                    ["sign"] = new JsonObject
                    {
                        ["publicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(publicKey)),
                        ["signature"] = Convert.ToBase64String(signature)
                    },
                    ["timestamp"] = ((DateTimeOffset)DateTime.UtcNow).ToUnixTimeMilliseconds()
                        .ToString(),
                    ["data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("NotWhatWasSigned"))
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("SignatureMismatch", error.Code);
            Assert.AreEqual("Signature verification was not successful.", error.Message);
        }

        static X509Certificate2 CreateX509Certificate2(string certName)
        {
            var rsa = RSA.Create();
            var req = new CertificateRequest(
                $"cn={certName}",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var cert = req.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddYears(1));
            return cert;
        }

        static byte[] SignData(byte[] data, string signingKey)
        {
            using RSA privateKey = RSA.Create();
            privateKey.ImportFromPem(signingKey);
            byte[] signature = privateKey.SignData(
                data,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            return signature;
        }
    }

    [TestMethod]
    public async Task InsertAndCheckEventsWithDelegatePolicy()
    {
        string contractId = this.ContractId;
        string getEventsUrl = $"contracts/{contractId}/events?id={contractId}";
        string putEventsUrl = $"/events";

        await this.ProposeContractAndAcceptCleanRoomPolicy(contractId, this.GovSidecarHostData);

        var eventsToInsert = new List<JsonObject>
        {
            new()
            {
                ["message"] = $"Event 1: Contract {contractId} passed consent check."
            },
            new()
            {
                ["message"] = $"Event 2: Job execution started for contract {contractId}."
            },
            new()
            {
                ["message"] = $"Event 3: Key released under contract {contractId}."
            },
            new()
            {
                ["message"] = $"Event 4: Job execution for {contractId} completed successfully."
            },
            new()
            {
                ["message"] = $"Event 5: This should fail with removed delegate policy."
            },
        };

        string policyUrl = $"contracts/{contractId}/cleanroompolicy/delegates/events/writer";

        // Setup a delegate policy (hostData2) using ccr-governance running with the
        // contract level clean room policy (hostData1).
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"cleanroompolicy/delegates/events/writer"))
        {
            var addPolicy = new JsonObject
            {
                ["type"] = "add",
                ["policyType"] = "snp-caci",
                ["claims"] = new JsonObject
                {
                    ["x-ms-sevsnpvm-is-debuggable"] = false,
                    ["x-ms-sevsnpvm-hostdata"] = this.GovSidecar2HostData
                }
            };
            request.Content = new StringContent(
                addPolicy.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            // Check the get response matches with the policy that was set.
            using HttpRequestMessage request2 = new(HttpMethod.Get, policyUrl);
            using HttpResponseMessage response2 = await this.CgsClient_Member0.SendAsync(request2);
            Assert.AreEqual(HttpStatusCode.OK, response2.StatusCode);
            var responseBody = (await response2.Content.ReadFromJsonAsync<JsonObject>())!;
            JsonObject expectedClaims =
                this.ConvertClaimsToArrayFormat(addPolicy["claims"]!.AsObject());
            Assert.IsTrue(
                JsonNode.DeepEquals(expectedClaims, responseBody["claims"]),
                $"Expected: {expectedClaims.ToJsonString()}, " +
                $"actual: {responseBody["claims"]?.ToJsonString()}");
        }

        // The list API should return the above policy name.
        using (HttpRequestMessage request =
            new(HttpMethod.Get, $"contracts/{contractId}/cleanroompolicy/delegates"))
        {
            var expectedListReponse = new List<JsonObject>
            {
                new()
                {
                    ["delegateType"] = "events",
                    ["delegateId"] = "writer",
                },
            };
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            var listResponse = JsonSerializer.Deserialize<ListDelegatePoliciesReponse>(
                responseBody.ToString())!;
            Assert.HasCount(1, listResponse.Value);
            Assert.IsNotNull(
                listResponse.Value.Find(
                    x => x.DelegateType == "events" && x.DelegateId == "writer"),
                $"List API did not contain expected delegate: {responseBody}");
        }

        // Insert 1st event using contract level clean room policy (hostData1).
        using (HttpRequestMessage request = new(HttpMethod.Put, putEventsUrl))
        {
            request.Content = new StringContent(
                eventsToInsert[0].ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.TryGetValues("x-ms-ccf-transaction-id", out var values));
            Assert.IsNotNull(values);
        }

        // Verify the first event was inserted.
        using (HttpRequestMessage request = new(HttpMethod.Get, getEventsUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(1, responseBody["value"]!.AsArray().Count);
            Assert.AreEqual(
                eventsToInsert[0].ToJsonString(),
                responseBody["value"]!.AsArray()[0]!["data"]!.ToJsonString());
        }

        // Insert 2nd event using delegate policy (hostData2).
        using (HttpRequestMessage request = new(HttpMethod.Put, putEventsUrl))
        {
            request.Content = new StringContent(
                eventsToInsert[1].ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.GovSidecar2Client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.TryGetValues("x-ms-ccf-transaction-id", out var values));
            Assert.IsNotNull(values);
        }

        // Insert 3rd event using delegate policy (hostData2).
        using (HttpRequestMessage request = new(HttpMethod.Put, putEventsUrl))
        {
            request.Content = new StringContent(
                eventsToInsert[2].ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.GovSidecar2Client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.TryGetValues("x-ms-ccf-transaction-id", out var values));
            Assert.IsNotNull(values);
        }

        // Insert 4th event using contract level policy (hostData1) - should still work.
        using (HttpRequestMessage request = new(HttpMethod.Put, putEventsUrl))
        {
            request.Content = new StringContent(
                eventsToInsert[3].ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.TryGetValues("x-ms-ccf-transaction-id", out var values));
            Assert.IsNotNull(values);
        }

        // Verify we now have 4 events.
        using (HttpRequestMessage request = new(HttpMethod.Get, getEventsUrl + "&from_seqno=1"))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            this.Logger.LogInformation(
                $"Events after inserting 4 events with delegate policy: " +
                $"{responseBody.ToJsonString()}");
            Assert.AreEqual(4, responseBody["value"]!.AsArray().Count);
            for (int index = 0; index < 4; index++)
            {
                Assert.AreEqual(
                    eventsToInsert[index].ToJsonString(),
                    responseBody["value"]!.AsArray()[index]!["data"]!.ToJsonString());
            }
        }

        // Remove the delegate policy.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"cleanroompolicy/delegates/events/writer"))
        {
            request.Content = new StringContent(
                new JsonObject
                {
                    ["type"] = "remove",
                    ["claims"] = new JsonObject
                    {
                        ["x-ms-sevsnpvm-is-debuggable"] = false,
                        ["x-ms-sevsnpvm-hostdata"] = this.GovSidecar2HostData
                    }
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            // After removing the policy above check the get response by the user shows nothing.
            using HttpRequestMessage request2 = new(HttpMethod.Get, policyUrl);
            using HttpResponseMessage response2 = await this.CgsClient_Member0.SendAsync(request2);
            Assert.AreEqual(HttpStatusCode.OK, response2.StatusCode);
            var responseBody = (await response2.Content.ReadFromJsonAsync<JsonObject>())!;
            var claimsJson = responseBody["claims"]?.ToJsonString();
            Assert.AreEqual("{}", claimsJson, $"Expected empty claims {{}}, but got: {claimsJson}");
        }

        // Try to insert event using removed delegate policy (hostData2) - should fail.
        using (HttpRequestMessage request = new(HttpMethod.Put, putEventsUrl))
        {
            request.Content = new StringContent(
                eventsToInsert[4].ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.GovSidecar2Client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifySnpAttestationFailed", error.Code);
        }

        // Verify we still have only 4 events (5th event insertion failed).
        using (HttpRequestMessage request = new(HttpMethod.Get, getEventsUrl + "&from_seqno=1"))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(4, responseBody["value"]!.AsArray().Count);
            for (int index = 0; index < 4; index++)
            {
                Assert.AreEqual(
                    eventsToInsert[index].ToJsonString(),
                    responseBody["value"]!.AsArray()[index]!["data"]!.ToJsonString());
            }
        }

        // Contract level policy (hostData1) should still work after delegate removal.
        using (HttpRequestMessage request = new(HttpMethod.Put, putEventsUrl))
        {
            var finalEvent = new JsonObject
            {
                ["message"] = $"Event 5: Final event with contract policy after delegate removal."
            };
            request.Content = new StringContent(
                finalEvent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.TryGetValues("x-ms-ccf-transaction-id", out var values));
            Assert.IsNotNull(values);
        }

        // Verify we now have 5 events total.
        using (HttpRequestMessage request = new(HttpMethod.Get, getEventsUrl + "&from_seqno=1"))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.AreEqual(5, responseBody["value"]!.AsArray().Count);
        }
    }

    public record ListDelegatePoliciesReponse(
        [property: JsonPropertyName("value")] List<ListDelegatePolicyResponse> Value);

    public record ListDelegatePolicyResponse(
    [property: JsonPropertyName("delegateType")] string DelegateType,
    [property: JsonPropertyName("delegateId")] string DelegateId);
}