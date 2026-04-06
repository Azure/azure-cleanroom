// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Controllers;

public class SparkFrontendClientManager
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private HttpClientManager httpClientManager;

    public SparkFrontendClientManager(ILogger logger, IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.httpClientManager = new(logger);
    }

    public async Task<HttpClient> GetClient()
    {
        var endpoint = this.configuration[SettingName.SparkFrontendEndpoint];
        if (string.IsNullOrEmpty(endpoint))
        {
            throw new Exception("Spark Frontend endpoint must be configured.");
        }

        var certLocator = new SparkFrontendServiceCertLocator(
            this.logger,
            this.SparkFrontendCertDiscoveryModel(endpoint));
        var client = await this.httpClientManager.GetOrAddClient(
            endpoint,
            certLocator,
            HttpRetries.Policies.DefaultRetryPolicy(this.logger),
            "spark-frontend");
        return client;
    }

    private SparkFrontendCertDiscoveryModel SparkFrontendCertDiscoveryModel(string endpoint)
    {
        var certDiscoveryEndpoint = endpoint + "/report";
        var certDiscoveryHostData = this.configuration[SettingName.SparkFrontendSnpHostData];
        if (string.IsNullOrEmpty(certDiscoveryHostData))
        {
            throw new Exception("Spark Frontend expected host data value must be configured.");
        }

        return new SparkFrontendCertDiscoveryModel
        {
            CertificateDiscoveryEndpoint = certDiscoveryEndpoint,
            HostData = [certDiscoveryHostData]
        };
    }
}
