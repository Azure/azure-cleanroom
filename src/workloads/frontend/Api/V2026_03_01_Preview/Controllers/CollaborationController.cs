// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json.Nodes;
using Controllers;
using FrontendSvc.AnalyticsClient;
using FrontendSvc.Api.Common;
using FrontendSvc.CcfClient;
using FrontendSvc.CGSClient;
using FrontendSvc.ConsortiumManagerClient;
using FrontendSvc.MembershipManagerClient;
using FrontendSvc.Models;
using FrontendSvc.Models.CCF;
using FrontendSvc.Publisher.Factory;
using FrontendSvc.Utils.Token;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace FrontendSvc.Api.V2026_03_01_Preview.Controllers;

/// <summary>
/// Collaboration controller for API version 2026-03-01-preview.
/// </summary>
[ApiController]
public class CollaborationController : CollaborationControllerBase
{
    private readonly ILogger<CollaborationController> logger;
    private readonly ClientManager clientManager;
    private readonly ICollaborationPublisherFactory collaborationPublisherFactory;
    private readonly IHostEnvironment hostEnvironment;

    public CollaborationController(
        ILogger<CollaborationController> logger,
        ClientManager clientManager,
        ICollaborationPublisherFactory collaborationPublisherFactory,
        IHostEnvironment hostEnvironment)
    {
        this.logger = logger;
        this.clientManager = clientManager;
        this.collaborationPublisherFactory = collaborationPublisherFactory;
        this.hostEnvironment = hostEnvironment;
    }

    internal WebContext WebContext =>
        (WebContext)this.ControllerContext.HttpContext.Items[
            WebContext.WebContextIdentifer]!;

    [HttpPost("/collaborations/{collaborationId}/analytics/queries/{documentId}/run")]
    public async Task<IActionResult> RunQuery(
        [FromRoute] string collaborationId,
        [FromRoute] string documentId,
        [FromBody] Models.QueryRunInput requestBody)
    {
        var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
        this.logger.LogInformation(
            $"Running query {documentId} for collaboration {collaborationId}");

        var analyticsClient = await this.clientManager.GetAnalyticsClientAsync(
            idToken,
            collaborationId,
            this.logger);

        // Convert versioned model to internal model.
        var internalRequestBody = new FrontendSvc.Models.QueryRunInput
        {
            RunId = requestBody.RunId,
            StartDate = requestBody.StartDate,
            EndDate = requestBody.EndDate,
            UseOptimizer = requestBody.UseOptimizer,
            DryRun = requestBody.DryRun
        };

        var result = await analyticsClient.RunQueryAsync(
            documentId,
            idToken,
            internalRequestBody,
            this.logger);

        return this.Ok(result);
    }

    [HttpGet("/collaborations/{collaborationId}/analytics/runs/{jobId}")]
    public async Task<IActionResult> GetRunResult(
        [FromRoute] string collaborationId,
        [FromRoute] string jobId)
    {
        var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
        this.logger.LogInformation(
            $"Getting query run status for jobId {jobId} " +
            $"in collaboration {collaborationId}");

        var analyticsClient = await this.clientManager.GetAnalyticsClientAsync(
            idToken,
            collaborationId,
            this.logger);

        var result = await analyticsClient.GetQueryRunResultAsync(
            jobId,
            idToken,
            this.logger);

        return this.Ok(result);
    }

    [HttpGet("/collaborations/{collaborationId}/analytics/queries/{documentId}/runs")]
    public async Task<IActionResult> GetQueryRunHistory(
        [FromRoute] string collaborationId,
        [FromRoute] string documentId)
    {
        var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
        this.logger.LogInformation(
          $"Getting query run history for documentId {documentId} " +
          $"in collaboration {collaborationId}");

        var analyticsClient = await this.clientManager.GetAnalyticsClientAsync(
            idToken,
            collaborationId,
            this.logger);

        var result = await analyticsClient.GetQueryRunHistoryAsync(
            documentId,
            idToken,
            this.logger);

        return this.Ok(result);
    }

    [HttpPut("/collaborations/{collaborationId}/analytics/secrets/{secretName}")]
    public async Task<IActionResult> SetSecret(
        [FromRoute] string collaborationId,
        [FromRoute] string secretName,
        [FromBody] Models.SecretValueRequest requestBody)
    {
        var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
        this.logger.LogInformation(
            $"Setting secret {secretName} for collaboration {collaborationId}");

        var governanceClient = await this.clientManager.GetCollaborationAsync(
            idToken,
            collaborationId);

        var result = await governanceClient.SetSecretAsync(
            governanceClient.AnalyticsWorkloadId,
            secretName,
            requestBody.SecretConfig,
            this.logger);

        return this.Ok(result);
    }

    [HttpGet("/collaborations/{collaborationId}/analytics/auditevents")]
    public async Task<IActionResult> GetAuditEvents(
        [FromRoute] string collaborationId,
        [FromQuery] string? scope,
        [FromQuery] string? from_seqno,
        [FromQuery] string? to_seqno)
    {
        var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
        this.logger.LogInformation(
            $"Getting audit events for analytics contract.");

        var governanceClient = await this.clientManager.GetCollaborationAsync(
            idToken,
            collaborationId);

        var auditEvents = await governanceClient.GetAuditEventsAsync(
            governanceClient.AnalyticsWorkloadId,
            this.logger,
            scope: scope,
            fromSeqno: from_seqno,
            toSeqno: to_seqno);

        return this.Ok(auditEvents);
    }

    [HttpGet("/collaborations/{collaborationId}/analytics")]
    public async Task<IActionResult> GetContract(
        [FromRoute] string collaborationId)
    {
        var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
        this.logger.LogInformation("Getting analytics workload details from cgs");

        var governanceClient = await this.clientManager.GetCollaborationAsync(
            idToken,
            collaborationId);

        var workload = await governanceClient.GetContractAsync(
            governanceClient.AnalyticsWorkloadId,
            this.logger);

        return this.Ok(workload);
    }

    [HttpPost("/collaborations/{collaborationId}/analytics/queries/{documentId}/vote")]
    public async Task<IActionResult> VoteQueryDocument(
        [FromRoute] string collaborationId,
        [FromRoute] string documentId,
        [FromBody] Models.VoteRequest request)
    {
        var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
        this.logger.LogInformation("Voting on document proposal in cgs");

        var governanceClient = await this.clientManager.GetCollaborationAsync(
            idToken,
            collaborationId);

        await governanceClient.VoteDocumentProposalAsync(
            documentId,
            request.ProposalId,
            request.VoteAction,
            this.logger);

        return this.NoContent();
    }

    [HttpGet("/collaborations/{collaborationId}/invitations/{invitationId}")]
    public async Task<IActionResult> GetInvitationDetails(
        [FromRoute] string collaborationId,
        [FromRoute] string invitationId)
    {
        var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
        this.logger.LogInformation(
            $"Getting details for invitation {invitationId} in collaboration {collaborationId}");

        var governanceClient = await this.clientManager.GetCollaborationAsync(
            idToken,
            collaborationId);

        var invitationDetails = await governanceClient.GetInvitationAsync(
            invitationId,
            this.logger);

        return this.Ok(invitationDetails);
    }

    [HttpGet("/collaborations/{collaborationId}/invitations")]
    public async Task<ActionResult> ListInvitations(
        [FromRoute] string collaborationId,
        [FromQuery] bool pendingOnly = false)
    {
        var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
        this.logger.LogInformation(
            $"Listing invitations for collaboration {collaborationId} " +
            $"(pendingOnly: {pendingOnly})");

        try
        {
            var membershipMgrClient =
                await this.clientManager.GetMembershipManagerClientAsync();
            var collaborationDetails =
                await membershipMgrClient.GetCollaborationAsync(
                    idToken,
                    collaborationId,
                    this.logger);

            if (pendingOnly &&
                !string.IsNullOrEmpty(collaborationDetails.UserStatus) &&
                collaborationDetails.UserStatus.Equals(
                    "Active",
                    StringComparison.OrdinalIgnoreCase))
            {
                // User is already active — no pending invitations to return.
                return this.Ok(new { invitations = Array.Empty<object>() });
            }

            var governanceClient = await this.clientManager.GetCollaborationAsync(
                idToken,
                collaborationId);

            var allInvitations = await governanceClient.ListInvitationsAsync(
                this.logger);

            var (userIdentity, userEmail) =
                TokenUtilities.ExtractUserInfoFromToken(idToken, this.logger);

            var userInvitations = allInvitations.Value.Where(
                invitation =>
                {
                    if (invitation.Claims != null)
                    {
                        // Check for email-based users (preferred_username claim).
                        if (!string.IsNullOrEmpty(userEmail))
                        {
                            if (invitation.Claims.TryGetValue(
                                "preferred_username",
                                out var usernames) && usernames != null)
                            {
                                if (usernames.Any(u =>
                                    u.Equals(
                                        userEmail,
                                        StringComparison.OrdinalIgnoreCase)))
                                {
                                    return true;
                                }
                            }
                        }
                        else
                        {
                            this.logger.LogWarning(
                                $"Invitation {invitation.InvitationId} does not have an email claim"
                                + $" and the user does not have an email in the token. " +
                                $"Skipping email matching for this invitation.");
                        }
                    }
                    else
                    {
                        this.logger.LogWarning(
                            $"Invitation {invitation.InvitationId} has no claims. " +
                            $"Skipping user matching for this invitation.");
                    }

                    return false;
                }).ToList();
            this.logger.LogInformation(
                $"Found {userInvitations.Count} invitations for user " +
                $"out of {allInvitations.Value.Count} total");

            var invitationSummaries = userInvitations
                .Select(i => new
                {
                    invitationId = i.InvitationId,
                    accountType = i.AccountType,
                    status = i.Status
                })
                .ToList();

            return this.Ok(new { invitations = invitationSummaries });
        }
        catch (Exception ex)
        {
            this.logger.LogError(
                ex,
                $"Failed to list invitations for collaboration {collaborationId}");
            throw;
        }
    }

    [HttpGet("/collaborations/{collaborationId}/analytics/datasets/{datasetId}/skrpolicy")]
    public async Task<IActionResult> GetContractSkrPolicy(
        [FromRoute] string collaborationId,
        [FromRoute] string datasetId)
    {
        var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
        this.logger.LogInformation(
            $"Getting SKR policy for analytics workload.");

        var governanceClient = await this.clientManager.GetCollaborationAsync(
            idToken,
            collaborationId);

        var skrPolicy = await governanceClient.GetSkrPolicyAsync(
            governanceClient.AnalyticsWorkloadId,
            datasetId,
            this.logger);

        if (skrPolicy == null)
        {
            this.logger.LogError(
                $"SKR policy is null for collaboration {collaborationId}.");
            return this.NotFound(new
            {
                error = "SkrPolicyNotFound",
                message = $"No SKR policy found for collaboration {collaborationId}"
            });
        }

        return this.Ok(skrPolicy);
    }

    [HttpGet("/collaborations/{collaborationId}/oidc/issuerInfo")]
    public async Task<IActionResult> GetOidcIssuerInfo(
        [FromRoute] string collaborationId)
    {
        try
        {
            var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
            this.logger.LogInformation(
                $"Getting OIDC issuer info for collaboration {collaborationId}");

            var governanceClient = await this.clientManager.GetCollaborationAsync(
                idToken,
                collaborationId);

            var oidcIssuerInfo = await governanceClient.GetOidcIssuerInfoAsync(
                this.logger);

            if (oidcIssuerInfo == null)
            {
                this.logger.LogError(
                    $"OIDC issuer info is null for collaboration {collaborationId}");
                return this.NotFound(new
                {
                    error = "OidcIssuerInfoNotFound",
                    message = $"No OIDC issuer info found for collaboration {collaborationId}"
                });
            }

            this.logger.LogInformation(
                $"Successfully retrieved OIDC issuer info for collaboration {collaborationId}");

            return this.Ok(oidcIssuerInfo);
        }
        catch (ApiException ex)
        {
            this.logger.LogError(
                $"Error getting OIDC issuer info for collaboration {collaborationId}: {ex.Message}");
            return this.StatusCode(
                (int)ex.StatusCode,
                $"Error getting OIDC issuer info: {ex.Message}");
        }
    }

    [HttpPost("/collaborations/{collaborationId}/oidc/setIssuerUrl")]
    public async Task<IActionResult> SetOidcIssuerUrl(
        [FromRoute] string collaborationId,
        [FromBody][Required] Models.SetIssuerUrlInput setIssuerUrlInput)
    {
        try
        {
            var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
            this.logger.LogInformation(
                $"Settings OIDC issuer URL for collaboration {collaborationId}");

            var governanceClient = await this.clientManager.GetCollaborationAsync(
                idToken,
                collaborationId);

            await governanceClient.SetIssuerUrl(setIssuerUrlInput.Url, this.logger);

            return this.Ok(new SetIssuerUrlResponse
            {
                Url = setIssuerUrlInput.Url,
                Message = "Successfully set OIDC issuer URL."
            });
        }
        catch (ApiException ex)
        {
            this.logger.LogError(
                $"Error setting OIDC issuer URL for collaboration {collaborationId}: {ex.Message}");
            return this.StatusCode(
                (int)ex.StatusCode,
                $"Error setting OIDC issuer URL: {ex.Message}");
        }
    }

    [HttpGet("/collaborations/{collaborationId}/oidc/keys")]
    public async Task<IActionResult> GetOidcKeys(
        [FromRoute] string collaborationId)
    {
        try
        {
            var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
            this.logger.LogInformation(
                $"Getting OIDC signing keys for collaboration {collaborationId}");

            var collaborationDetails = await this.clientManager.GetCollaborationAsync(
                idToken,
                collaborationId);

            var ccfClient = this.clientManager.GetCcfClient(collaborationDetails);
            var oidcKeys = await ccfClient.GetOidcKeysAsync(this.logger);

            this.logger.LogInformation(
                $"Successfully retrieved OIDC keys for collaboration {collaborationId}");

            return this.Ok(oidcKeys);
        }
        catch (ApiException ex)
        {
            this.logger.LogError(
                $"Error getting OIDC keys for collaboration {collaborationId}: {ex.Message}");
            return this.StatusCode(
                (int)ex.StatusCode,
                $"Error getting OIDC keys: {ex.Message}");
        }
    }

    [HttpGet("/collaborations/{collaborationId}/analytics/datasets")]
    public async Task<IActionResult> ListDatasets(
        [FromRoute] string collaborationId)
    {
        var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
        this.logger.LogInformation(
            $"Listing user documents for collaboration {collaborationId}");

        var governanceClient = await this.clientManager.GetCollaborationAsync(
            idToken,
            collaborationId);

        var userDocuments = await governanceClient.ListDatasetsAsync(
            this.logger);

        return this.Ok(userDocuments);
    }

    [HttpGet("/collaborations/{collaborationId}/analytics/queries")]
    public async Task<IActionResult> ListQueries(
        [FromRoute] string collaborationId)
    {
        var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
        this.logger.LogInformation(
            $"Listing user documents for collaboration {collaborationId}");

        var governanceClient = await this.clientManager.GetCollaborationAsync(
            idToken,
            collaborationId);

        var userDocuments = await governanceClient.ListQueriesAsync(
            this.logger);

        return this.Ok(userDocuments);
    }

    [HttpGet("/collaborations/{collaborationId}/analytics/datasets/{documentId}/queries")]
    public async Task<IActionResult> ListQueriesByDataset(
        [FromRoute] string collaborationId,
        [FromRoute] string documentId)
    {
        var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
        this.logger.LogInformation(
            $"Listing queries that use dataset {documentId} for collaboration {collaborationId}");

        var governanceClient = await this.clientManager.GetCollaborationAsync(
            idToken,
            collaborationId);

        var userdocuments = await governanceClient.ListQueriesbyDatasetAsync(
            documentId,
            this.logger);

        return this.Ok(userdocuments);
    }

    [HttpGet("/collaborations/{collaborationId}/analytics/datasets/{documentId}")]
    public async Task<IActionResult> GetDataset(
        [FromRoute] string collaborationId,
        [FromRoute] string documentId)
    {
        var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
        this.logger.LogInformation(
            $"Getting dataset {documentId} for collaboration {collaborationId}");
        var governanceClient = await this.clientManager.GetCollaborationAsync(
            idToken,
            collaborationId);

        var dataset = await governanceClient.GetDocumentAsync(
            documentId,
            this.logger);

        return this.Ok(GetDatasetDocument.FromDatasetDetails(dataset));
    }

    [HttpGet("/collaborations/{collaborationId}/analytics/queries/{documentId}")]
    public async Task<IActionResult> GetQueryDetails(
        [FromRoute] string collaborationId,
        [FromRoute] string documentId)
    {
        var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
        this.logger.LogInformation(
            $"Getting query {documentId} for collaboration {collaborationId}");
        var governanceClient = await this.clientManager.GetCollaborationAsync(
            idToken,
            collaborationId);

        var queryDetails = await governanceClient.GetDocumentAsync(
            documentId,
            this.logger);

        var queryDocument = GetQueryDocument.FromDocumentResponse(queryDetails);

        return this.Ok(queryDocument);
    }

    [HttpGet("/collaborations/{collaborationId}/consent/{documentId}")]
    public async Task<IActionResult> CheckDocumentConsent(
        [FromRoute] string collaborationId,
        [FromRoute] string documentId)
    {
        var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
        this.logger.LogInformation(
            $"Checking execution status for document {documentId} " +
            $"in collaboration {collaborationId}");

        var governanceClient = await this.clientManager.GetCollaborationAsync(
            idToken,
            collaborationId);

        var executionStatus = await governanceClient.CheckDocumentExecutionStatusAsync(
            documentId,
            this.logger);

        return this.Ok(executionStatus);
    }

    [HttpPut("/collaborations/{collaborationId}/consent/{documentId}")]
    public async Task<IActionResult> SetDocumentConsent(
        [FromRoute] string collaborationId,
        [FromRoute] string documentId,
        [FromBody] Models.ConsentActionRequest requestBody)
    {
        var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
        this.logger.LogInformation(
            $"Setting execution consent to '{requestBody.ConsentAction}' for " +
            $"document {documentId} in collaboration {collaborationId}");

        var action = requestBody.ConsentAction;

        var governanceClient = await this.clientManager.GetCollaborationAsync(
            idToken,
            collaborationId);
        await governanceClient.SetDocumentConsentAsync(
            documentId,
            action,
            this.logger);

        return this.NoContent();
    }

    [HttpPost("/collaborations/{collaborationId}/invitations/{invitationId}/accept")]
    public async Task<ActionResult> AcceptInvitation(
        [FromRoute] string collaborationId,
        [FromRoute] string invitationId)
    {
        this.logger.LogInformation(
            $"Accepting invitation {invitationId} for collaboration {collaborationId}.");

        var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);

        var membershipMgrClient =
            await this.clientManager.GetMembershipManagerClientAsync();

        var governanceClient = await this.clientManager.GetCollaborationAsync(
            idToken,
            collaborationId);

        bool done = false;
        try
        {
            while (!done)
            {
                var invitation = await governanceClient.GetInvitationAsync(
                    invitationId,
                    this.logger);
                this.logger.LogInformation(
                    $"Invitation {invitationId} status: {invitation.Status}.");
                switch (invitation.Status)
                {
                    case "Finalized":
                        await membershipMgrClient.UpdateCollaborationAsync(
                            idToken,
                            collaborationId,
                            "Active",
                            this.logger);
                        done = true;
                        break;

                    case "Accepted":
                        var consortiumMgrClient =
                        await this.clientManager.GetConsortiumManagerClientAsync();
                        await consortiumMgrClient.ActivateUserAsync(
                            governanceClient,
                            invitationId,
                            this.logger);
                        break;

                    case "Open":
                        await governanceClient.AcceptInvitationAsync(
                            invitationId,
                            this.logger);
                        break;
                }
            }
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            this.logger.LogError(
                $"Invitation {invitationId} not found " +
                $"for collaboration {collaborationId}");
            return this.NotFound(new
            {
                error = "InvitationNotFound",
                message = $"Invitation {invitationId} not found"
            });
        }

        this.logger.LogInformation(
            $"Successfully accepted invitation {invitationId} for collaboration {collaborationId}.");

        return this.NoContent();
    }

    [HttpGet("/collaborations")]
    public async Task<ActionResult> ListCollaborations(
        [FromQuery] bool activeOnly = false)
    {
        var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
        this.logger.LogInformation(
            $"Listing collaborations for the user (activeOnly: {activeOnly})");

        var membershipMgrClient = await this.clientManager.GetMembershipManagerClientAsync();

        var listCollaborationDetails = await membershipMgrClient.ListCollaborationAsync(
                idToken,
                this.logger,
                activeOnly: activeOnly);

        this.logger.LogInformation(
            $"Fetched {listCollaborationDetails.Collaborations.Count} " +
            $"collaborations.");

        return this.Ok(listCollaborationDetails);
    }

    [HttpGet("/collaborations/{collaborationId}")]
    public async Task<IActionResult> GetCollaboration(
        [FromRoute] string collaborationId,
        [FromQuery] bool activeOnly = false)
    {
        var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
        this.logger.LogInformation(
            $"Getting collaboration details for id: {collaborationId} " +
            $"(activeOnly: {activeOnly})");

        var membershipMgrClient = await this.clientManager.GetMembershipManagerClientAsync();

        var collaboration = await membershipMgrClient.GetCollaborationAsync(
            idToken,
            collaborationId,
            this.logger,
            activeOnly: activeOnly);

        var result = new CollaborationOutput
        {
            CollaborationId = collaboration.CollaborationId,
            CollaborationName = collaboration.CollaborationName,
            UserStatus = collaboration.UserStatus
        };

        return this.Ok(result);
    }

    [HttpPost("/collaborations/{collaborationId}/analytics/queries/{documentId}/publish")]
    public async Task<IActionResult> PublishQuery(
        [FromRoute] string collaborationId,
        [FromRoute] string documentId,
        [FromBody][Required] QueryDetails queryDetails)
    {
        try
        {
            var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);

            this.logger.LogInformation($"Fetching the collaboration details {collaborationId}.");

            var governanceClient = await this.clientManager.GetCollaborationAsync(
                idToken,
                collaborationId);

            this.logger.LogInformation($"Publishing query with id {documentId}.");

            var publishQueryInputDetails = PublishQueryInputDetails.FromQueryDetails(queryDetails);

            var queryDocumentPublisher =
                this.collaborationPublisherFactory.GetQueryDocumentPublisher(governanceClient);
            await queryDocumentPublisher.Publish(
                documentId,
                publishQueryInputDetails,
                governanceClient.AnalyticsWorkloadId);

            this.logger.LogInformation(
                $"Successfully published query with id {documentId}.");

            return this.NoContent();
        }
        catch (ApiException ex)
        {
            this.logger.LogError(
                $"Error publishing query with id {documentId}: {ex} and message: {ex.Message}");
            return this.StatusCode((int)ex.StatusCode, "Error publishing query: " + ex.Message);
        }
        catch (Exception ex)
        {
            this.logger.LogError(
                $"Error publishing query with id {documentId}: {ex} and message: {ex.Message}");
            return this.StatusCode(500, "Error publishing query: " + ex.Message);
        }
    }

    [HttpPost("/collaborations/{collaborationId}/analytics/datasets/{documentId}/publish")]
    public async Task<IActionResult> PublishDataset(
        [FromRoute] string collaborationId,
        [FromRoute] string documentId,
        [FromBody][Required] DatasetDetails datasetDetails)
    {
        try
        {
            var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
            var collaboratorId = TokenUtilities
                .ExtractUserInfoFromToken(idToken, this.logger)
                .userIdentity?.ObjectId ?? string.Empty;

            this.logger.LogInformation($"Fetching the collaboration details {collaborationId}.");

            var governanceClient = await this.clientManager.GetCollaborationAsync(
                idToken,
                collaborationId);

            var publishDatasetInputDetails = new PublishDatasetInputDetails
            {
                CollaboratorId = collaboratorId,
                Data = DatasetSpecification.FromDatasetDetails(datasetDetails)
            };

            this.logger.LogInformation($"Publishing dataset with id {documentId}.");

            var datasetDocumentPublisher =
                this.collaborationPublisherFactory.GetDatasetDocumentPublisher(governanceClient);
            await datasetDocumentPublisher.Publish(
                documentId,
                publishDatasetInputDetails,
                governanceClient.AnalyticsWorkloadId);

            this.logger.LogInformation(
                $"Successfully published dataset with id {documentId}.");

            return this.NoContent();
        }
        catch (ApiException ex)
        {
            this.logger.LogError(
                $"Error publishing dataset with id {documentId}: {ex} and message: {ex.Message}");
            return this.StatusCode((int)ex.StatusCode, "Error publishing dataset: " + ex.Message);
        }
        catch (Exception ex)
        {
            this.logger.LogError(
                $"Error publishing dataset with id {documentId}: {ex} and message: {ex.Message}");
            return this.StatusCode(500, "Error publishing dataset: " + ex.Message);
        }
    }

    [HttpGet("/collaborations/{collaborationId}/report")]
    public async Task<IActionResult> GetCollaborationReport(
        [FromRoute] string collaborationId)
    {
        var idToken = ValidateAndGetToken(this.Request.Headers.Authorization);
        this.logger.LogInformation(
            $"Getting collaboration report for collaboration {collaborationId}");

        var governanceClient = await this.clientManager.GetCollaborationAsync(
            idToken,
            collaborationId);

        var contract = await governanceClient.GetContractAsync(
            governanceClient.AnalyticsWorkloadId,
            this.logger);

        if (contract.Data == null)
        {
            this.logger.LogError(
                $"Contract {governanceClient.AnalyticsWorkloadId} has null data.");
            return this.BadRequest(new
            {
                error = "ContractDataEmpty",
                message = $"Contract {governanceClient.AnalyticsWorkloadId} has null data."
            });
        }

        string dataString = contract.Data.ToString() ?? string.Empty;
        JsonNode? contractDataNode = JsonNode.Parse(dataString);
        if (contractDataNode == null)
        {
            this.logger.LogError("Failed to parse contract data.");
            return this.BadRequest(new
            {
                error = "InvalidContractData",
                message = "Failed to parse contract data."
            });
        }

        string? recoveryAgentEndpoint = contractDataNode["ccrgovServiceCertDiscovery"]?
        ["endpoint"]?.GetValue<string>();
        if (string.IsNullOrEmpty(recoveryAgentEndpoint))
        {
            this.logger.LogError("Recovery agent endpoint not found in contract data.");
            return this.BadRequest(new
            {
                error = "RecoveryAgentEndpointNotFound",
                message = "Recovery agent endpoint not found in contract data."
            });
        }

        this.logger.LogInformation(
            $"Using recovery agent endpoint: {recoveryAgentEndpoint}.");

        HttpClient httpClient = this.clientManager.GetCcfRecoveryClient(
            recoveryAgentEndpoint);
        var attestationReport = await httpClient.HttpGetAsync<CcfAttestationReportResponse>(
            recoveryAgentEndpoint,
            this.logger);

        if (attestationReport == null)
        {
            this.logger.LogError("Recovery agent returned empty attestation report.");
            return this.StatusCode(
                500,
                new
                {
                    error = "EmptyResponse",
                    message = "Recovery agent returned empty attestation report."
                });
        }

        this.logger.LogInformation(
            $"Successfully retrieved CGS attestation report for " +
            $"collaboration {collaborationId}.");

        var consortiumMgrClient = await this.clientManager.GetConsortiumManagerClientAsync();
        var consortiumMgrReport = await consortiumMgrClient.GetReportAsync(
            this.logger);

        var result = new CollaborationReportResponse
        {
            Cgs = new CgsReportInfo
            {
                CgsEndpoint = governanceClient.ConsortiumEndpoint,
                RecoveryAgentEndpoint = recoveryAgentEndpoint,
                Report = attestationReport
            },
            ConsortiumManager = new ConsortiumManagerReportInfo
            {
                Endpoint = this.clientManager.ConsortiumManagerEndpoint,
                Report = consortiumMgrReport
            }
        };

        return this.Ok(result);
    }

    // In the managed case, the analytics workload id is pre-configured using the Frontend config.
    // This is just an override and should be used only for testing purposes.
    [HttpPost("/collaborations/{collaborationId}/analytics/{workloadId}")]
    public async Task<IActionResult> SetAnalyticsWorkloadId(
        [FromRoute] string collaborationId,
        [FromRoute] string workloadId)
    {
        if (!this.hostEnvironment.IsDevelopment())
        {
            return this.StatusCode(
                (int)HttpStatusCode.Forbidden,
                "This endpoint is only available in Development environment.");
        }

        ValidateAndGetToken(this.Request.Headers.Authorization);
        this.logger.LogInformation("Overriding the analytics workload id.");

        this.clientManager.UpdateAnalyticsWorkloadId(workloadId);
        return this.NoContent();
    }
}
