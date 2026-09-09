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
        await this.SetRuntimeOption(RuntimeOption.AutoApproveDeploymentInfo, ActionName.Enable);
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

        // And for deployment info.
        var infoInput = new JsonObject
        {
            ["armId"] = "somethingElse"
        };
        proposalId =
            await this.ProposeContractProposal(
                contractId,
                "deploymentinfo",
                infoInput,
                asMember: Members.Member0);
        proposalResponse =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"proposals/{proposalId}"))!;
        Assert.AreEqual("Open", proposalResponse["proposalState"]!.ToString());

        proposalId =
            await this.ProposeContractProposal(
                contractId,
                "deploymentinfo",
                infoInput,
                asMember: Members.Member1);
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

        proposalId =
            await this.ProposeContractProposal(
                contractId,
                "deploymentinfo",
                infoInput,
                asMember: Members.Member1);
        proposalResponse =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
                $"proposals/{proposalId}"))!;
        Assert.AreEqual("Open", proposalResponse["proposalState"]!.ToString());

        // Update member1 to again become be a cgsOperator.
        await this.SetRole(Members.Member1, RoleName.ContractOperator, "true");

        // Disabling the auto-approve options.
        await this.SetRuntimeOption(RuntimeOption.AutoApproveDeploymentSpec, ActionName.Disable);
        await this.SetRuntimeOption(RuntimeOption.AutoApproveDeploymentInfo, ActionName.Disable);
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

        proposalId =
            await this.ProposeContractProposal(
                contractId,
                "deploymentinfo",
                infoInput,
                asMember: Members.Member1);
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

    // Exercises the operator/member authorization matrix for set_member and remove_member.
    // Consortium: member0, member1 ordinary; member2 is promoted to operator during the test.
    // Operators may add/remove operators, and re-apply to a not-yet-Active operator, without
    // a member vote; changing an already-Active member needs one. finally restores member2.
    [TestMethod]
    public async Task CheckOperatorMemberLifecycleAuthorization()
    {
        const string TemporaryOperatorIdentifier = "constitution-test-operator";
        string member1Certificate = await this.ReadRoleMemberCertificate(Members.Member1);
        string member2Certificate = await this.ReadRoleMemberCertificate(Members.Member2);
        var member2Info =
            (await this.CgsClients[Members.Member2].GetFromJsonAsync<JsonObject>("/show"))!;
        var originalMemberData = member2Info["memberData"]!.DeepClone();
        string? temporaryOperatorId = null;
        bool member2IsOperator = false;

        try
        {
            // Promoting an ordinary member (member2) to operator is NOT auto-approved: the
            // proposal stays Open until every active member votes, then member2 re-activates.
            var operatorMemberData = new JsonObject
            {
                ["identifier"] = "member2",
                ["isOperator"] = true
            };
            string proposalId = await this.ProposeSetMember(
                member2Certificate,
                operatorMemberData,
                Members.Member2);
            await this.AssertProposalState(proposalId, "Open");
            await this.AllMembersAcceptProposal(proposalId);
            member2IsOperator = true;
            await this.ActivateMember(Members.Member2);

            // member2 is now an Active operator; editing its OWN Active membership still requires
            // a full member vote - being an operator is no self-service shortcut once Active.
            proposalId = await this.ProposeSetMember(
                member2Certificate,
                operatorMemberData,
                Members.Member2);
            await this.AssertProposalState(proposalId, "Open");
            await this.MemberAcceptProposal(
                this.CgsClients[Members.Member0],
                proposalId);
            await this.AssertProposalState(proposalId, "Open");
            await this.MemberAcceptProposal(
                this.CgsClients[Members.Member1],
                proposalId);
            await this.AssertProposalState(proposalId, "Accepted");
            await this.ActivateMember(Members.Member2);

            // an operator cannot unilaterally promote an existing Active member
            // (member1) to operator - it stays Open and requires the full member vote.
            proposalId = await this.ProposeSetMember(
                member1Certificate,
                new JsonObject
                {
                    ["identifier"] = "member1",
                    ["isOperator"] = true
                },
                Members.Member2);
            await this.AssertProposalState(proposalId, "Open");

            // Test setup: mint a brand-new operator identity not yet present in the consortium.
            using var temporaryOperatorKey =
                System.Security.Cryptography.ECDsa.Create(
                    System.Security.Cryptography.ECCurve.NamedCurves.nistP384);
            var certificateRequest = new System.Security.Cryptography.X509Certificates
                .CertificateRequest(
                    $"CN={TemporaryOperatorIdentifier}",
                    temporaryOperatorKey,
                    System.Security.Cryptography.HashAlgorithmName.SHA384);
            using var temporaryOperatorCertificate = certificateRequest.CreateSelfSigned(
                new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero));
            string temporaryOperatorCertificatePem =
                temporaryOperatorCertificate.ExportCertificatePem();
            var temporaryOperatorData = new JsonObject
            {
                ["identifier"] = TemporaryOperatorIdentifier,
                ["isOperator"] = true
            };

            // An ordinary member (member1) adding a brand-new operator gets NO shortcut: the add
            // is Open until all members vote - only operator proposers skip voting.
            proposalId = await this.ProposeSetMember(
                temporaryOperatorCertificatePem,
                temporaryOperatorData,
                Members.Member1);
            await this.AssertProposalState(proposalId, "Open");
            await this.MemberAcceptProposal(
                this.CgsClients[Members.Member0],
                proposalId);
            await this.AssertProposalState(proposalId, "Open");
            await this.MemberAcceptProposal(
                this.CgsClients[Members.Member1],
                proposalId);
            await this.AssertProposalState(proposalId, "Accepted");
            temporaryOperatorId = await this.GetMemberId(TemporaryOperatorIdentifier);

            // Re-applying to that operator while it is only Accepted, proposed by an ordinary
            // member (member1), still needs the full vote - the shortcut is operator-only.
            proposalId = await this.ProposeSetMember(
                temporaryOperatorCertificatePem,
                temporaryOperatorData,
                Members.Member1);
            await this.AssertProposalState(proposalId, "Open");
            await this.MemberAcceptProposal(
                this.CgsClients[Members.Member0],
                proposalId);
            await this.AssertProposalState(proposalId, "Open");
            await this.MemberAcceptProposal(
                this.CgsClients[Members.Member1],
                proposalId);
            await this.AssertProposalState(proposalId, "Accepted");

            // An operator (member2) may unilaterally REMOVE another operator - Accepted, no vote.
            proposalId = await this.ProposeRemoveMember(
                temporaryOperatorId,
                Members.Member2);
            await this.AssertProposalState(proposalId, "Accepted");
            temporaryOperatorId = null;

            // An operator may unilaterally ADD a brand-new operator - Accepted, no vote.
            proposalId = await this.ProposeSetMember(
                temporaryOperatorCertificatePem,
                temporaryOperatorData,
                Members.Member2);
            await this.AssertProposalState(proposalId, "Accepted");
            temporaryOperatorId = await this.GetMemberId(TemporaryOperatorIdentifier);

            // The shortcut still applies while that operator is only Accepted (not yet Active):
            // an operator may re-apply set_member to it without a member vote.
            proposalId = await this.ProposeSetMember(
                temporaryOperatorCertificatePem,
                temporaryOperatorData,
                Members.Member2);
            await this.AssertProposalState(proposalId, "Accepted");

            // An operator may unilaterally remove that operator again - Accepted, no vote.
            proposalId = await this.ProposeRemoveMember(
                temporaryOperatorId,
                Members.Member2);
            await this.AssertProposalState(proposalId, "Accepted");
            temporaryOperatorId = null;
        }
        finally
        {
            // Cleanup: remove the temporary operator if a scenario above threw before doing so.
            if (temporaryOperatorId != null)
            {
                string proposalId = await this.ProposeRemoveMember(
                    temporaryOperatorId,
                    Members.Member2);
                await this.AssertProposalState(proposalId, "Accepted");
            }

            // Restore member2 to its original (non-operator) data so the consortium is unchanged
            // for other tests (a normal change to an Active member, hence a full vote).
            if (member2IsOperator)
            {
                string proposalId = await this.ProposeSetMember(
                    member2Certificate,
                    originalMemberData,
                    Members.Member2);
                await this.AssertProposalState(proposalId, "Open");
                await this.MemberAcceptProposal(
                    this.CgsClients[Members.Member0],
                    proposalId);
                await this.MemberAcceptProposal(
                    this.CgsClients[Members.Member1],
                    proposalId);
                await this.ActivateMember(Members.Member2);
            }
        }
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

    private async Task<string> ProposeSetMember(
        string certificate,
        JsonNode memberData,
        int asMember)
    {
        return await this.CreateMemberProposal(
            "set_member",
            new JsonObject
            {
                ["cert"] = certificate,
                ["member_data"] = memberData.DeepClone()
            },
            asMember);
    }

    private async Task<string> ProposeRemoveMember(string memberId, int asMember)
    {
        return await this.CreateMemberProposal(
            "remove_member",
            new JsonObject
            {
                ["member_id"] = memberId
            },
            asMember);
    }

    private async Task<string> CreateMemberProposal(
        string actionName,
        JsonObject args,
        int asMember)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "proposals/create")
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["actions"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = actionName,
                        ["args"] = args
                    }
                }
            })
        };
        using HttpResponseMessage response = await this.CgsClients[asMember].SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        return responseBody[ProposalIdKey]!.ToString();
    }

    private async Task AssertProposalState(string proposalId, string expectedState)
    {
        var proposal = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(
            $"proposals/{proposalId}"))!;
        Assert.AreEqual(expectedState, proposal["proposalState"]!.ToString());
    }

    private async Task ActivateMember(int member)
    {
        using HttpResponseMessage response = await this.CgsClients[member].PostAsync(
            "members/statedigests/ack",
            content: null);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<string> GetMemberId(string identifier)
    {
        var members = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>("members"))!;
        foreach (JsonNode? member in members["value"]!.AsArray())
        {
            if (member!["memberData"]?["identifier"]?.ToString() == identifier)
            {
                return member["memberId"]!.ToString();
            }
        }

        Assert.Fail($"Could not find member ID for {identifier}.");
        return string.Empty;
    }

    private async Task<string> ReadRoleMemberCertificate(int member)
    {
        string certificatePath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "roles",
                "sandbox_common",
                $"member{member}_cert.pem"));
        return await File.ReadAllTextAsync(certificatePath);
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
        public const string AutoApproveDeploymentInfo = "autoapprove-deploymentinfo-proposal";
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