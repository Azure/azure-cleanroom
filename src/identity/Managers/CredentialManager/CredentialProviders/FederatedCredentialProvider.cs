// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Identity.CredentialManager;
using Microsoft.Azure.CleanRoomSidecar.Identity.Utils;
using Microsoft.Extensions.Logging;
using Polly;

namespace Microsoft.Azure.CleanRoomSidecar.Identity.CredentialProviders;

/// <summary>
/// A credential provider that manages federated credentials.
/// </summary>
internal class FederatedCredentialProvider : ITokenCredentialProvider
{
    private static IAsyncPolicy retryPolicy =
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

    private static HttpClient httpClient = new();
    private readonly ILogger logger;
    private readonly string audience;
    private readonly string? issuer;
    private readonly string subject;
    private readonly string idTokenEndpoint;
    private readonly Dictionary<string, object> retryContextData;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederatedCredentialProvider"/> class.
    /// </summary>
    /// <param name="idTokenEndpoint">
    /// The endpoint at which the source token can be obtained.
    /// </param>
    /// <param name="subject">The subject claim.</param>
    /// <param name="audience">The audience claim.</param>
    /// <param name="issuer">Any issuer claim.</param>
    /// <param name="logger">The logger to be used.</param>
    public FederatedCredentialProvider(
        string idTokenEndpoint,
        string subject,
        string audience,
        string? issuer,
        ILogger logger)
    {
        this.logger = logger;
        this.subject = subject;
        this.audience = audience;
        this.issuer = issuer;
        this.idTokenEndpoint = idTokenEndpoint;
        this.retryContextData = new Dictionary<string, object>
        {
            {
                "logger",
                this.logger
            }
        };
    }

    /// <inheritdoc/>
    public Task<TokenCredential> GetTokenCredentialAsync(string tenantId, string clientId)
    {
        TokenCredential credential = new ClientAssertionCredential(
            tenantId,
            clientId,
            async (cToken) =>
            {
                string url = $"{this.idTokenEndpoint.TrimEnd('/')}/oauth/token?" +
                    $"sub={this.subject}&tenantId={tenantId}&aud={this.audience}";
                if (!string.IsNullOrEmpty(this.issuer))
                {
                    url += $"&iss={this.issuer}";
                }

                JsonObject? result = await RetryPolicies.DefaultPolicy.ExecuteAsync(
                async (ctx) =>
                {
                    this.logger.LogInformation($"Fetching client assertion from '{url}'");
                    HttpResponseMessage response = await httpClient.PostAsync(url, null);
                    await response.ValidateStatusCodeAsync(this.logger);
                    return await response.Content.ReadFromJsonAsync<JsonObject>();
                },
                new Context("oauth/token", this.retryContextData));

                return result?["value"]?.ToString();
            });
        return Task.FromResult(credential);
    }
}
