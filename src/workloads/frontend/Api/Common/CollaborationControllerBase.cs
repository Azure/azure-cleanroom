// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using Controllers;
using FrontendSvc.CGSClient;
using Microsoft.AspNetCore.Mvc;

namespace FrontendSvc.Api.Common;

/// <summary>
/// Base controller with shared functionality for all API versions.
/// </summary>
public abstract class CollaborationControllerBase : ControllerBase
{
    /// <summary>
    /// Validates the Authorization header and extracts the bearer token.
    /// </summary>
    /// <param name="authorization">The Authorization header value.</param>
    /// <returns>The extracted bearer token.</returns>
    /// <exception cref="ApiException">Thrown when the header is missing or invalid.</exception>
    protected static string ValidateAndGetToken(string? authorization)
    {
        if (string.IsNullOrEmpty(authorization))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    "MissingAuthorizationHeader",
                    "The Authorization header is missing."));
        }

        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var authParts = authorization.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (authParts.Length != 2)
            {
                throw new ApiException(
                    HttpStatusCode.BadRequest,
                    new ODataError(
                        "InvalidAuthorizationHeader",
                        "Invalid Authorization header format."));
            }

            return authParts[1];
        }

        throw new ApiException(
            HttpStatusCode.BadRequest,
            new ODataError(
                "InvalidAuthorizationHeader",
                "Invalid Authorization header format."));
    }
}
