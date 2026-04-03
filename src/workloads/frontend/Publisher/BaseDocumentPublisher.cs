// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Controllers;
using FrontendSvc.CGSClient;
using FrontendSvc.Models;

namespace FrontendSvc.Publisher;

/// <summary>
/// BaseDocumentPublisher class contains some common functionality for all publisher classes.
/// </summary>
/// <typeparam name="T">Type of input details for publishing.</typeparam>
public abstract class BaseDocumentPublisher<T>(
    CollaborationDetails governanceClient,
    bool isAcceptVoteUserDocumentRequired,
    ILogger logger)
    : ICollaborationPublisher<T>
    where T : BaseDataPublishInputDetails
{
    private static readonly string AcceptedState = "Accepted";

    private readonly CollaborationDetails governanceClient = governanceClient;
    private readonly bool isAcceptVoteUserDocumentRequired = isAcceptVoteUserDocumentRequired;

    protected ILogger Logger { get; } = logger;

    protected JsonSerializerOptions JsonSerializerOptions { get; } = new()
    {
        WriteIndented = true,
    };

    public abstract Task Publish(string id, T input, string contractId);

    protected async Task PublishDocument(
        string documentId,
        CreateUserDocument userDocument)
    {
        await this.CreateUserDocument(documentId, userDocument);

        this.Logger.LogInformation(
            $"Successfully created user document with id {documentId}");

        var fetchedUserDocument = await this.GetUserDocument(documentId);

        this.Logger.LogInformation(
            $"Successfully fetched user document with id {documentId}, version " +
            $"{fetchedUserDocument.Version}, proposalId {fetchedUserDocument.ProposalId} " +
            $"and state {fetchedUserDocument.State}");

        // If the document is in "Accepted" state already then no further action is required.
        if (string.Equals(
            fetchedUserDocument.State,
            AcceptedState,
            StringComparison.InvariantCultureIgnoreCase))
        {
            this.Logger.LogInformation(
                $"User document with id {documentId} is already in {AcceptedState} state. " +
                $"No further action is required.");
        }
        else
        {
            var proposalId = fetchedUserDocument.ProposalId;

            // No need to propose if there is an existing proposal id.
            if (string.IsNullOrWhiteSpace(proposalId))
            {
                proposalId = await this.ProposeUserDocument(
                    documentId,
                    new UserDocumentProposal
                    {
                        Version = fetchedUserDocument.Version
                    });

                this.Logger.LogInformation(
                    $"Successfully proposed user document with id {documentId} and version " +
                    $"{fetchedUserDocument.Version} and proposal id {proposalId}");
            }

            if (this.isAcceptVoteUserDocumentRequired)
            {
                var vote = Vote.Accept;
                await this.VoteUserDocument(documentId, proposalId, vote);

                this.Logger.LogInformation(
                    $"Successfully voted {vote} user document with id {documentId} and version " +
                    $"{fetchedUserDocument.Version}");
            }
        }
    }

    protected async Task CreateUserDocument(string documentId, CreateUserDocument userDocument)
    {
        try
        {
            var userDocumentJsonObject = JsonSerializer.SerializeToNode(
                userDocument,
                this.JsonSerializerOptions) as JsonObject ?? [];

            await this.governanceClient.CreateUserDocumentAsync(
                documentId,
                userDocumentJsonObject,
                this.Logger);
        }
        catch (ApiException ex)
        {
            // 409 -> User document already exists but state is not yet "Accepted".
            // 405 -> User document already exists and "Accepted".
            if (ex.StatusCode == HttpStatusCode.Conflict
                || ex.StatusCode == HttpStatusCode.MethodNotAllowed)
            {
                this.Logger.LogInformation(
                    $"User document with document id {documentId} already exists but we continue. " +
                    $"Error: \"{ex.Message}\". Status Code: {ex.StatusCode}");
            }
            else
            {
                throw;
            }
        }
    }

    protected Task<GetUserDocument> GetUserDocument(string documentId)
    {
        return this.governanceClient.GetUserDocumentAsync(
            documentId,
            this.Logger);
    }

    protected async Task<string> ProposeUserDocument(
        string documentId,
        UserDocumentProposal userDocumentProposal)
    {
        var userDocumentProposalJsonObject = JsonSerializer.SerializeToNode(
            userDocumentProposal,
            this.JsonSerializerOptions) as JsonObject ?? [];

        try
        {
            var proposal = await this.governanceClient.ProposeUserDocumentAsync(
                    documentId,
                    userDocumentProposalJsonObject,
                    this.Logger);

            return proposal.ProposalId;
        }
        catch (ApiException ex)
        {
            // 409 -> Proposal already exists for this document.
            if (ex.StatusCode == HttpStatusCode.Conflict)
            {
                this.Logger.LogInformation(
                    $"Proposal for user document with document id {documentId} already exists. " +
                    $"Error: \"{ex.Message}\". Status Code: {ex.StatusCode}");

                var fetchedUserDocument = await this.GetUserDocument(documentId);
                return fetchedUserDocument.ProposalId;
            }
            else
            {
                throw;
            }
        }
    }

    protected async Task VoteUserDocument(string documentId, string proposerId, Vote vote)
    {
        try
        {
            await this.governanceClient.VoteDocumentProposalAsync(
                documentId,
                proposerId,
                vote.ToString().ToLower(),
                this.Logger);
        }
        catch (ApiException ex)
        {
            // 409 -> Vote already exists for this proposal, proposal is not open.
            if (ex.StatusCode == HttpStatusCode.Conflict)
            {
                this.Logger.LogInformation(
                    $"Vote for user document with document id {documentId} and proposal id " +
                    $"{proposerId} already exists. Error: \"{ex.Message}\". " +
                    $"Status Code: {ex.StatusCode}");
            }
            else
            {
                throw;
            }
        }
    }
}
