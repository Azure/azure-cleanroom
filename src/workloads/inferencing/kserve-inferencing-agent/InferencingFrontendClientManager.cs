// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Controllers;

public class InferencingFrontendClientManager
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private HttpClientManager httpClientManager;

    public InferencingFrontendClientManager(ILogger logger, IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.httpClientManager = new(logger);
    }

    public async Task<HttpClient> GetClient()
    {
        var endpoint = this.configuration[SettingName.InferencingFrontendEndpoint];
        if (string.IsNullOrEmpty(endpoint))
        {
            throw new Exception("Inferencing Frontend endpoint must be configured.");
        }

        var certLocator = new InferencingFrontendServiceCertLocator(
            this.logger,
            this.InferencingFrontendCertDiscoveryModel(endpoint));
        var client = await this.httpClientManager.GetOrAddClient(
            endpoint,
            certLocator,
            HttpRetries.Policies.DefaultRetryPolicy(this.logger),
            "inferencing-frontend");
        return client;
    }

    private InferencingFrontendCertDiscoveryModel InferencingFrontendCertDiscoveryModel(
        string endpoint)
    {
        var certDiscoveryEndpoint = endpoint + "/report";
        var certDiscoveryHostData = this.configuration[SettingName.InferencingFrontendSnpHostData];
        if (string.IsNullOrEmpty(certDiscoveryHostData))
        {
            throw new Exception("Inferencing Frontend expected host data value must be configured.");
        }

        return new InferencingFrontendCertDiscoveryModel
        {
            CertificateDiscoveryEndpoint = certDiscoveryEndpoint,
            HostData = [certDiscoveryHostData]
        };
    }
}
