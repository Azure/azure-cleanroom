// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using Azure.Core;
using Controllers;
using Identity.Configuration;
using Microsoft.Extensions.Logging;

namespace Identity.CredentialManager;

/// <summary>
/// Class that represents the credential manager for the identity sidecar.
/// </summary>
public class CredentialManager
{
    private readonly ILogger logger;
    private readonly IdentityConfiguration config;
    private readonly ConcurrentDictionary<string, ICredential<AccessToken>>
        credentialDictionary = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CredentialManager"/> class.
    /// </summary>
    /// <param name="config">The identity configuration.</param>
    /// <param name="logger">The logger.</param>
    public CredentialManager(IdentityConfiguration config, ILogger logger)
    {
        this.config = config;
        this.logger = logger;
        this.InitializeCredentials();
    }

    /// <summary>
    /// Gets the specified credential from the configuration.
    /// </summary>
    /// <param name="clientId">The client ID.</param>
    /// <returns>An instance of the <see cref="ICredential{AccessToken}"/>.</returns>
    public ICredential<AccessToken> GetCredential(string clientId)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            this.logger.LogTrace($"Empty client ID passed in. Using default credential.");

            return new DefaultCredential(this.logger);
        }

        this.logger.LogTrace($"Fetching credential for client ID: '{clientId}'.");

        if (this.credentialDictionary.TryGetValue(clientId, out var credential))
        {
            return credential;
        }

        throw new ApiException(new ODataError(
            code: "InvalidClientId",
            message: $"Invalid client ID specified: {clientId}"));
    }

    public bool RegisterCredential(ApplicationIdentity identity)
    {
        var credential = new ApplicationCredential(identity, this.logger);
        return this.credentialDictionary.TryAdd(identity.ClientId, credential);
    }

    private void InitializeCredentials()
    {
        foreach (ManagedIdentity identity in this.config.Identities.ManagedIdentities)
        {
            this.credentialDictionary.TryAdd(
                identity.ClientId,
                new ManagedIdentityCredential(identity, this.logger));
        }

        foreach (ApplicationIdentity identity in this.config.Identities.ApplicationIdentities)
        {
            this.credentialDictionary.TryAdd(
                identity.ClientId,
                new ApplicationCredential(identity, this.logger));
        }
    }
}