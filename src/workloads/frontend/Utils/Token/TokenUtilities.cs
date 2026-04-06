// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using Controllers;
using FrontendSvc.Models;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;

namespace FrontendSvc.Utils.Token;

public static class TokenUtilities
{
    public static (UserIdentity? userIdentity, string? userIdentifier)
       ExtractUserInfoFromToken(string userToken, ILogger logger)
    {
        try
        {
            var handler = new JsonWebTokenHandler();
            var jwtToken = handler.ReadJsonWebToken(userToken);

            var oidClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == "oid");
            var tidClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == "tid");
            var emailClaim = jwtToken.Claims.FirstOrDefault(
                    c => c.Type == "preferred_username") ??
                    jwtToken.Claims.FirstOrDefault(c => c.Type == "upn") ??
                    jwtToken.Claims.FirstOrDefault(c => c.Type == "email");

            // Client application ID: v1 tokens use "appid", v2 tokens use "azp".
            var appIdClaim = jwtToken.Claims.FirstOrDefault(
                    c => c.Type == "appid") ??
                    jwtToken.Claims.FirstOrDefault(c => c.Type == "azp");

            UserIdentity? userIdentity = null;
            string? userIdentifier = null;

            if (!string.IsNullOrEmpty(emailClaim?.Value))
            {
                userIdentifier = emailClaim?.Value;
                logger.LogInformation(
                    "User token detected. Extracted email as user identifier: {Email}",
                    userIdentifier);
            }
            else
            {
                userIdentifier = appIdClaim?.Value;
                logger.LogInformation(
                    "App-only token detected. Extracted appid/azp as user identifier: {AppId}",
                    userIdentifier);
            }

            if (oidClaim != null && tidClaim != null)
            {
                logger.LogInformation(
                    "Extracted UserIdentity from token: oid={ObjectId}, tid={TenantId}",
                    oidClaim.Value,
                    tidClaim.Value);
                userIdentity = new UserIdentity
                {
                    ObjectId = oidClaim.Value,
                    TenantId = tidClaim.Value,
                    AccountType = "microsoft"
                };
            }

            // Validate that we have a user identifier
            if (string.IsNullOrEmpty(userIdentifier))
            {
                throw new ApiException(
                    HttpStatusCode.BadRequest,
                    new ODataError(
                        "UserIdentifierRequired",
                        "User tokens must contain an email claim " +
                        "(preferred_username/upn/email). " +
                        "App tokens must contain appid or azp claim."));
            }

            return (userIdentity, userIdentifier);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to extract user info from token");
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    "InvalidToken",
                    $"Failed to parse user token: {ex.Message}"));
        }
    }
}
