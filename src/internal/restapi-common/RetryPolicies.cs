// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;

namespace HttpRetries;

public static class Policies
{
    private static IAsyncPolicy<HttpResponseMessage>? defaultRetryPolicy;

    public static IAsyncPolicy<HttpResponseMessage> NoRetries =>
        Policy.NoOpAsync<HttpResponseMessage>().WithPolicyKey("NoRetryPolicy");

    public static IAsyncPolicy<HttpResponseMessage> DefaultRetryPolicy(ILogger logger)
    {
        // Note: Might end up assigning more than once if invoked concurrently but can live
        // with that here.
        defaultRetryPolicy ??= Policy<HttpResponseMessage>
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
                    string action = $"{result.Result?.RequestMessage?.Method} " +
                    $"{result.Result?.RequestMessage?.RequestUri}";
                    logger.LogWarning(
                        $"Hit retryable exception while performing operation '{action}'. " +
                        $"Retrying after " +
                        $"{timeSpan}. RetryCount: {retryCount}. Code: " +
                        $"'{result?.Result?.StatusCode}'. Exception: '{result?.Exception}'.");
                }).
                WithPolicyKey("DefaultRetryPolicy");

        return defaultRetryPolicy;
    }
}