// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.CleanRoomSidecar.Identity.Utils;

/// <summary>
/// HTTP utilities.
/// </summary>
public static class HttpUtils
{
    /// <summary>
    /// Helper to validate the status codes returned by API calls.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>A Task reference.</returns>
    public static async Task ValidateStatusCodeAsync(
        this HttpResponseMessage response,
        ILogger logger)
    {
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();

            logger.LogError(
                $"{response.RequestMessage!.Method} request for resource: " +
                $"{response.RequestMessage.RequestUri} " +
                $"failed with statusCode {response.StatusCode}, " +
                $"reasonPhrase: {response.ReasonPhrase} and content: {content}.");

            throw new Azure.RequestFailedException((int)response.StatusCode, content);
        }
    }
}
