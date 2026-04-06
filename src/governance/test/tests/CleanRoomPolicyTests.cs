// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Test;

[TestClass]
public class CleanRoomPolicyTests : TestBase
{
    [TestMethod]
    public async Task ProposeCleanRoomPolicyInvalidScenarios()
    {
        // Proposing a policy for a non-existent contract should fail.
        string contractId = Guid.NewGuid().ToString().Substring(0, 8);
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"contracts/{contractId}/cleanroompolicy/propose"))
        {
            var proposalContent = new JsonObject
            {
                ["type"] = "add",
                ["policyType"] = "snp-caci",
                ["contractId"] = contractId,
                ["claims"] = new JsonObject
                {
                    ["x-ms-sevsnpvm-is-debuggable"] = false,
                    ["x-ms-sevsnpvm-hostdata"] =
                        "73973b78d70cc68353426de188db5dfc57e5b766e399935fb73a61127ea26d20"
                }
            };

            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("ProposalFailedToValidate", error.Code);
            Assert.Contains(
                "Proposal failed to validate: set_clean_room_policy at position 0 failed " +
                "validation: Error: Clean Room Policy can only be proposed once contract " +
                $"'{contractId}' has been accepted.",
                error.Message);
        }
    }
}