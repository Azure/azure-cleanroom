// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Controllers;
using FrontendSvc.Models;
using FrontendSvc.Utils.Token;

namespace FrontendSvc.MembershipManagerClient;

public static class MembershipManagerClientExtensions
{
    private static readonly string ODataErrorCode = "MembershipManagerRequestFailed";

    public static async Task<ListCollaborationsOutput> ListCollaborationAsync(
        this HttpClient client,
        string userToken,
        ILogger logger,
        bool activeOnly = false)
    {
        var (userIdentity, userEmail) = TokenUtilities.ExtractUserInfoFromToken(userToken, logger);
        var headers = GetHeaders(userEmail, userIdentity);
        ListCollaborationsOutput? results = null;

        var path = "collaborations";
        try
        {
            results = await HttpClientUtilities.PerformHttpCallWithErrorHandling(
                httpClient => httpClient.HttpGetAsync<ListCollaborationsOutput>(
                    path,
                    logger,
                    headers: headers),
                client,
                ODataErrorCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get collaborations from the membership manager.");
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    ODataErrorCode,
                    "Failed to get collaborations from the membership manager."));
        }

        var collaborations = results?.Collaborations ?? new List<CollaborationOutput>();
        if (activeOnly)
        {
            collaborations = collaborations
                .Where(c => c.UserStatus == "Active")
                .ToList();
        }

        return new ListCollaborationsOutput
        {
            Collaborations = collaborations
        };
    }

    public static async Task<GetCollaborationOutput> GetCollaborationAsync(
        this HttpClient client,
        string userToken,
        string collaborationId,
        ILogger logger,
        bool activeOnly = false)
    {
        var (userIdentity, userEmail) = TokenUtilities.ExtractUserInfoFromToken(userToken, logger);
        var headers = GetHeaders(userEmail, userIdentity);

        try
        {
            var path = $"collaborations/{collaborationId}";

            var result = await HttpClientUtilities.PerformHttpCallWithErrorHandling(
                httpClient => httpClient.HttpGetAsync<GetCollaborationOutput>(
                    path,
                    logger,
                    headers: headers),
                client,
                ODataErrorCode);

            if (result != null)
            {
                if (activeOnly && result.UserStatus != "Active")
                {
                    logger.LogInformation(
                        $"Collaboration {collaborationId} filtered out " +
                        $"(activeOnly=true, status={result.UserStatus})");
                }
                else
                {
                    logger.LogInformation(
                        $"Successfully retrieved collaboration " +
                        $"{collaborationId} by identity");
                    return result;
                }
            }
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogInformation(
                $"Collaboration {collaborationId} not found");
        }

        throw new ApiException(
            HttpStatusCode.NotFound,
            new ODataError(
                "CollaborationNotFound",
                $"Collaboration {collaborationId} not found."));
    }

    public static Task UpdateCollaborationAsync(
        this HttpClient client,
        string userToken,
        string collaborationId,
        string userStatus,
        ILogger logger)
    {
        var (userIdentity, userEmail) = TokenUtilities.ExtractUserInfoFromToken(userToken, logger);

        var content = new JsonObject
        {
            ["userStatus"] = userStatus
        };
        using var requestBody = new StringContent(
            content.ToJsonString(),
            Encoding.UTF8,
            "application/json");

        // Create headers for identity
        var headers = GetHeaders(userEmail, userIdentity);
        var path = $"collaborations/{collaborationId}/updateCollaboratorStatus";
        return HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpPostAsync(
                path,
                logger,
                headers: headers,
                body: requestBody),
            client,
            ODataErrorCode);
    }

    private static Dictionary<string, string?> GetHeaders(
        string? userEmail,
        UserIdentity? userIdentity)
    {
        // x-ms-user-email for email users.
        return new Dictionary<string, string?>
        {
            ["x-ms-user-identifier"] = userEmail,
            ["x-ms-object-id"] = userIdentity?.ObjectId,
            ["x-ms-tenant-id"] = userIdentity?.TenantId
        };
    }
}
