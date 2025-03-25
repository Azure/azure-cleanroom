// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcfCommon;
using Controllers;
using Microsoft.Extensions.Logging;

namespace CcfRecoveryProvider;

public class CcfRecoveryServiceProvider
{
    private ILogger logger;
    private ICcfRecoveryServiceInstanceProvider svcInstanceProvider;

    public CcfRecoveryServiceProvider(
        ILogger logger,
        ICcfRecoveryServiceInstanceProvider svcProvider)
    {
        this.logger = logger;
        this.svcInstanceProvider = svcProvider;
    }

    public async Task<CcfRecoveryService> CreateService(
        string serviceName,
        string akvEndpoint,
        string maaEndpoint,
        string? managedIdentityId,
        NetworkJoinPolicy networkJoinPolicy,
        SecurityPolicyConfiguration policyOption,
        JsonObject? providerConfig)
    {
        var instanceName = "rs-" + serviceName + "-0";
        var svcEndpoint = await this.svcInstanceProvider.CreateRecoveryService(
            instanceName,
            serviceName,
            akvEndpoint,
            maaEndpoint,
            managedIdentityId,
            networkJoinPolicy,
            policyOption,
            providerConfig);

        this.logger.LogInformation($"Recovery service endpoint is up at: {svcEndpoint.Endpoint}.");

        var serviceCert = await this.GetSelfSignedCert(
            serviceName,
            svcEndpoint.Endpoint,
            onRetry: () => this.CheckServiceHealthy(serviceName, providerConfig));

        return new CcfRecoveryService
        {
            Name = serviceName,
            InfraType = this.svcInstanceProvider.InfraType.ToString(),
            Endpoint = svcEndpoint.Endpoint,
            ServiceCert = serviceCert
        };
    }

    public async Task DeleteService(
        string serviceName,
        JsonObject? providerConfig)
    {
        await this.svcInstanceProvider.DeleteRecoveryService(serviceName, providerConfig);
    }

    public async Task<CcfRecoveryService?> GetService(string serviceName, JsonObject? providerConfig)
    {
        var svcEndpoint =
            await this.svcInstanceProvider.TryGetRecoveryServiceEndpoint(
                serviceName,
                providerConfig);
        if (svcEndpoint != null)
        {
            var serviceCert = await this.GetSelfSignedCert(serviceName, svcEndpoint.Endpoint);

            return new CcfRecoveryService
            {
                Name = serviceName,
                InfraType = this.svcInstanceProvider.InfraType.ToString(),
                Endpoint = svcEndpoint.Endpoint,
                ServiceCert = serviceCert
            };
        }

        return null;
    }

    public async Task<JsonObject> GenerateSecurityPolicy(
        NetworkJoinPolicy joinPolicy,
        SecurityPolicyCreationOption policyOption)
    {
        return await this.svcInstanceProvider.GenerateSecurityPolicy(joinPolicy, policyOption);
    }

    private async Task CheckServiceHealthy(
        string serviceName,
        JsonObject? providerConfig)
    {
        var serviceHealth = await this.svcInstanceProvider.GetRecoveryServiceHealth(
            serviceName,
            providerConfig);
        if (serviceHealth.Status == nameof(ServiceStatus.Unhealthy))
        {
            throw new Exception(
                $"Service instance {serviceName} is reporting unhealthy: " +
                $"{JsonSerializer.Serialize(serviceHealth, CcfUtils.Options)}");
        }
    }

    private async Task<string> GetSelfSignedCert(
        string serviceName,
        string endpoint,
        Func<Task>? onRetry = null)
    {
        using var client = HttpClientManager.NewInsecureClient(endpoint, this.logger);

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
                    $"{serviceName}: Waiting for {endpoint}/report to report " +
                    $"success. Current statusCode: {response.StatusCode}.");
            }
            catch (TaskCanceledException te)
            {
                this.logger.LogError(
                    $"{serviceName}: Hit HttpClient timeout waiting for " +
                    $"{endpoint}/report to report success. Current error: {te.Message}.");
            }
            catch (HttpRequestException re)
            {
                this.logger.LogInformation(
                    $"{serviceName}: Waiting for {endpoint}/report to report " +
                    $"success. Current statusCode: {re.StatusCode}, error: {re.Message}.");
            }

            if (stopwatch.Elapsed > readyTimeout)
            {
                throw new TimeoutException(
                    $"{serviceName}: Hit timeout waiting for {endpoint}/report");
            }

            if (onRetry != null)
            {
                await onRetry.Invoke();
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }
}
