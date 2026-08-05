// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using Identity.Configuration;
using Microsoft.Azure.CleanRoomSidecar.Identity.CredentialStore;
using Microsoft.Extensions.Logging;

namespace Identity.CredentialManager.CredentialProviders;

/// <summary>
/// A token credential provider backed by a certificate.
/// </summary>
internal class CertificateCredentialProvider : ITokenCredentialProvider
{
    private readonly ILogger logger;
    private readonly string certificate;
    private readonly SecretStore secretStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="CertificateCredentialProvider"/> class.
    /// </summary>
    /// <param name="secretStore">The secret store.</param>
    /// <param name="logger">The logger to be used.</param>
    /// <param name="certificate">The certificate name.</param>
    public CertificateCredentialProvider(
        SecretStore secretStore,
        ILogger logger,
        string certificate)
    {
        this.secretStore = secretStore;
        this.logger = logger;
        this.certificate = certificate;
    }

    /// <inheritdoc/>
    public async Task<TokenCredential> GetTokenCredentialAsync(string tenantId, string clientId)
    {
        ISecretStore store = SecretStoreFactory.GetSecretStore(this.secretStore, this.logger);
        X509Certificate2 certificate = await store.GetCertificateAsync(this.certificate);

        return new ClientCertificateCredential(
            tenantId,
            clientId,
            certificate,
            new ClientCertificateCredentialOptions
            {
                SendCertificateChain = true,
            });
    }
}
