// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CcfConsortiumMgr.Clients.Governance;
using CcfConsortiumMgr.Clients.Governance.Models;
using Controllers;

namespace CcfConsortiumMgr.Utils;

public static class GovernanceExtensions
{
    public static async Task AcceptMembership(this IGovernanceClient govClient)
    {
        JsonObject? stateDigest = await govClient.GetStateDigestUpdate();
        await govClient.AckStateDigest(stateDigest!);
    }

    public static async Task<List<Member>> GetRecoveryMembers(this IGovernanceClient govClient)
    {
        Members members = await govClient.GetMembers();
        return members.Value.Where(x => x.PublicEncryptionKey != null).ToList();
    }

    public static async Task ProvisionContract(
        this IGovernanceClient govClient,
        string contractId,
        JsonObject contractData)
    {
        await govClient.CreateContract(contractId, contractData);
        await govClient.WaitAppTransactionCommittedAsync();

        string proposalId = await govClient.CreateContractProposal(contractId, contractData);
        await govClient.WaitGovTransactionCommittedAsync();

        await govClient.AcceptProposal(proposalId);
    }

    public static async Task ProvisionUserIdentity(
        this IGovernanceClient govClient,
        Models.UserIdentity userIdentity)
    {
        string proposalId =
            await govClient.CreateUserIdentityProposal(userIdentity.ToUserIdentity());
        await govClient.WaitGovTransactionCommittedAsync();

        await govClient.AcceptProposal(proposalId);
    }

    public static async Task ProvisionUserIdentity(
        this IGovernanceClient govClient,
        string invitationId)
    {
        Invitation invitation =
            await govClient.GetInvitation(invitationId);
        if (invitation.Status != "Accepted")
        {
            throw new ApiException(
                HttpStatusCode.PreconditionFailed,
                "InvitationNotAccepted",
                "Specified invitation is not in an accepted state. " +
                $"Invitation Status: {invitation.Status}.");
        }

        string proposalId =
            await govClient.CreateUserIdentityProposal(invitation.ToUserIdentity());
        await govClient.WaitGovTransactionCommittedAsync();

        await govClient.AcceptProposal(proposalId);
    }

    public static async Task ProvisionUserInvitation(
        this IGovernanceClient govClient,
        Models.UserInvitation userInvitation)
    {
        string proposalId =
            await govClient.CreateUserInvitationProposal(userInvitation.ToUserInvitation());
        await govClient.WaitGovTransactionCommittedAsync();

        await govClient.AcceptProposal(proposalId);
    }

    public static async Task ProvisionDeploymentSpec(
        this IGovernanceClient govClient,
        string contractId,
        JsonObject deploymentSpec,
        JsonObject cleanRoomPolicy)
    {
        string proposalId =
            await govClient.CreateDeploymentSpecProposal(contractId, deploymentSpec);
        await govClient.WaitGovTransactionCommittedAsync();
        await govClient.AcceptProposal(proposalId);

        proposalId = await govClient.CreateCleanRoomPolicyProposal(contractId, cleanRoomPolicy);
        await govClient.WaitGovTransactionCommittedAsync();
        await govClient.AcceptProposal(proposalId);
    }

    public static async Task ProvisionDeploymentInfo(
        this IGovernanceClient govClient,
        string contractId,
        JsonObject deploymentInfo)
    {
        string proposalId =
            await govClient.CreateDeploymentInfoProposal(contractId, deploymentInfo);
        await govClient.WaitGovTransactionCommittedAsync();
        await govClient.AcceptProposal(proposalId);
    }

    public static async Task ProvisionSigningKeys(
        this IGovernanceClient govClient,
        string contractId)
    {
        string proposalId = await govClient.CreateEnableCAProposal(contractId);
        await govClient.WaitGovTransactionCommittedAsync();
        await govClient.AcceptProposal(proposalId);

        await govClient.GenerateContractSigningKey(contractId);
        await govClient.WaitAppTransactionCommittedAsync();
    }

    public static async Task ProvisionOidcSigningKey(
        this IGovernanceClient govClient)
    {
        await govClient.GenerateOidcSigningKey();
        await govClient.WaitAppTransactionCommittedAsync();
    }

    public static async Task PerformRecovery(
        this IGovernanceClient govClient,
        string encryptionPrivateKey)
    {
        EncryptedShareData encryptedShareData = await govClient.GetMemberEncryptedShare();
        string decryptedShare = encryptedShareData.GetDecryptedShare(encryptionPrivateKey);

        await govClient.PerformMemberRecovery(decryptedShare);
        await govClient.WaitGovTransactionCommittedAsync();
    }

    private static async Task AcceptProposal(this IGovernanceClient govClient, string proposalId)
    {
        await govClient.VoteProposal(
            proposalId,
            new JsonObject
            {
                ["ballot"] = "export function vote (proposal, proposerId) { return true }"
            });
        await govClient.WaitGovTransactionCommittedAsync();

        Proposal proposal = await govClient.GetProposal(proposalId);
        if (proposal.ProposalState != "Accepted")
        {
            throw new InvalidOperationException(
                $"Failed to accept proposal. Id: {proposalId}, state: {proposal.ProposalState}.");
        }
    }

    private static async Task WaitGovTransactionCommittedAsync(this IGovernanceClient govClient)
    {
        // TODO (devbabu): Implement this by actually querying the transaction status.
        await Task.Delay(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitAppTransactionCommittedAsync(this IGovernanceClient govClient)
    {
        // TODO (devbabu): Implement this by actually querying the transaction status.
        await Task.Delay(TimeSpan.FromSeconds(5));
    }

    private static string GetDecryptedShare(
        this EncryptedShareData encryptedShareData,
        string encryptionPrivateKey)
    {
        using RSA rsaEncKey = ToRSAKey(encryptionPrivateKey);
        byte[] decryptedShare = rsaEncKey.Decrypt(
            Convert.FromBase64String(encryptedShareData.EncryptedShare),
            RSAEncryptionPadding.OaepSHA256);
        return Convert.ToBase64String(decryptedShare);
    }

    private static RSA ToRSAKey(string encryptionPrivateKey)
    {
        var rsaEncKey = RSA.Create();
        rsaEncKey.ImportFromPem(encryptionPrivateKey);
        return rsaEncKey;
    }
}
