// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace Controllers;

public static class HttpResponseMessageExtensions
{
    public static async Task ValidateStatusCodeAsync(
        this HttpResponseMessage response,
        ILogger logger)
    {
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();

            var requestDetail =
                $"{response.RequestMessage!.Method} request for resource: " +
                $"{response.RequestMessage.RequestUri} " +
                $"failed with statusCode {response.StatusCode}, " +
                $"reasonPhrase: {response.ReasonPhrase} and content: {content}.";

            logger.LogError(requestDetail);

            var message = string.IsNullOrWhiteSpace(content)
                ? requestDetail
                : content;

            throw new Azure.RequestFailedException((int)response.StatusCode, message);
        }
    }
}