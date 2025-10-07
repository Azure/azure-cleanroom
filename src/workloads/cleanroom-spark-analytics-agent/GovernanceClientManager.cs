// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Controllers;

public class GovernanceClientManager
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private HttpClientManager httpClientManager;
    private string endpoint;

    public GovernanceClientManager(ILogger logger, IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.httpClientManager = new(logger);
        var governancePort = this.configuration[SettingName.GovernancePort] ?? "8300";
        this.endpoint = $"http://localhost:{governancePort}";
    }

    public HttpClient GetClient()
    {
        // No retry policy is specified as retries are handled within governance sidecar itself.
        var client = this.httpClientManager.GetOrAddClient(
            this.endpoint,
            HttpRetries.Policies.NoRetries,
            endpointCert: null,
            "ccr-governance");
        return client;
    }
}
