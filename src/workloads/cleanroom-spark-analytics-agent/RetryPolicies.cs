// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Identity;
using HttpRetries;
using Microsoft.Identity.Client;
using Polly;

namespace Controllers;

internal static class RetryPolicies
{
    public static readonly IAsyncPolicy DefaultPolicy =
        Policy.Handle<Exception>(RetryUtilities.IsRetryableException)
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

    // When using federated credentials it takes a few seconds before the federated credentials
    // create on a managed identity is usable. Below failure occurs when this happens before a
    // retry starts working:
    // Azure.Identity.AuthenticationFailedException: ClientAssertionCredential authentication
    // failed: A configuration issue is preventing authentication - check the error message
    // from the server for details. You can modify the configuration in the application
    // registration portal. See https://aka.ms/msal-net-invalid-client for details.
    // Original exception: AADSTS700213: No matching federated identity record found for
    // presented assertion subject '7e11dc90'. Check your federated identity credential
    // Subject, Audience and Issuer against the presented assertion.
    // https://learn.microsoft.com/entra/workload-id/workload-identity-federation
    // Trace ID: e91e22aa-fe7f-42c6-a4df-82448f236000 Correlation ID:
    // de447432-b0cb-4bca-bf62-ca1625db201b Timestamp: 2025-06-18 08:02:45Z
    public static readonly IAsyncPolicy FederatedCredsPolicy =
        Policy.Handle<AuthenticationFailedException>(RetryPolicies.IsRetryableAuthException)
        .WaitAndRetryAsync(
            7,
            retryAttempt =>
            {
                Random jitterer = new();
                return TimeSpan.FromSeconds(5) + TimeSpan.FromSeconds(jitterer.Next(0, 5));
            },
            (exception, timeSpan, retryCount, context) =>
            {
                ILogger logger = (ILogger)context["logger"];
                logger.LogWarning(
                    $"Hit AuthenticationFailedException while performing operation: " +
                    $"{context.OperationKey}. Retrying after " +
                    $"{timeSpan}. RetryCount: {retryCount}. Exception: {exception}.");
            });

    private static bool IsRetryableAuthException(AuthenticationFailedException afe)
    {
        return afe.InnerException is MsalException mse &&
            (mse.Message.Contains("AADSTS700213") || mse.Message.Contains("AADSTS70025"));
    }
}