// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Controllers;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.CleanRoomSidecar.Identity.CredentialStore;

/// <summary>
/// A Cleanroom Governance Service (CGS) based secret store.
/// </summary>
internal class CgsSecretStore : ISecretStore
{
    private static HttpClient httpClient = new();
    private readonly string governanceSidecarEndpoint;
    private readonly ILogger logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CgsSecretStore"/> class.
    /// </summary>
    /// <param name="governanceSidecarEndpoint">The governance sidecar endpoint.</param>
    /// <param name="logger">The logger.</param>
    public CgsSecretStore(string governanceSidecarEndpoint, ILogger logger)
    {
        this.governanceSidecarEndpoint = governanceSidecarEndpoint;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public Task<X509Certificate2> GetCertificateAsync(string name)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public async Task<string> GetSecretAsync(string name)
    {
        string uri = $"{this.governanceSidecarEndpoint}/secrets/{name}";

        HttpResponseMessage response =
            await httpClient.PostAsync(uri, null);
        await response.ValidateStatusCodeAsync(this.logger);
        string responseStr = await response.Content.ReadAsStringAsync();
        var document = JsonSerializer.Deserialize<JsonElement>(responseStr);
        string secret = document.GetProperty("value").GetString()!;
        return secret;
    }
}
