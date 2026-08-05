// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Controllers;
using OhttpCommon;

namespace OhttpClient;

/// <summary>
/// Fetches and caches the OHTTP server's KeyConfig. Refreshes on error or
/// when explicitly invalidated.
/// </summary>
public class KeyConfigCache
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private readonly IHttpClientFactory httpClientFactory;
    private KeyConfig? cachedKeyConfig;
    private byte[]? cachedKeyConfigBytes;

    public KeyConfigCache(
        ILogger logger,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Gets the cached KeyConfig, fetching from the server if needed.
    /// </summary>
    /// <returns>The cached or newly fetched KeyConfig.</returns>
    public async Task<KeyConfig> GetKeyConfigAsync()
    {
        if (this.cachedKeyConfig != null)
        {
            return this.cachedKeyConfig;
        }

        await this.RefreshAsync();
        return this.cachedKeyConfig!;
    }

    /// <summary>
    /// Gets the raw binary KeyConfig bytes.
    /// </summary>
    /// <returns>The raw binary KeyConfig bytes.</returns>
    public async Task<byte[]> GetKeyConfigBytesAsync()
    {
        if (this.cachedKeyConfigBytes != null)
        {
            return this.cachedKeyConfigBytes;
        }

        await this.RefreshAsync();
        return this.cachedKeyConfigBytes!;
    }

    /// <summary>
    /// Fetches the KeyConfig from the OHTTP server.
    /// </summary>
    /// <returns>A task representing the asynchronous refresh operation.</returns>
    public async Task RefreshAsync()
    {
        string serverEndpoint = this.configuration["OHTTP_GATEWAY_ENDPOINT"]
            ?? throw new InvalidOperationException(
                "OHTTP_GATEWAY_ENDPOINT environment variable is not set.");

        string url = $"{serverEndpoint}{OhttpConstants.WellKnownKeysPath}";
        this.logger.LogInformation("Fetching KeyConfig from {Url}...", url);

        HttpClient client = this.httpClientFactory.CreateClient("OhttpGateway");
        using HttpResponseMessage response = await client.GetAsync(url);
        await response.ValidateStatusCodeAsync(this.logger);

        this.cachedKeyConfigBytes = await response.Content.ReadAsByteArrayAsync();
        this.cachedKeyConfig = KeyConfig.Deserialize(this.cachedKeyConfigBytes);

        this.logger.LogInformation(
            "KeyConfig cached. KeyId={KeyId}, KemId=0x{KemId:X4}.",
            this.cachedKeyConfig.KeyId,
            this.cachedKeyConfig.KemId);
    }

    /// <summary>
    /// Invalidates the cache so the next call will re-fetch.
    /// </summary>
    public void Invalidate()
    {
        this.cachedKeyConfig = null;
        this.cachedKeyConfigBytes = null;
    }
}
