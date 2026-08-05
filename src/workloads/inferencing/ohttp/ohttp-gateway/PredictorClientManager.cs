// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

namespace Controllers;

public class PredictorClientManager
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private readonly GovernanceClientManager governanceClientManager;
    private HttpClientManager httpClientManager;
    private string? caCertPem;

    public PredictorClientManager(
        ILogger logger,
        IConfiguration configuration,
        GovernanceClientManager governanceClientManager)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.governanceClientManager = governanceClientManager;
        this.httpClientManager = new(logger);
    }

    public async Task<HttpClient> GetClient()
    {
        if (this.caCertPem == null)
        {
            this.caCertPem = await this.FetchCaCertPem();
        }

        var client = this.httpClientManager.GetOrAddClient(
            "https://placeholder",
            HttpRetries.Policies.DefaultRetryPolicy(this.logger),
            endpointCert: this.caCertPem,
            endpointName: "predictor");
        return client;
    }

    private async Task<string?> FetchCaCertPem()
    {
        // Allow direct cert path for testing without a governance sidecar.
        string? certPath = this.configuration["PREDICTOR_CA_CERT_PATH"];
        if (!string.IsNullOrEmpty(certPath) && File.Exists(certPath))
        {
            this.logger.LogInformation(
                "Loading predictor CA cert from {Path}.",
                certPath);
            return await File.ReadAllTextAsync(certPath);
        }

        var govClient = this.governanceClientManager.GetClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "ca/info");

        using HttpResponseMessage response =
            await govClient.SendAsync(request);
        await response.ValidateStatusCodeAsync(this.logger);

        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        string? caCertPem = json?["caCert"]?.ToString();
        if (string.IsNullOrEmpty(caCertPem))
        {
            throw new InvalidOperationException(
                "CA cert not available from governance sidecar.");
        }

        var cert = X509Certificate2.CreateFromPem(caCertPem);
        this.logger.LogInformation(
            "Fetched CA cert from governance sidecar. " +
            "Subject: {Subject}, Issuer: {Issuer}, " +
            "Thumbprint: {Thumbprint}, " +
            "NotBefore: {NotBefore}, NotAfter: {NotAfter}.",
            cert.Subject,
            cert.Issuer,
            cert.Thumbprint,
            cert.NotBefore,
            cert.NotAfter);
        cert.Dispose();

        return caCertPem;
    }
}
