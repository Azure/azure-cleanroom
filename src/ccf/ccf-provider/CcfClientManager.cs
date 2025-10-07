// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Controllers;
using Microsoft.Extensions.Logging;

namespace CcfProvider;

public class CcfClientManager
{
    private readonly ILogger logger;
    private SigningConfiguration signingConfig = default!;
    private HttpClientManager httpClientManager;

    public CcfClientManager(ILogger logger)
    {
        this.logger = logger;
        this.httpClientManager = new(logger);
    }

    public void SetSigningConfig(SigningConfiguration wsConfig)
    {
        this.signingConfig = wsConfig;
    }

    public async Task<SigningConfiguration> GetSigningConfig()
    {
        await this.InitializeSigningConfig();
        return this.signingConfig;
    }

    public SigningConfiguration TryGetSigningConfig()
    {
        return this.signingConfig;
    }

    public async Task CheckSigningConfig()
    {
        await this.InitializeSigningConfig();
    }

    public Task<HttpClient> GetGovClient(string ccfEndpoint, string serviceCert)
    {
        var client = this.httpClientManager.GetOrAddClient(
            ccfEndpoint,
            HttpRetries.Policies.DefaultRetryPolicy(this.logger),
            serviceCert,
            "gov-client");
        return Task.FromResult(client);
    }

    public string GetGovApiVersion()
    {
        return "2024-07-01";
    }

    private async Task InitializeSigningConfig()
    {
        if (this.signingConfig == null)
        {
            throw new Exception("Invoke /configure first to setup signing cert and key details");
        }

        await Task.CompletedTask;
    }
}
