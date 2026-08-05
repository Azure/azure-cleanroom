// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Controllers;

public class FrontendClientManager
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private readonly FrontendClientConfig clientConfig;
    private HttpClientManager httpClientManager;

    public FrontendClientManager(
        ILogger logger,
        IConfiguration configuration,
        FrontendClientConfig clientConfig)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.clientConfig = clientConfig;
        this.httpClientManager = new(logger);
    }

    public async Task<HttpClient> GetClient()
    {
        var endpoint = this.configuration[this.clientConfig.EndpointSettingName];
        if (string.IsNullOrEmpty(endpoint))
        {
            throw new Exception(
                $"{this.clientConfig.ServiceName} endpoint must be configured.");
        }

        var certLocator = new FrontendServiceCertLocator(
            this.logger,
            this.GetCertDiscoveryModel(endpoint));
        var client = await this.httpClientManager.GetOrAddClient(
            endpoint,
            certLocator,
            HttpRetries.Policies.DefaultRetryPolicy(this.logger),
            this.clientConfig.ServiceName);
        return client;
    }

    private FrontendCertDiscoveryModel GetCertDiscoveryModel(string endpoint)
    {
        var certDiscoveryEndpoint = endpoint + "/report";
        var certDiscoveryHostData =
            this.configuration[this.clientConfig.HostDataSettingName];
        if (string.IsNullOrEmpty(certDiscoveryHostData))
        {
            throw new Exception(
                $"{this.clientConfig.ServiceName} expected host data value " +
                $"must be configured.");
        }

        return new FrontendCertDiscoveryModel
        {
            CertificateDiscoveryEndpoint = certDiscoveryEndpoint,
            HostData = [certDiscoveryHostData]
        };
    }
}
