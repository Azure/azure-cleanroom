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
public class ContractProposalsTests : TestBase
{
    [TestMethod]
    public async Task CreateAndAcceptDeploymentSpec()
    {
        string contractId = this.ContractId;
        string specUrl = $"contracts/{contractId}/deploymentspec";
        await this.ProposeAndAcceptContract(contractId);
        var specInput = new JsonObject
        {
            ["armTemplate"] = "something"
        };
        await this.ProposeAndAcceptContractProposal(contractId, "deploymentspec", specInput);

        var spec = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(specUrl))!;
        Assert.AreEqual(specInput.ToJsonString(), spec["data"]!.ToJsonString());
    }

    [TestMethod]
    public async Task CreateAndAcceptDeploymentInfo()
    {
        string contractId = this.ContractId;
        string infoUrl = $"contracts/{contractId}/deploymentinfo";
        await this.ProposeAndAcceptContract(contractId);
        var infoInput = new JsonObject
        {
            ["armId"] = "something"
        };
        await this.ProposeAndAcceptContractProposal(contractId, "deploymentinfo", infoInput);

        var info = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(infoUrl))!;
        Assert.AreEqual(infoInput.ToJsonString(), info["data"]!.ToJsonString());
    }

    [TestMethod]
    public async Task ProposeDeploymentSpecInvalidScenarios()
    {
        // Proposing a policy for a non-existent contract should fail.
        string contractId = Guid.NewGuid().ToString().Substring(0, 8);
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"contracts/{contractId}/deploymentspec/propose"))
        {
            var proposalContent = new JsonObject
            {
                ["armTemplate"] = "something"
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
                "Proposal failed to validate: set_deployment_spec at position 0 failed " +
                "validation: Error: Deployment spec can only be proposed once contract " +
                $"'{contractId}' has been accepted.",
                error.Message);
        }
    }
}