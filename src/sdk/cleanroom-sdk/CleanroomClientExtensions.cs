// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Cleanroom.Governance.Client;
using Microsoft.Ccf.Client._Proposals;

using Proposal = Microsoft.Ccf.Client._Proposals.Proposal;

#pragma warning disable SA1611 // Missing parameter documentation - pending scrub.
#pragma warning disable SA1615 // Element return value should be documented - pending scrub.

namespace Azure.Cleanroom.Sdk;

/// <summary>
/// Convenience extension methods for <see cref="ICleanroomClient"/>
/// that build governance proposals and vote on them. Each method
/// constructs the appropriate <c>JsonObject</c> and delegates to the
/// core <see cref="ICleanroomClient.CreateProposalAsync"/>,
/// <see cref="ICleanroomClient.VoteAcceptProposalAsync"/>,
/// <see cref="ICleanroomClient.VoteRejectProposalAsync"/>, or
/// <see cref="ICleanroomClient.VoteOnProposalAsync"/> methods.
/// </summary>
public static class CleanroomClientExtensions
{
    // ================================================================
    // Contract proposals
    // ================================================================

    /// <summary>
    /// Proposes a contract change. Fetches the current contract
    /// from the KV store and submits it as a governance proposal.
    /// </summary>
    public static async Task<Proposal>
        ProposeContractChangeAsync(
            this ICleanroomClient client,
            string contractId)
    {
        var current = await client.GetContractAsync(contractId);
        var contractNode = new JsonObject
        {
            ["id"] = current.Id,
            ["version"] = current.Version,
            ["state"] = current.State,
            ["data"] = JsonNode.Parse(
                current.Data.ToString()),
        };

        if (!string.IsNullOrEmpty(current.ProposalId))
        {
            contractNode["proposalId"] = current.ProposalId;
        }

        var proposal = BuildProposal(
            "set_contract",
            new JsonObject
            {
                ["contractId"] = contractId,
                ["contract"] = contractNode
            });

        return await client.CreateProposalAsync(proposal);
    }

    /// <summary>
    /// Votes to accept a contract proposal.
    /// </summary>
    public static async Task<Proposal>
        VoteAcceptContractAsync(
            this ICleanroomClient client,
            string proposalId)
    {
        return await client.VoteAcceptProposalAsync(proposalId);
    }

    /// <summary>
    /// Votes to reject a contract proposal.
    /// </summary>
    public static async Task<Proposal>
        VoteRejectContractAsync(
            this ICleanroomClient client,
            string proposalId)
    {
        return await client.VoteRejectProposalAsync(proposalId);
    }

    // ================================================================
    // Deployment spec / info proposals
    // ================================================================

    /// <summary>
    /// Proposes a deployment spec change.
    /// </summary>
    public static async Task<Proposal>
        ProposeDeploymentSpecChangeAsync(
            this ICleanroomClient client,
            string contractId,
            JsonObject specData)
    {
        var proposal = BuildProposal(
            "set_deployment_spec",
            new JsonObject
            {
                ["contractId"] = contractId,
                ["spec"] = new JsonObject
                {
                    ["data"] = specData
                }
            });

        return await client.CreateProposalAsync(proposal);
    }

    /// <summary>
    /// Proposes a deployment info change.
    /// </summary>
    public static async Task<Proposal>
        ProposeDeploymentInfoChangeAsync(
            this ICleanroomClient client,
            string contractId,
            JsonObject infoData)
    {
        var proposal = BuildProposal(
            "set_deployment_info",
            new JsonObject
            {
                ["contractId"] = contractId,
                ["info"] = new JsonObject
                {
                    ["data"] = infoData
                }
            });

        return await client.CreateProposalAsync(proposal);
    }

    // ================================================================
    // Clean room policy proposals
    // ================================================================

    /// <summary>
    /// Proposes a clean room policy change.
    /// </summary>
    public static async Task<Proposal>
        ProposeCleanRoomPolicyChangeAsync(
            this ICleanroomClient client,
            string contractId,
            CleanRoomPolicyProposal policy)
    {
        var proposal = BuildProposal(
            "set_clean_room_policy",
            new JsonObject
            {
                ["contractId"] = contractId,
                ["type"] = policy.TypeName,
                ["claims"] = JsonSerializer.SerializeToNode(policy.Claims)
            });

        return await client.CreateProposalAsync(proposal);
    }

    // ================================================================
    // Member document proposals
    // ================================================================

    /// <summary>
    /// Proposes a member document change. Fetches the current
    /// document from the KV store and submits it as a governance
    /// proposal.
    /// </summary>
    public static async Task<Proposal>
        ProposeMemberDocumentChangeAsync(
            this ICleanroomClient client,
            string contractId,
            string documentId)
    {
        var current = await client.GetMemberDocumentAsync(
            contractId,
            documentId);
        var documentNode = new JsonObject
        {
            ["id"] = current.Id,
            ["contractId"] = current.ContractId,
            ["version"] = current.Version,
            ["state"] = current.State,
            ["data"] = JsonNode.Parse(
                current.Data.ToString()),
        };

        if (!string.IsNullOrEmpty(current.ProposalId))
        {
            documentNode["proposalId"] = current.ProposalId;
        }

        var proposal = BuildProposal(
            "set_member_document",
            new JsonObject
            {
                ["documentId"] = documentId,
                ["document"] = documentNode
            });

        return await client.CreateProposalAsync(proposal);
    }

    /// <summary>
    /// Votes to accept a member document proposal.
    /// </summary>
    public static async Task<Proposal>
        VoteAcceptMemberDocumentAsync(
            this ICleanroomClient client,
            string proposalId)
    {
        return await client.VoteAcceptProposalAsync(proposalId);
    }

    /// <summary>
    /// Votes to reject a member document proposal.
    /// </summary>
    public static async Task<Proposal>
        VoteRejectMemberDocumentAsync(
            this ICleanroomClient client,
            string proposalId)
    {
        return await client.VoteRejectProposalAsync(proposalId);
    }

    // ================================================================
    // User document proposals
    // ================================================================

    /// <summary>
    /// Proposes a user document change. Fetches the current
    /// document from the KV store and submits it as a user
    /// proposal (app-level, not CCF governance).
    /// If the document has no approvers, defaults to all active
    /// non-operator members (matching cgs-client behavior).
    /// </summary>
    public static async Task<CreateUserProposalResponse>
        ProposeUserDocumentChangeAsync(
            this ICleanroomClient client,
            string contractId,
            string documentId)
    {
        var current = await client.GetUserDocumentAsync(
            contractId,
            documentId);
        var documentNode = new JsonObject
        {
            ["id"] = current.Id,
            ["contractId"] = current.ContractId,
            ["version"] = current.Version,
            ["state"] = current.State,
            ["data"] = JsonNode.Parse(
                current.Data.ToString()),
        };

        if (!string.IsNullOrEmpty(current.ProposalId))
        {
            documentNode["proposalId"] = current.ProposalId;
        }

        var proposalId = Guid.NewGuid().ToString("N");
        var request = new CreateUserProposalRequest(
            "set_user_document",
            BinaryData.FromObjectAsJson(new JsonObject
            {
                ["documentId"] = documentId,
                ["document"] = documentNode
            }));

        if (current.Approvers.Count > 0)
        {
            foreach (var approver in current.Approvers)
            {
                request.Approvers.Add(approver);
            }
        }
        else
        {
            // Default to all active non-operator members
            // as approvers (matching cgs-client behavior).
            await PopulateDefaultApproversAsync(client, request);
        }

        return await client.PutUserProposalAsync(
            proposalId,
            request);
    }

    /// <summary>
    /// Votes to accept a user document proposal.
    /// </summary>
    public static async Task
        VoteAcceptUserDocumentAsync(
            this ICleanroomClient client,
            string proposalId)
    {
        await client.SubmitUserProposalBallotAsync(
            proposalId,
            new SubmitUserProposalBallotRequest("accepted"));
    }

    /// <summary>
    /// Votes to reject a user document proposal.
    /// </summary>
    public static async Task
        VoteRejectUserDocumentAsync(
            this ICleanroomClient client,
            string proposalId)
    {
        await client.SubmitUserProposalBallotAsync(
            proposalId,
            new SubmitUserProposalBallotRequest("rejected"));
    }

    // ================================================================
    // Runtime option proposals
    // ================================================================

    /// <summary>
    /// Proposes enabling logging for a contract.
    /// </summary>
    public static async Task<Proposal>
        ProposeEnableLoggingAsync(
            this ICleanroomClient client,
            string contractId)
    {
        return await ProposeRuntimeOptionAsync(
            client,
            "set_contract_runtime_options_enable_logging",
            contractId);
    }

    /// <summary>
    /// Proposes disabling logging for a contract.
    /// </summary>
    public static async Task<Proposal>
        ProposeDisableLoggingAsync(
            this ICleanroomClient client,
            string contractId)
    {
        return await ProposeRuntimeOptionAsync(
            client,
            "set_contract_runtime_options_disable_logging",
            contractId);
    }

    /// <summary>
    /// Proposes enabling telemetry for a contract.
    /// </summary>
    public static async Task<Proposal>
        ProposeEnableTelemetryAsync(
            this ICleanroomClient client,
            string contractId)
    {
        return await ProposeRuntimeOptionAsync(
            client,
            "set_contract_runtime_options_enable_telemetry",
            contractId);
    }

    /// <summary>
    /// Proposes disabling telemetry for a contract.
    /// </summary>
    public static async Task<Proposal>
        ProposeDisableTelemetryAsync(
            this ICleanroomClient client,
            string contractId)
    {
        return await ProposeRuntimeOptionAsync(
            client,
            "set_contract_runtime_options_disable_telemetry",
            contractId);
    }

    /// <summary>
    /// Proposes a named runtime option change for a contract.
    /// </summary>
    public static async Task<Proposal>
        ProposeRuntimeOptionAsync(
            this ICleanroomClient client,
            string option,
            string contractId)
    {
        var proposal = BuildProposal(
            option,
            new JsonObject { ["contractId"] = contractId });

        return await client.CreateProposalAsync(proposal);
    }

    // ================================================================
    // User identity / invitation proposals
    // ================================================================

    /// <summary>
    /// Proposes adding a user identity.
    /// </summary>
    public static async Task<Proposal>
        AddUserIdentityAsync(
            this ICleanroomClient client,
            AddUserIdentityRequest identity)
    {
        var data = new JsonObject
        {
            ["tenantId"] = identity.TenantId
        };

        if (!string.IsNullOrEmpty(identity.Identifier))
        {
            data["identifier"] = identity.Identifier;
        }

        var proposal = BuildProposal(
            "set_user_identity",
            new JsonObject
            {
                ["id"] = identity.ObjectId,
                ["accountType"] = identity.AccountType,
                ["data"] = data
            });

        return await client.CreateProposalAsync(proposal);
    }

    /// <summary>
    /// Proposes an invitation for a user.
    /// </summary>
    public static async Task<InvitationProposalResponse>
        ProposeInvitationAsync(
            this ICleanroomClient client,
            InvitationProposalInput invitation)
    {
        var args = JsonSerializer.SerializeToNode(invitation)!
            .AsObject();

        var proposal = BuildProposal(
            "set_user_invitation", args);
        var response = await client.CreateProposalAsync(proposal);

        return new InvitationProposalResponse
        {
            ProposalId = response.ProposalId,
            InvitationId = string.Empty
        };
    }

    private static JsonObject BuildProposal(
        string actionName,
        JsonObject args)
    {
        return new JsonObject
        {
            ["actions"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = actionName,
                    ["args"] = args
                }
            }
        };
    }

    /// <summary>
    /// Populates default approvers from all active non-operator members.
    /// Matches cgs-client behavior in UserProposalControllerBase.SubmitUserProposal.
    /// </summary>
    private static async Task PopulateDefaultApproversAsync(
        ICleanroomClient client,
        CreateUserProposalRequest request)
    {
        var members = await client.ListMembersAsync();
        foreach (var member in members)
        {
            if (member.Status == Microsoft.Ccf.Client._ServiceState.MemberStatus.Active)
            {
                // Skip operator members by checking member_data.
                if (member.MemberData != null)
                {
                    var memberData = JsonSerializer.Deserialize<JsonObject>(member.MemberData);
                    if (memberData != null)
                    {
                        bool isOperator =
                            memberData["is_operator"]?.GetValue<bool>() == true;
                        bool isRecoveryOperator =
                            memberData["is_recovery_operator"]?.GetValue<bool>() == true;
                        var cgsRoles = memberData["cgsRoles"];
                        bool isCgsOperator =
                            cgsRoles?["cgsOperator"]?.GetValue<bool>() == true;
                        bool isContractOperator =
                            cgsRoles?["contractOperator"]?.GetValue<bool>() == true;
                        if (isOperator || isRecoveryOperator || isCgsOperator || isContractOperator)
                        {
                            continue;
                        }
                    }
                }

                request.Approvers.Add(new UserProposalApprover(member.MemberId, "member"));
            }
        }
    }
}