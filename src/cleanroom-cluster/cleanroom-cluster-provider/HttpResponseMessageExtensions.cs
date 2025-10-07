// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Rest.TransientFaultHandling;

namespace CleanRoomProvider;

internal static class HttpResponseMessageExtensions
{
    internal static async Task ValidateStatusCodeAsync(
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

            throw new HttpRequestWithStatusException(content)
            {
                StatusCode = response.StatusCode
            };
        }
    }
}