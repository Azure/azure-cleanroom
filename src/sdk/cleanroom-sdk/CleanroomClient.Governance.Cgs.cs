// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Cleanroom.Governance.Client;

using GeneratedClient =
    Azure.Cleanroom.Governance.Client.GovernanceClient;

namespace Azure.Cleanroom.Sdk;

/// <summary>
/// Wire-level implementation (delegates to generated sub-clients).
/// Delegates to the TypeSpec-generated sub-clients, unwraps
/// <c>ClientResult&lt;T&gt;</c> → <c>T</c>.
/// </summary>
public partial class CleanroomClient
{
    // ================================================================
    // Certificate Authority
    // ================================================================

    /// <inheritdoc/>
    public async Task GenerateCaSigningKeyAsync(string contractId)
    {
        await this.generatedClient
            .GetCertificateAuthorityClient()
            .GenerateCASigningKeyAsync(contractId);
    }

    /// <summary>
    /// Generates an endorsed certificate (wire-level).
    /// </summary>
    /// <param name="contractId">The contract identifier.</param>
    /// <param name="request">The endorsed cert request.</param>
    /// <returns>The endorsed certificate response.</returns>
    public async Task<GenerateEndorsedCertResponse>
        GenerateEndorsedCertAsync(
            string contractId,
            GenerateEndorsedCertRequest request)
    {
        var result = await this.generatedClient
            .GetCertificateAuthorityClient()
            .GenerateEndorsedCertAsync(contractId, request);
        return result.Value;
    }

    /// <inheritdoc/>
    public async Task<CAInfo> GetCaInfoAsync(string contractId)
    {
        var result = await this.generatedClient
            .GetCertificateAuthorityClient()
            .GetCAInfoAsync(contractId);
        return result.Value;
    }

    // ================================================================
    // Clean Room Policy
    // ================================================================

    /// <inheritdoc/>
    public async Task<GetCleanRoomPolicyResponse>
        GetCleanRoomPolicyAsync(string contractId)
    {
        var result = await this.generatedClient
            .GetCleanRoomPolicyClient()
            .GetCleanRoomPolicyAsync(contractId);
        return result.Value;
    }

    /// <inheritdoc/>
    public async Task<ListDelegatePoliciesResponse>
        ListDelegatePoliciesAsync(string contractId)
    {
        var result = await this.generatedClient
            .GetCleanRoomPolicyClient()
            .GetDelegatePoliciesAsync(contractId);
        return result.Value;
    }

    /// <inheritdoc/>
    public async Task<GetDelegatePolicyResponse>
        GetDelegatePolicyAsync(
            string contractId,
            string delegateType,
            string delegateId)
    {
        var result = await this.generatedClient
            .GetCleanRoomPolicyClient()
            .GetDelegatePolicyAsync(
                contractId,
                delegateType,
                delegateId);
        return result.Value;
    }

    /// <summary>
    /// Sets a delegate policy (wire-level).
    /// </summary>
    /// <param name="contractId">The contract identifier.</param>
    /// <param name="delegateType">The delegate type.</param>
    /// <param name="delegateId">The delegate identifier.</param>
    /// <param name="request">The policy request.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SetDelegatePolicyAsync(
        string contractId,
        string delegateType,
        string delegateId,
        SetDelegateCleanRoomPolicyRequest request)
    {
        await this.generatedClient
            .GetCleanRoomPolicyClient()
            .SetDelegatePolicyAsync(
                contractId,
                delegateType,
                delegateId,
                request);
    }

    // ================================================================
    // Contract Runtime Options
    // ================================================================

    /// <inheritdoc/>
    public async Task<GetRuntimeOptionResponse>
        CheckContractRuntimeOptionAsync(
            string contractId,
            string option)
    {
        var result = await this.generatedClient
            .GetContractRuntimeOptionsClient()
            .CheckContractRuntimeOptionAsync(contractId, option);
        return result.Value;
    }

    /// <inheritdoc/>
    public async Task EnableContractRuntimeOptionAsync(
        string contractId,
        string option)
    {
        await this.generatedClient
            .GetContractRuntimeOptionsClient()
            .EnableContractRuntimeOptionAsync(contractId, option);
    }

    /// <inheritdoc/>
    public async Task DisableContractRuntimeOptionAsync(
        string contractId,
        string option)
    {
        await this.generatedClient
            .GetContractRuntimeOptionsClient()
            .DisableContractRuntimeOptionAsync(contractId, option);
    }

    // ================================================================
    // Contracts
    // ================================================================

    /// <inheritdoc/>
    public async Task<ListContractsResponse> ListContractsAsync()
    {
        var result = await this.generatedClient
            .GetContractsClient()
            .GetContractsAsync();
        return result.Value;
    }

    /// <inheritdoc/>
    public async Task PutContractAsync(
        string contractId,
        PutContractRequest request)
    {
        await this.generatedClient
            .GetContractsClient()
            .PutContractAsync(contractId, request);
    }

    /// <inheritdoc/>
    public async Task<GetContractResponse> GetContractAsync(
        string contractId)
    {
        var result = await this.generatedClient
            .GetContractsClient()
            .GetContractAsync(contractId);
        return result.Value;
    }

    // ================================================================
    // Deployment
    // ================================================================

    /// <inheritdoc/>
    public async Task<GetDeploymentInfoResponse>
        GetDeploymentInfoAsync(string contractId)
    {
        var result = await this.generatedClient
            .GetDeploymentInfoClient()
            .GetDeploymentInfoAsync(contractId);
        return result.Value;
    }

    /// <inheritdoc/>
    public async Task<GetDeploymentSpecResponse>
        GetDeploymentSpecAsync(string contractId)
    {
        var result = await this.generatedClient
            .GetDeploymentSpecClient()
            .GetDeploymentSpecAsync(contractId);
        return result.Value;
    }

    // ================================================================
    // Events
    // ================================================================

    /// <inheritdoc/>
    public async Task<GetEventsResponse> ListEventsAsync(
        string contractId)
    {
        var result = await this.generatedClient
            .GetEventsClient()
            .GetEventsAsync(contractId);
        return result.Value;
    }

    /// <summary>
    /// Puts an audit event (wire-level).
    /// </summary>
    /// <param name="contractId">The contract identifier.</param>
    /// <param name="request">The event request.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task PutEventAsync(
        string contractId,
        PutEventRequest request)
    {
        await this.generatedClient
            .GetEventsClient()
            .PutEventAsync(contractId, request);
    }

    // ================================================================
    // Member Documents
    // ================================================================

    /// <inheritdoc/>
    public async Task<ListMemberDocumentResponse[]>
        ListMemberDocumentsAsync(string contractId)
    {
        var result = await this.generatedClient
            .GetMemberDocumentsClient()
            .GetMemberDocumentsAsync(contractId);
        return result.Value.ToArray();
    }

    /// <inheritdoc/>
    public async Task PutMemberDocumentAsync(
        string contractId,
        string documentId,
        PutMemberDocumentRequest request)
    {
        await this.generatedClient
            .GetMemberDocumentsClient()
            .PutMemberDocumentAsync(contractId, documentId, request);
    }

    /// <inheritdoc/>
    public async Task<GetMemberDocumentResponse>
        GetMemberDocumentAsync(
            string contractId,
            string documentId)
    {
        var result = await this.generatedClient
            .GetMemberDocumentsClient()
            .GetMemberDocumentAsync(contractId, documentId);
        return result.Value;
    }

    /// <summary>
    /// Gets an accepted member document (wire-level).
    /// </summary>
    /// <param name="contractId">The contract identifier.</param>
    /// <param name="documentId">The document identifier.</param>
    /// <param name="request">The attestation request.</param>
    /// <returns>The accepted member document response.</returns>
    public async Task<GetAcceptedMemberDocumentResponse>
        GetAcceptedMemberDocumentAsync(
            string contractId,
            string documentId,
            GetAcceptedMemberDocumentRequest request)
    {
        var result = await this.generatedClient
            .GetMemberDocumentsClient()
            .GetAcceptedMemberDocumentAsync(
                contractId,
                documentId,
                request);
        return result.Value;
    }

    // ================================================================
    // OIDC
    // ================================================================

    /// <inheritdoc/>
    public async Task<DiscoveryResponse> GetConfigurationAsync()
    {
        var result = await this.generatedClient
            .GetOidcClient()
            .GetConfigurationAsync();
        return result.Value;
    }

    /// <inheritdoc/>
    public async Task<JwksResponse> GetJwksAsync()
    {
        var result = await this.generatedClient
            .GetOidcClient()
            .GetJwksAsync();
        return result.Value;
    }

    /// <inheritdoc/>
    public async Task GenerateIssuerSigningKeyAsync()
    {
        await this.generatedClient
            .GetOidcClient()
            .GenerateIssuerSigningKeyAsync();
    }

    /// <inheritdoc/>
    public async Task SetIssuerUrlAsync(SetIssuerUrlRequest request)
    {
        await this.generatedClient
            .GetOidcClient()
            .SetIssuerUrlAsync(request);
    }

    /// <inheritdoc/>
    public async Task<OidcIssuerInfo> GetOidcIssuerInfoAsync()
    {
        var result = await this.generatedClient
            .GetOidcClient()
            .GetOidcIssuerInfoAsync();
        return result.Value;
    }

    // ================================================================
    // Proposals
    // ================================================================

    /// <inheritdoc/>
    public async Task<GetProposalResponse>
        GetProposalHistoricalAsync(string proposalId)
    {
        var result = await this.generatedClient
            .GetProposalsClient()
            .GetProposalHistoricalAsync(proposalId);
        return result.Value;
    }

    // ================================================================
    // Runtime Options
    // ================================================================

    /// <inheritdoc/>
    public async Task<GetRuntimeOptionResponse>
        CheckRuntimeOptionAsync(string option)
    {
        var result = await this.generatedClient
            .GetRuntimeOptionsClient()
            .CheckRuntimeOptionAsync(option);
        return result.Value;
    }

    // ================================================================
    // Secrets
    // ================================================================

    /// <inheritdoc/>
    public async Task<ListSecretsResponse> ListSecretsAsync(
        string contractId)
    {
        var result = await this.generatedClient
            .GetSecretsClient()
            .GetSecretsAsync(contractId);
        return result.Value;
    }

    /// <inheritdoc/>
    public async Task<PutSecretResponse> PutSecretAsync(
        string contractId,
        string secretName,
        object request)
    {
        var data = BinaryData.FromObjectAsJson(request);
        var result = await this.generatedClient
            .GetSecretsClient()
            .PutSecretAsync(contractId, secretName, data);
        return result.Value;
    }

    /// <summary>
    /// Gets a secret (wire-level).
    /// </summary>
    /// <param name="contractId">The contract identifier.</param>
    /// <param name="secretName">The secret name.</param>
    /// <param name="request">The attestation request.</param>
    /// <returns>The secret response.</returns>
    public async Task<GetSecretResponse> GetSecretAsync(
        string contractId,
        string secretName,
        GetSecretRequest request)
    {
        var result = await this.generatedClient
            .GetSecretsClient()
            .GetSecretAsync(contractId, secretName, request);
        return result.Value;
    }

    // ================================================================
    // Signing
    // ================================================================

    /// <inheritdoc/>
    public async Task SignPayloadAsync(string contractId)
    {
        await this.generatedClient
            .GetSigningClient()
            .SignPayloadAsync(contractId);
    }

    /// <inheritdoc/>
    public async Task GenerateSigningKeyAsync()
    {
        await this.generatedClient
            .GetSigningClient()
            .GenerateSigningKeyAsync();
    }

    /// <inheritdoc/>
    public async Task GetSigningInfoAsync()
    {
        await this.generatedClient
            .GetSigningClient()
            .GetSigningInfoAsync();
    }

    // ================================================================
    // Token
    // ================================================================

    /// <summary>
    /// Gets an OIDC token (wire-level).
    /// </summary>
    /// <param name="contractId">The contract identifier.</param>
    /// <param name="request">The attestation request.</param>
    /// <returns>The token response.</returns>
    public async Task<GetTokenResponse> GetTokenAsync(
        string contractId,
        GetTokenRequest request)
    {
        var result = await this.generatedClient
            .GetTokenClient()
            .GetTokenAsync(contractId, request);
        return result.Value;
    }

    // ================================================================
    // User Document Runtime Options
    // ================================================================

    /// <inheritdoc/>
    public async Task<GetRuntimeOptionResponse>
        CheckUserDocumentRuntimeOptionAsync(
            string contractId,
            string documentId,
            string option)
    {
        var result = await this.generatedClient
            .GetUserDocumentRuntimeOptionsClient()
            .CheckUserDocumentRuntimeOptionAsync(
                contractId,
                documentId,
                option);
        return result.Value;
    }

    /// <inheritdoc/>
    public async Task EnableUserDocumentRuntimeOptionAsync(
        string contractId,
        string documentId,
        string option)
    {
        await this.generatedClient
            .GetUserDocumentRuntimeOptionsClient()
            .EnableUserDocumentRuntimeOptionAsync(
                contractId,
                documentId,
                option);
    }

    /// <inheritdoc/>
    public async Task DisableUserDocumentRuntimeOptionAsync(
        string contractId,
        string documentId,
        string option)
    {
        await this.generatedClient
            .GetUserDocumentRuntimeOptionsClient()
            .DisableUserDocumentRuntimeOptionAsync(
                contractId,
                documentId,
                option);
    }

    // ================================================================
    // User Documents
    // ================================================================

    /// <inheritdoc/>
    public async Task<ListUserDocumentsResponse>
        ListUserDocumentsAsync(string contractId)
    {
        var result = await this.generatedClient
            .GetUserDocumentsClient()
            .GetUserDocumentsAsync(contractId);
        return result.Value;
    }

    /// <inheritdoc/>
    public async Task PutUserDocumentAsync(
        string contractId,
        string documentId,
        PutUserDocumentRequest request)
    {
        await this.generatedClient
            .GetUserDocumentsClient()
            .PutUserDocumentAsync(contractId, documentId, request);
    }

    /// <inheritdoc/>
    public async Task<GetUserDocumentResponse>
        GetUserDocumentAsync(
            string contractId,
            string documentId)
    {
        var result = await this.generatedClient
            .GetUserDocumentsClient()
            .GetUserDocumentAsync(contractId, documentId);
        return result.Value;
    }

    /// <summary>
    /// Gets an accepted user document (wire-level).
    /// </summary>
    /// <param name="contractId">The contract identifier.</param>
    /// <param name="documentId">The document identifier.</param>
    /// <param name="request">The attestation request.</param>
    /// <returns>The accepted user document response.</returns>
    public async Task<GetAcceptedUserDocumentResponse>
        GetAcceptedUserDocumentAsync(
            string contractId,
            string documentId,
            GetAcceptedUserDocumentRequest request)
    {
        var result = await this.generatedClient
            .GetUserDocumentsClient()
            .GetAcceptedUserDocumentAsync(
                contractId,
                documentId,
                request);
        return result.Value;
    }

    // ================================================================
    // User Identities
    // ================================================================

    /// <inheritdoc/>
    public async Task<ListUserIdentitiesResponse>
        ListUserIdentitiesAsync()
    {
        var result = await this.generatedClient
            .GetUserIdentitiesClient()
            .GetUserIdentitiesAsync();
        return result.Value;
    }

    /// <inheritdoc/>
    public async Task<CheckUserIdentityResponse>
        CheckUserIdentityAsync()
    {
        var result = await this.generatedClient
            .GetUserIdentitiesClient()
            .CheckUserIdentityAsync();
        return result.Value;
    }

    /// <inheritdoc/>
    public async Task<GetUserIdentityResponse>
        GetUserIdentityAsync(string identityId)
    {
        var result = await this.generatedClient
            .GetUserIdentitiesClient()
            .GetUserIdentityAsync(identityId);
        return result.Value;
    }

    // ================================================================
    // User Invitations
    // ================================================================

    /// <inheritdoc/>
    public async Task<ListUserInvitationsResponse>
        ListInvitationsAsync()
    {
        var result = await this.generatedClient
            .GetUserInvitationsClient()
            .GetInvitationsAsync();
        return result.Value;
    }

    /// <inheritdoc/>
    public async Task<GetUserInvitationResponse>
        GetInvitationAsync(string invitationId)
    {
        var result = await this.generatedClient
            .GetUserInvitationsClient()
            .GetInvitationAsync(invitationId);
        return result.Value;
    }

    /// <inheritdoc/>
    public async Task AcceptInvitationAsync(string invitationId)
    {
        await this.generatedClient
            .GetUserInvitationsClient()
            .AcceptInvitationAsync(invitationId);
    }

    // ================================================================
    // User Proposals
    // ================================================================

    /// <inheritdoc/>
    public async Task<GetUserProposalResponse>
        GetUserProposalAsync(string proposalId)
    {
        var result = await this.generatedClient
            .GetUserProposalsClient()
            .GetUserProposalAsync(proposalId);
        return result.Value;
    }

    /// <inheritdoc/>
    public async Task<CreateUserProposalResponse>
        PutUserProposalAsync(
            string proposalId,
            CreateUserProposalRequest request)
    {
        var result = await this.generatedClient
            .GetUserProposalsClient()
            .PutUserProposalAsync(proposalId, request);
        return result.Value;
    }

    /// <inheritdoc/>
    public async Task<UserProposalInfoItem>
        CheckUserProposalAsync(string proposalId)
    {
        var result = await this.generatedClient
            .GetUserProposalsClient()
            .CheckUserProposalAsync(proposalId);
        return result.Value;
    }

    /// <inheritdoc/>
    public async Task WithdrawUserProposalAsync(string proposalId)
    {
        await this.generatedClient
            .GetUserProposalsClient()
            .WithdrawUserProposalAsync(proposalId);
    }

    /// <inheritdoc/>
    public async Task SubmitUserProposalBallotAsync(
        string proposalId,
        SubmitUserProposalBallotRequest request)
    {
        await this.generatedClient
            .GetUserProposalsClient()
            .SubmitUserProposalBallotAsync(proposalId, request);
    }
}