// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using HttpRetries;
using Microsoft.Extensions.Logging;
using Polly;

namespace Identity.CredentialManager;

internal static class RetryPolicies
{
    public static readonly IAsyncPolicy DefaultPolicy =
        Policy.Handle<Exception>((e) => RetryUtilities.IsRetryableException(e))
        .WaitAndRetryAsync(
            5,
            retryAttempt =>
            {
                Random jitterer = new();
                return TimeSpan.FromSeconds(10) + TimeSpan.FromSeconds(jitterer.Next(0, 20));
            },
            (exception, timeSpan, retryCount, context) =>
            {
                ILogger logger = (ILogger)context["logger"];
                logger.LogWarning(
                    $"Hit retryable exception while performing operation: " +
                    $"{context.OperationKey}. Retrying after " +
                    $"{timeSpan}. RetryCount: {retryCount}. Exception: {exception}.");
            });
}