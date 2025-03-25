// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Test;

[TestClass]
public class RoleTests : TestBase
{
    /// <summary>
    /// Initialize tests.
    /// </summary>
    [TestInitialize]
    public override void Initialize()
    {
        string? testConfigurationFile = Environment.GetEnvironmentVariable(
            "TEST_CONFIGURATION_FILE");

        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json")
            .AddEnvironmentVariables();

        if (!string.IsNullOrEmpty(testConfigurationFile))
        {
            configBuilder.AddJsonFile(testConfigurationFile);
        }

        this.Configuration = configBuilder.Build();

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
        });

        this.Logger = loggerFactory.CreateLogger<EventTests>();

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
            {
                return true;
            }
        };

        string contractId = Guid.NewGuid().ToString().Substring(0, 8);
        this.Logger.LogInformation($"contractId: {contractId}");
        this.ContractId = contractId;

        this.CcfClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(this.Configuration["roleTesting:ccfEndpoint"]!)
        };

        this.CgsClients = new List<HttpClient>();
        this.CgsClient_Member0 = new HttpClient(handler)
        {
            BaseAddress = new Uri(this.Configuration["roleTesting:cgsClientEndpoint_member0"]!)
        };
        this.CgsClients.Add(this.CgsClient_Member0);

        this.CgsClients.Add(new HttpClient(handler)
        {
            BaseAddress = new Uri(this.Configuration["roleTesting:cgsClientEndpoint_member1"]!)
        });

        this.CgsClients.Add(new HttpClient(handler)
        {
            BaseAddress = new Uri(this.Configuration["roleTesting:cgsClientEndpoint_member2"]!)
        });
    }

    [TestMethod]
    public async Task CheckContractOperatorActions()
    {
        string contractId = this.ContractId;
        await this.ProposeAndAcceptContract(contractId);

        // Enable the contract operator role for member1 along with enabling auto approval for
        // clean room policy/deployment spec proposals. This should result in member1's proposal
        // to get auto-accepted while any other member's proposal should remain open.

        // Enabling auto-approve deployment spec/clean room policy option.
        await this.SetRuntimeOption(RuntimeOption.AutoApproveDeploymentSpec, ActionName.Enable);
        await this.SetRuntimeOption(RuntimeOption.AutoApproveCleanRoomPolicy, ActionName.Enable);

        // Update member1 to become a contractOperator.
        await this.SetRole(Members.Member1, RoleName.ContractOperator, "true");

        // Now a clean room policy proposal from member0 should remain open while from member1
        // should get auto-accepted (no voting was required).
        string proposalId =
            await this.ProposeAllowAllCleanRoomPolicy(contractId, asMember: Members.Member0);
        var proposalResponse =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"proposals/{proposalId}"))!;
        Assert.AreEqual("Open", proposalResponse["proposalState"]!.ToString());

        proposalId =
            await this.ProposeAllowAllCleanRoomPolicy(contractId, asMember: Members.Member1);
        proposalResponse =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"proposals/{proposalId}"))!;
        Assert.AreEqual("Accepted", proposalResponse["proposalState"]!.ToString());

        // Similar behavior for deployment spec.
        var specInput = new JsonObject
        {
            ["armTemplate"] = "something"
        };
        proposalId =
            await this.ProposeDeploymentSpec(contractId, specInput, asMember: Members.Member0);
        proposalResponse =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"proposals/{proposalId}"))!;
        Assert.AreEqual("Open", proposalResponse["proposalState"]!.ToString());

        proposalId =
            await this.ProposeDeploymentSpec(contractId, specInput, asMember: Members.Member1);
        proposalResponse =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"proposals/{proposalId}"))!;
        Assert.AreEqual("Accepted", proposalResponse["proposalState"]!.ToString());

        // Update member1 to no longer be a cgsOperator.
        await this.SetRole(Members.Member1, RoleName.ContractOperator, "false");

        // Now a proposal from member1 should remain open as role was disabled.
        proposalId =
            await this.ProposeAllowAllCleanRoomPolicy(contractId, asMember: Members.Member1);
        proposalResponse =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"proposals/{proposalId}"))!;
        Assert.AreEqual("Open", proposalResponse["proposalState"]!.ToString());

        proposalId =
            await this.ProposeDeploymentSpec(contractId, specInput, asMember: Members.Member1);
        proposalResponse =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"proposals/{proposalId}"))!;
        Assert.AreEqual("Open", proposalResponse["proposalState"]!.ToString());

        // Update member1 to again become be a cgsOperator.
        await this.SetRole(Members.Member1, RoleName.ContractOperator, "true");

        // Disabling the auto-approve options.
        await this.SetRuntimeOption(RuntimeOption.AutoApproveDeploymentSpec, ActionName.Disable);
        await this.SetRuntimeOption(RuntimeOption.AutoApproveCleanRoomPolicy, ActionName.Disable);

        // Now a proposal from member1 should remain open as auto-approve was disabled.
        proposalId =
            await this.ProposeAllowAllCleanRoomPolicy(contractId, asMember: Members.Member1);
        proposalResponse =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"proposals/{proposalId}"))!;
        Assert.AreEqual("Open", proposalResponse["proposalState"]!.ToString());

        proposalId =
            await this.ProposeDeploymentSpec(contractId, specInput, asMember: Members.Member1);
        proposalResponse =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"proposals/{proposalId}"))!;
        Assert.AreEqual("Open", proposalResponse["proposalState"]!.ToString());

        // Cleanup role assignment.
        await this.SetRole(Members.Member1, RoleName.ContractOperator, "false");
    }

    [TestMethod]
    public async Task CheckCgsOperatorActions()
    {
        // Enable the cgs operator role for member2 along with enabling auto approval for
        // clean room policy proposals. This should result in member2's proposal to get
        // auto-accepted while any other member's proposal should remain open.

        // Enabling auto-approve constitution/jsapp proposals.
        await this.SetRuntimeOption(RuntimeOption.AutoApproveConstitution, ActionName.Enable);
        await this.SetRuntimeOption(RuntimeOption.AutoApproveJsApp, ActionName.Enable);

        // Update member2 to become a cgsOperator.
        await this.SetRole(Members.Member2, RoleName.CgsOperator, "true");

        // Now a JS app proposal from member0 should remain open while from member2
        // should get auto-accepted (no voting was required).
        string proposalId = await this.ProposeJsApp(asMember: Members.Member0);
        var proposalResponse =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"proposals/{proposalId}"))!;
        Assert.AreEqual("Open", proposalResponse["proposalState"]!.ToString());

        proposalId = await this.ProposeJsApp(asMember: Members.Member2);
        proposalResponse =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"proposals/{proposalId}"))!;
        Assert.AreEqual("Accepted", proposalResponse["proposalState"]!.ToString());

        // Similar behavior for set constitution.
        proposalId = await this.ProposeConstitution(asMember: Members.Member0);
        proposalResponse =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"proposals/{proposalId}"))!;
        Assert.AreEqual("Open", proposalResponse["proposalState"]!.ToString());

        proposalId = await this.ProposeConstitution(asMember: Members.Member2);
        proposalResponse =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"proposals/{proposalId}"))!;
        Assert.AreEqual("Accepted", proposalResponse["proposalState"]!.ToString());

        // Update member2 to no longer be a cgsOperator.
        await this.SetRole(Members.Member2, RoleName.CgsOperator, "false");

        // Now a proposal from member2 should remain open as role was disabled.
        proposalId = await this.ProposeJsApp(asMember: Members.Member2);
        proposalResponse =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"proposals/{proposalId}"))!;
        Assert.AreEqual("Open", proposalResponse["proposalState"]!.ToString());

        proposalId =
            await this.ProposeConstitution(asMember: Members.Member2);
        proposalResponse =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"proposals/{proposalId}"))!;
        Assert.AreEqual("Open", proposalResponse["proposalState"]!.ToString());

        // Update member2 to again become be a cgsOperator.
        await this.SetRole(Members.Member2, RoleName.CgsOperator, "true");

        // Disabling the auto-approve options.
        await this.SetRuntimeOption(RuntimeOption.AutoApproveJsApp, ActionName.Disable);
        await this.SetRuntimeOption(RuntimeOption.AutoApproveConstitution, ActionName.Disable);

        // Now a proposal from member2 should remain open as auto-approve was disabled.
        proposalId = await this.ProposeJsApp(asMember: Members.Member2);
        proposalResponse =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"proposals/{proposalId}"))!;
        Assert.AreEqual("Open", proposalResponse["proposalState"]!.ToString());

        proposalId =
            await this.ProposeConstitution(asMember: Members.Member2);
        proposalResponse =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"proposals/{proposalId}"))!;
        Assert.AreEqual("Open", proposalResponse["proposalState"]!.ToString());

        // Cleanup role assignment.
        await this.SetRole(Members.Member2, RoleName.CgsOperator, "false");
    }

    [TestMethod]
    [DataRow(RoleName.CgsOperator)]
    [DataRow(RoleName.ContractOperator)]
    public async Task CheckNonVotingOperations(string roleName)
    {
        // Set member2 to cgsOperator.
        await this.SetRole(Members.Member2, roleName, "true");

        string contractId = Guid.NewGuid().ToString().Substring(0, 8);
        string proposalId = await this.ProposeContract(contractId);

        // Vote as member0 and member1.
        await this.MemberAcceptProposal(this.CgsClients[Members.Member0], proposalId);
        await this.MemberAcceptProposal(this.CgsClients[Members.Member1], proposalId);

        // The proposal should be Accepted.
        var proposalResponse =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"proposals/{proposalId}"))!;
        Assert.AreEqual("Accepted", proposalResponse["proposalState"]!.ToString());

        // Only member0 and member1 should be counted in the final votes.
        var info = await this.CgsClients[Members.Member0].GetFromJsonAsync<JsonObject>("/show");
        string member0Id = info!["memberId"]!.ToString();

        info = await this.CgsClients[Members.Member1].GetFromJsonAsync<JsonObject>("/show");
        string member1Id = info!["memberId"]!.ToString();

        info = await this.CgsClients[Members.Member2].GetFromJsonAsync<JsonObject>("/show");
        string member2Id = info!["memberId"]!.ToString();

        Assert.AreEqual("true", proposalResponse["finalVotes"]![member0Id]!.ToString());
        Assert.AreEqual("true", proposalResponse["finalVotes"]![member1Id]!.ToString());
        Assert.IsNull(proposalResponse["finalVotes"]![member2Id]);

        // Remove role assignment.
        await this.SetRole(Members.Member2, roleName, "false");

        // Create a new contract.
        contractId = Guid.NewGuid().ToString().Substring(0, 8);
        proposalId = await this.ProposeContract(contractId);

        // Vote as member0 and member1.
        await this.MemberAcceptProposal(this.CgsClients[Members.Member0], proposalId);
        await this.MemberAcceptProposal(this.CgsClients[Members.Member1], proposalId);

        // The proposal should remain Open.
        proposalResponse =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"proposals/{proposalId}"))!;
        Assert.AreEqual("Open", proposalResponse["proposalState"]!.ToString());

        await this.MemberAcceptProposal(this.CgsClients[Members.Member2], proposalId);

        // The proposal should be Accepted.
        proposalResponse =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"proposals/{proposalId}"))!;
        Assert.AreEqual("Accepted", proposalResponse["proposalState"]!.ToString());

        // All 3 members should be counted in the final votes.
        Assert.AreEqual("true", proposalResponse["finalVotes"]![member0Id]!.ToString());
        Assert.AreEqual("true", proposalResponse["finalVotes"]![member1Id]!.ToString());
        Assert.AreEqual("true", proposalResponse["finalVotes"]![member2Id]!.ToString());
    }

    protected override async Task AllMembersAcceptProposal(string proposalId)
    {
        // Get the members needed to vote upfront so that any member state changes while
        // voting do not impact the voting process.
        var membersNeededToVote = await this.GetMembersNeededToVote();
        foreach (var member in membersNeededToVote)
        {
            await this.MemberAcceptProposal(this.CgsClients[member], proposalId);
        }
    }

    protected override async Task AllMembersAcceptContract(string contractId, string proposalId)
    {
        // Get the members needed to vote upfront so that any member state changes while
        // voting do not impact the voting process.
        var membersNeededToVote = await this.GetMembersNeededToVote();
        foreach (var member in membersNeededToVote)
        {
            await this.MemberAcceptContract(this.CgsClients[member], contractId, proposalId);
        }
    }

    private async Task SetRole(int asMember, string roleName, string value)
    {
        var info = await this.CgsClients[asMember].GetFromJsonAsync<JsonObject>("/show");
        string memberId = info!["memberId"]!.ToString();

        using (HttpRequestMessage request = new(HttpMethod.Post, "proposals/create"))
        {
            var proposalContent = new JsonObject
            {
                ["actions"] = new JsonArray
                {
                    {
                        new JsonObject
                        {
                            ["name"] = "set_member_data",
                            ["args"] = new JsonObject
                            {
                                ["member_id"] = memberId,
                                ["member_data"] = new JsonObject
                                {
                                    ["cgsRoles"] = new JsonObject
                                    {
                                        [roleName] = value
                                    }
                                }
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
            string proposalId = responseBody[ProposalIdKey]!.ToString();
            await this.AllMembersAcceptProposal(proposalId);

            info = await this.CgsClients[asMember].GetFromJsonAsync<JsonObject>("/show");
            Assert.AreEqual(
                value,
                info!["memberData"]!["cgsRoles"]![roleName]!.ToString());
        }
    }

    private async Task<List<int>> GetMembersNeededToVote()
    {
        List<int> membersNeededToVote = [];
        for (int i = 0; i < this.CgsClients.Count; i++)
        {
            var info = await this.CgsClients[i].GetFromJsonAsync<JsonObject>("/show");
            Assert.IsNotNull(info);

            if (!IsRole(info, RoleName.ContractOperator) &&
                !IsRole(info, RoleName.CgsOperator))
            {
                membersNeededToVote.Add(i);
            }
        }

        return membersNeededToVote;

        static bool IsRole(JsonObject memberInfo, string roleName)
        {
            var roles = memberInfo!["memberData"]!["cgsRoles"];

            // No cgs roles set.
            if (roles == null)
            {
                return false;
            }

            if (roles[roleName] != null)
            {
                string roleValue = roles[roleName]!.ToString();
                if (roleValue == "true")
                {
                    return true;
                }
                else if (roleValue == "false")
                {
                    return false;
                }

                throw new Exception($"Unexpected value for role '{roleName}': '{roleValue}'.");
            }

            return false;
        }
    }

    private async Task SetRuntimeOption(string optionName, string actionName)
    {
        string checkOptionStatusUrl = $"runtimeoptions/checkstatus/{optionName}";
        string setUrl = $"runtimeoptions/{optionName}/propose-{actionName}";

        string proposalId;
        using (HttpRequestMessage request = new(HttpMethod.Post, setUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            proposalId = responseBody[ProposalIdKey]!.ToString();
        }

        await this.AllMembersAcceptProposal(proposalId);

        // Status should now be reported as enabled.
        using (HttpRequestMessage request = new(HttpMethod.Post, checkOptionStatusUrl))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            var exepctedStatus = actionName == ActionName.Enable ? "enabled" :
                actionName == ActionName.Disable ? "disabled" :
                throw new ArgumentException($"Unsupported actionName: {actionName}");
            Assert.AreEqual(exepctedStatus, statusResponse.Status);
        }
    }

    private async Task<string> ProposeJsApp(int asMember = Members.Member0)
    {
        // Fetch the current app and propose that back as-is.
        var bundle = await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>("jsapp/bundle");

        string proposalId;
        using (HttpRequestMessage request = new(HttpMethod.Post, "proposals/create"))
        {
            var proposalContent = new JsonObject
            {
                ["actions"] = new JsonArray
                {
                    {
                        new JsonObject
                        {
                            ["name"] = "set_js_app",
                            ["args"] = new JsonObject
                            {
                                ["bundle"] = bundle
                            }
                        }
                    }
                }
            };

            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClients[asMember].SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            proposalId = responseBody[ProposalIdKey]!.ToString();
        }

        return proposalId;
    }

    private async Task<string> ProposeConstitution(int asMember = Members.Member0)
    {
        // Fetch the current constitution propose that back as-is.
        var constitution = await this.CgsClient_Member0.GetStringAsync("constitution");

        string proposalId;
        using (HttpRequestMessage request = new(HttpMethod.Post, "proposals/create"))
        {
            var proposalContent = new JsonObject
            {
                ["actions"] = new JsonArray
                {
                    {
                        new JsonObject
                        {
                            ["name"] = "set_constitution",
                            ["args"] = new JsonObject
                            {
                                ["constitution"] = constitution
                            }
                        }
                    }
                }
            };

            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClients[asMember].SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            proposalId = responseBody[ProposalIdKey]!.ToString();
        }

        return proposalId;
    }

    public static class RuntimeOption
    {
        public const string AutoApproveCleanRoomPolicy = "autoapprove-cleanroompolicy-proposal";
        public const string AutoApproveDeploymentSpec = "autoapprove-deploymentspec-proposal";
        public const string AutoApproveConstitution = "autoapprove-constitution-proposal";
        public const string AutoApproveJsApp = "autoapprove-jsapp-proposal";
    }

    public static class ActionName
    {
        public const string Enable = "enable";
        public const string Disable = "disable";
    }

    public static class RoleName
    {
        public const string CgsOperator = "cgsOperator";
        public const string ContractOperator = "contractOperator";
    }
}