// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;

namespace HttpRetries;

public static class Policies
{
    public static readonly IAsyncPolicy DefaultRetryPolicy =
        Policy.Handle<Exception>((e) => RetryUtilities.IsRetryableException(e))
        .WaitAndRetryAsync(
            3,
            retryAttempt =>
            {
                Random jitterer = new();
                return TimeSpan.FromSeconds(5) + TimeSpan.FromSeconds(jitterer.Next(0, 15));
            },
            (exception, timeSpan, retryCount, context) =>
            {
                ILogger logger = (ILogger)context["logger"];
                logger.LogWarning(
                    $"Hit retryable exception while performing operation: " +
                    $"{context.OperationKey}. Retrying after " +
                    $"{timeSpan}. RetryCount: {retryCount}. Exception: {exception}.");
            });

    public static IAsyncPolicy<HttpResponseMessage> GetDefaultRetryPolicy(ILogger logger)
    {
        return Policy<HttpResponseMessage>
            .Handle<Exception>(RetryUtilities.IsRetryableException)
            .OrTransientHttpStatusCode()
            .WaitAndRetryAsync(
                3,
                retryAttempt =>
                {
                    Random jitterer = new();
                    return TimeSpan.FromSeconds(5) + TimeSpan.FromSeconds(jitterer.Next(0, 15));
                },
                (result, timeSpan, retryCount, context) =>
                {
                    logger.LogWarning(
                        $"Hit retryable exception while performing operation. Retrying after " +
                        $"{timeSpan}. RetryCount: {retryCount}. Code: " +
                        $"{result.Result?.StatusCode}. Exception: {result.Exception}.");
                });
    }
}