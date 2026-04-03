// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure;
using Controllers;
using FrontendSvc.Models;
using FrontendSvc.Utils.Token;

namespace FrontendSvc.ConsortiumManagerClient;

public static class ConsortiumManagerClientExtensions
{
    private static readonly string ODataErrorCode = "ConsortiumManagerRequestFailed";

    public static async Task ActivateUserAsync(
        this HttpClient client,
        CollaborationDetails collaborationDetails,
        string invitationId,
        ILogger logger)
    {
        var requestBody = new JsonObject
        {
            ["ccfEndpoint"] = collaborationDetails.ConsortiumEndpoint,
            ["ccfServiceCertPem"] =
                collaborationDetails.ConsortiumServiceCertificatePem,
            ["invitationId"] = invitationId
        };
        using var content = new StringContent(
            requestBody.ToJsonString(),
            Encoding.UTF8,
            "application/json");
        try
        {
            await HttpClientUtilities.PerformHttpCallWithErrorHandling(
                httpClient => httpClient.HttpPostAsync(
                    "users/activateUserInvitation",
                    logger,
                    body: content),
                client,
                ODataErrorCode);
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            logger.LogWarning(
                ex,
                "Received PreconditionFailed when activating user for invitation {InvitationId}. " +
                "User may already be activated.",
                invitationId);
        }
    }

    public static Task<ConsortiumManagerReport> GetReportAsync(
        this HttpClient client,
        ILogger logger)
    {
        return HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpGetAsync<ConsortiumManagerReport>(
                "report",
                logger),
            client,
            ODataErrorCode);
    }
}