// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcfCommon;
using Controllers;
using Microsoft.Extensions.Logging;

namespace CcfConsortiumMgrProvider;

public class CcfConsortiumManagerProvider
{
    private ILogger logger;
    private ICcfConsortiumManagerInstanceProvider instanceProvider;
    private HttpClientManager httpClientManager;

    public CcfConsortiumManagerProvider(
        ILogger logger,
        ICcfConsortiumManagerInstanceProvider instanceProvider)
    {
        this.logger = logger;
        this.instanceProvider = instanceProvider;
        this.httpClientManager = new(logger);
    }

    public async Task<CcfConsortiumManager> CreateConsortiumManager(
        string consortiumManagerName,
        string akvEndpoint,
        string maaEndpoint,
        string? managedIdentityId,
        SecurityPolicyConfiguration policyOption,
        JsonObject? providerConfig)
    {
        var instanceName = "cm-" + consortiumManagerName + "-0";
        var cmEndpoint =
            await this.instanceProvider.CreateConsortiumManager(
                instanceName,
                consortiumManagerName,
                akvEndpoint,
                maaEndpoint,
                managedIdentityId,
                policyOption,
                providerConfig);

        this.logger.LogInformation(
            $"Consortium manager endpoint is up at: {cmEndpoint.Endpoint}.");

        string serviceCert =
            await this.GetSelfSignedCert(consortiumManagerName, cmEndpoint.Endpoint);
        await this.GenerateKeys(cmEndpoint.Endpoint, serviceCert);

        return new CcfConsortiumManager
        {
            Name = consortiumManagerName,
            InfraType = this.instanceProvider.InfraType.ToString(),
            Endpoint = cmEndpoint.Endpoint,
            ServiceCert = serviceCert
        };
    }

    public async Task<CcfConsortiumManager?> GetConsortiumManager(
        string consortiumManagerName,
        JsonObject? providerConfig)
    {
        var cmEndpoint =
            await this.instanceProvider.TryGetConsortiumManagerEndpoint(
                consortiumManagerName,
                providerConfig);
        if (cmEndpoint != null)
        {
            return new CcfConsortiumManager
            {
                Name = consortiumManagerName,
                InfraType = this.instanceProvider.InfraType.ToString(),
                Endpoint = cmEndpoint.Endpoint,
                ServiceCert = await this.GetSelfSignedCert(
                    consortiumManagerName,
                    cmEndpoint.Endpoint,
                    onRetry: () => this.CheckServiceHealthy(consortiumManagerName, providerConfig))
            };
        }

        return null;
    }

    private async Task CheckServiceHealthy(
        string consortiumManagerName,
        JsonObject? providerConfig)
    {
        var serviceHealth = await this.instanceProvider.GetConsortiumManagerHealth(
            consortiumManagerName,
            providerConfig);
        if (serviceHealth.Status == ServiceStatus.Unhealthy)
        {
            throw new Exception(
                $"Service instance {consortiumManagerName} is reporting unhealthy: " +
                $"{JsonSerializer.Serialize(serviceHealth, CcfUtils.Options)}");
        }
    }

    private async Task<string> GetSelfSignedCert(
        string consortiumManagerName,
        string endpoint,
        Func<Task>? onRetry = null)
    {
        // TODO (devbabu): Add a CheckServiceHealth callback similar to the recovery service flow.
        // No retry policy is specified as retries are handled in the loop below.
        using var client = HttpClientManager.NewInsecureClient(
            endpoint,
            this.logger,
            HttpRetries.Policies.NoRetries);

        // Use a shorter timeout than the default (100s) so that we retry faster to connect to the
        // endpoint that is warming up.
        client.Timeout = TimeSpan.FromSeconds(30);

        // At times it takes a while for the endpoint to start responding so giving a large timeout.
        TimeSpan readyTimeout = TimeSpan.FromSeconds(300);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var response = await client.GetAsync("/report");
                if (response.IsSuccessStatusCode)
                {
                    var serviceCert = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
                    var value = serviceCert["serviceCert"]!.ToString();
                    return value;
                }

                this.logger.LogInformation(
                    $"{consortiumManagerName}: Waiting for {endpoint}/report to report " +
                    $"success. Current statusCode: {response.StatusCode}.");
            }
            catch (TaskCanceledException te)
            {
                this.logger.LogError(
                    $"{consortiumManagerName}: Hit HttpClient timeout waiting for " +
                    $"{endpoint}/report to report success. Current error: {te.Message}.");
            }
            catch (HttpRequestException re)
            {
                this.logger.LogInformation(
                    $"{consortiumManagerName}: Waiting for {endpoint}/report to report " +
                    $"success. Current statusCode: {re.StatusCode}, error: {re.Message}.");
            }

            if (stopwatch.Elapsed > readyTimeout)
            {
                throw new TimeoutException(
                    $"{consortiumManagerName}: Hit timeout waiting for {endpoint}/report");
            }

            if (onRetry != null)
            {
                await onRetry.Invoke();
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async Task GenerateKeys(string endpoint, string serviceCert)
    {
        using var client = this.httpClientManager.GetOrAddClient(
            endpoint,
            HttpRetries.Policies.DefaultRetryPolicy(this.logger),
            endpointCert: serviceCert);

        using var response = await client.PostAsync("/generateKeys", content: null);
        await response.ValidateStatusCodeAsync(this.logger);
    }
}
