// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Core;
using Identity.Configuration;
using Identity.CredentialManager.CredentialProviders;
using Microsoft.Azure.CleanRoomSidecar.Identity.CredentialProviders;
using Microsoft.Extensions.Logging;
using Polly;

namespace Identity.CredentialManager;

/// <summary>
/// Class that defines an Enterprise Application in Azure AD.
/// </summary>
public class ApplicationCredential : ICredential<AccessToken>
{
    private readonly ApplicationIdentity applicationIdentity;
    private readonly ILogger logger;
    private readonly Dictionary<string, object> retryContextData;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApplicationCredential"/> class.
    /// </summary>
    /// <param name="applicationIdentity">The application identity.</param>
    /// <param name="logger">The logger.</param>
    public ApplicationCredential(
        ApplicationIdentity applicationIdentity,
        ILogger logger)
    {
        this.applicationIdentity = applicationIdentity;
        this.logger = logger;
        this.retryContextData = new Dictionary<string, object>
        {
            {
                "logger",
                this.logger
            }
        };
    }

    /// <inheritdoc/>
    public async Task<AccessToken> GetTokenAsync(string scope, string tenantId)
    {
        ITokenCredentialProvider tokenCredentialProvider =
            this.GetTokenCredentialProvider();

        var tokenCredential = await tokenCredentialProvider.GetTokenCredentialAsync(
            tenantId,
            this.applicationIdentity.ClientId);

        // Hitting below failure at times in local box runs. So adding retries.
        // (Name or service not known (login.microsoftonline.com:443))
        // (Resource temporarily unavailable (login.microsoftonline.com:443))
        return await RetryPolicies.DefaultPolicy.ExecuteAsync(
        async (ctx) =>
        {
            return await tokenCredential.GetTokenAsync(
                new TokenRequestContext(scope.FormatScope()),
                CancellationToken.None);
        },
        new Context("GetTokenAsync", this.retryContextData));
    }

    /// <summary>
    /// Gets an instance of <see cref="ITokenCredentialProvider"/>.
    /// </summary>
    /// <returns>An <see cref="ITokenCredentialProvider"/>.</returns>
    private ITokenCredentialProvider GetTokenCredentialProvider()
    {
        return this.applicationIdentity.Credential.CredentialType switch
        {
            CredentialType.Secret => new SecretCredentialProvider(
                this.applicationIdentity.Credential.SecretConfiguration.SecretStore,
                this.logger,
                this.applicationIdentity.Credential.SecretConfiguration.SecretName),

            CredentialType.Certificate => new CertificateCredentialProvider(
                this.applicationIdentity.Credential.SecretConfiguration.SecretStore,
                this.logger,
                this.applicationIdentity.Credential.SecretConfiguration.SecretName),

            CredentialType.FederatedCredential => new FederatedCredentialProvider(
                this.applicationIdentity.Credential.FederationConfiguration
                    .IdTokenEndpoint,
                this.applicationIdentity.Credential.FederationConfiguration.Subject,
                this.applicationIdentity.Credential.FederationConfiguration.Audience,
                this.applicationIdentity.Credential.FederationConfiguration.Issuer,
                this.logger),

            _ => throw new NotSupportedException($"Credential type " +
                $"{this.applicationIdentity.Credential.CredentialType} is not supported."),
        };
    }
}