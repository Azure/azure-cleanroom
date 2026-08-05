// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json.Nodes;
using CoseUtils;
using Microsoft.Ccf.Client._Acks;
using Microsoft.Ccf.Client._Proposals;
using Microsoft.Ccf.Client._Recovery;
using Microsoft.Ccf.Client._ServiceState;

namespace Azure.Cleanroom.Sdk;

/// <summary>
/// CCF governance (<c>gov/*</c>) operations using the generated
/// <see cref="Microsoft.Ccf.Client.CcfClient"/>. Write operations build
/// COSE Sign1 envelopes explicitly via
/// <see cref="Cose.CreateGovCoseSign1Message"/> and send them
/// through the generated client's protocol methods. Read operations
/// use the typed convenience methods directly.
/// </summary>
public partial class CleanroomClient
{
    // ================================================================
    // State Digest
    // ================================================================

    /// <inheritdoc/>
    public async Task<StateDigest> UpdateStateDigestAsync()
    {
        this.RequireMemberAuth();
        var cose = await Cose.CreateGovCoseSign1Message(
            this.coseSignKey!,
            GovMessageType.StateDigest,
            payload: null);
        var result = await this.ccfClient
            .GetAcksClient()
            .GetAcksStateDigestsClient()
            .UpdateAsync(
                this.memberId!,
                BinaryContent.Create(new BinaryData(cose)));
        return (StateDigest)result;
    }

    /// <inheritdoc/>
    public async Task AcknowledgeStateDigestAsync()
    {
        this.RequireMemberAuth();
        var stateDigest = await this.UpdateStateDigestAsync();
        var payload = ModelReaderWriter.Write(stateDigest).ToString();
        var cose = await Cose.CreateGovCoseSign1Message(
            this.coseSignKey!,
            GovMessageType.Ack,
            payload);
        await this.ccfClient
            .GetAcksClient()
            .GetAcksStateDigestsClient()
            .AcknowledgeAsync(
                this.memberId!,
                BinaryContent.Create(new BinaryData(cose)));
    }

    // ================================================================
    // Proposals (COSE write operations)
    // ================================================================

    /// <inheritdoc/>
    public async Task<Proposal> CreateProposalAsync(
        JsonObject proposal)
    {
        this.RequireMemberAuth();
        var cose = await Cose.CreateGovCoseSign1Message(
            this.coseSignKey!,
            GovMessageType.Proposal,
            proposal.ToJsonString());
        var result = await this.ccfClient
            .GetProposalsClient()
            .GetProposalsProposalsClient()
            .CreateAsync(
                BinaryContent.Create(new BinaryData(cose)));
        return (Proposal)result;
    }

    /// <inheritdoc/>
    public async Task<Proposal> WithdrawProposalAsync(
        string proposalId)
    {
        this.RequireMemberAuth();
        var cose = await Cose.CreateGovCoseSign1Message(
            this.coseSignKey!,
            GovMessageType.Withdrawal,
            payload: null,
            proposalId: proposalId);
        var result = await this.ccfClient
            .GetProposalsClient()
            .GetProposalsProposalsClient()
            .WithdrawAsync(
                proposalId,
                BinaryContent.Create(new BinaryData(cose)));
        return (Proposal)result;
    }

    // ================================================================
    // Voting (COSE write operations)
    // ================================================================

    /// <inheritdoc/>
    public async Task<Proposal> VoteAcceptProposalAsync(
        string proposalId)
    {
        var ballot = new JsonObject
        {
            ["ballot"] =
                "export function vote (proposal, proposerId) " +
                "{ return true }"
        };

        return await this.SubmitBallotAsync(proposalId, ballot);
    }

    /// <inheritdoc/>
    public async Task<Proposal> VoteRejectProposalAsync(
        string proposalId)
    {
        var ballot = new JsonObject
        {
            ["ballot"] =
                "export function vote (proposal, proposerId) " +
                "{ return false }"
        };

        return await this.SubmitBallotAsync(proposalId, ballot);
    }

    /// <inheritdoc/>
    public async Task<Proposal> VoteOnProposalAsync(
        string proposalId,
        JsonObject ballot)
    {
        return await this.SubmitBallotAsync(proposalId, ballot);
    }

    // ================================================================
    // Proposals (read-only GET operations — no COSE)
    // ================================================================

    /// <inheritdoc/>
    public async Task<Proposal> GetGovProposalAsync(
        string proposalId)
    {
        this.RequireMemberAuth();
        var result = await this.ccfClient
            .GetProposalsClient()
            .GetProposalsProposalsClient()
            .GetAsync(proposalId);
        return result.Value;
    }

    /// <inheritdoc/>
    public async Task<ProposalActions> GetProposalActionsAsync(
        string proposalId)
    {
        this.RequireMemberAuth();
        var result = await this.ccfClient
            .GetProposalsClient()
            .GetProposalsProposalsClient()
            .GetActionsAsync(proposalId);
        return result.Value;
    }

    /// <summary>
    /// Gets the votes for a governance proposal.
    /// Uses the legacy (pre api-version) endpoint which returns
    /// member-wise ballot details inline. This endpoint is not
    /// modeled in the CCF TypeSpec and is hand-crafted.
    /// See https://github.com/microsoft/CCF/issues/6107.
    /// Auth: member_cert (mTLS).
    /// </summary>
    /// <param name="proposalId">The ID of the proposal.</param>
    /// <returns>A list of votes with member IDs and their accept/reject votes.</returns>
    public async Task<VotesListResponse> GetProposalVotesAsync(
        string proposalId)
    {
        this.RequireMemberAuth();

        // Legacy endpoint: GET gov/proposals/{id} (no api-version).
        // Returns { "ballots": { "<memberId>": "<script>", ... } }.
        var uri = new Uri(
            this.endpoint,
            $"gov/proposals/{Uri.EscapeDataString(proposalId)}");
        var classifier = PipelineMessageClassifier.Create([200]);
        using var message = this.ccfClient.Pipeline.CreateMessage(
            uri,
            "GET",
            classifier);
        message.Request.Headers.Set("Accept", "application/json");
        await this.ccfClient.Pipeline.SendAsync(message)
            .ConfigureAwait(false);

        if (message.Response?.IsError == true)
        {
            throw new ClientResultException(message.Response);
        }

        var json = JsonNode.Parse(
            message.Response!.Content.ToString())!.AsObject();
        var ballots = json["ballots"]?.AsObject();
        var votes = new List<Vote>();

        if (ballots != null)
        {
            const string voteYes =
                "export function vote (proposal, proposerId)" +
                " { return true }";

            foreach (var ballot in ballots)
            {
                string? script = ballot.Value?.ToString();
                bool accepted = script == voteYes;
                votes.Add(new Vote
                {
                    MemberId = ballot.Key,
                    VoteProp = accepted
                });
            }
        }

        return new VotesListResponse
        {
            ValueName = votes.ToArray()
        };
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Proposal>>
        ListGovProposalsAsync()
    {
        this.RequireMemberAuth();
        var proposals = new List<Proposal>();
        await foreach (var proposal in this.ccfClient
            .GetProposalsClient()
            .GetProposalsProposalsClient()
            .GetAllAsync())
        {
            proposals.Add(proposal);
        }

        return proposals;
    }

    // ================================================================
    // Network / Members (read-only GET operations — no COSE)
    // ================================================================

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Member>> ListMembersAsync()
    {
        var members = new List<Member>();
        await foreach (var member in this.ccfClient
            .GetServiceStateClient()
            .GetServiceStateServiceStateClient()
            .GetMembersAsync())
        {
            members.Add(member);
        }

        return members;
    }

    /// <inheritdoc/>
    public async Task<ServiceInfo> ShowNetworkAsync()
    {
        this.RequireMemberAuth();
        var result = await this.ccfClient
            .GetServiceStateClient()
            .GetServiceStateServiceStateClient()
            .GetServiceInfoAsync();
        return result.Value;
    }

    /// <inheritdoc/>
    public async Task<EncryptedRecoveryShare>
        GetMemberEncryptedShareAsync()
    {
        this.RequireMemberAuth();
        var result = await this.ccfClient
            .GetRecoveryClient()
            .GetRecoveryEncryptedSharesClient()
            .GetAsync(this.memberId);
        return result.Value;
    }

    /// <inheritdoc/>
    public async Task<RecoveryResponse> SubmitRecoveryShareAsync(
        string decryptedShare)
    {
        this.RequireMemberAuth();
        var payload = new JsonObject
        {
            ["share"] = decryptedShare
        };
        var cose = await Cose.CreateGovCoseSign1Message(
            this.coseSignKey!,
            GovMessageType.RecoveryShare,
            payload.ToJsonString());
        var result = await this.ccfClient
            .GetRecoveryClient()
            .GetRecoverySharesClient()
            .SubmitAsync(
                this.memberId!,
                BinaryContent.Create(new BinaryData(cose)));
        return (RecoveryResponse)result;
    }

    private async Task<Proposal> SubmitBallotAsync(
        string proposalId,
        JsonObject ballot)
    {
        this.RequireMemberAuth();
        var cose = await Cose.CreateGovCoseSign1Message(
            this.coseSignKey!,
            GovMessageType.Ballot,
            ballot.ToJsonString(),
            proposalId);
        var result = await this.ccfClient
            .GetProposalsClient()
            .GetProposalsBallotsClient()
            .SubmitAsync(
                proposalId,
                this.memberId!,
                BinaryContent.Create(new BinaryData(cose)));
        return (Proposal)result;
    }
}