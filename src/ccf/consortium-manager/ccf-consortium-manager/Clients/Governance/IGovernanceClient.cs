// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using CcfConsortiumMgr.Clients.Governance.Models;

namespace CcfConsortiumMgr.Clients.Governance;

public interface IGovernanceClient
{
    string MemberId { get; }

    Task<JsonObject> GetStateDigestUpdate();

    Task AckStateDigest(JsonObject stateDigest);

    Task<Members> GetMembers();

    Task<EncryptedShareData> GetMemberEncryptedShare();

    Task PerformMemberRecovery(string decryptedShare);

    Task<Invitation> GetInvitation(string invitationId);

    Task<string> CreateUserIdentityProposal(UserIdentity userIdentity);

    Task<string> CreateUserInvitationProposal(UserInvitation userInvitation);

    Task CreateContract(string contractId, JsonObject contractData);

    Task<string> CreateContractProposal(string contractId, JsonObject contractData);

    Task<string> CreateDeploymentSpecProposal(string contractId, JsonObject deploymentSpec);

    Task<string> CreateDeploymentInfoProposal(string contractId, JsonObject deploymentInfo);

    Task<string> CreateCleanRoomPolicyProposal(string contractId, JsonObject cleanRoomPolicy);

    Task<string> CreateEnableCAProposal(string contractId);

    Task GenerateContractSigningKey(string contractId);

    Task GenerateOidcSigningKey();

    Task<Proposal> GetProposal(string proposalId);

    Task VoteProposal(string proposalId, JsonObject ballot);
}
