// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Http.Json;
using System.Text.Json;
using Controllers;
using Microsoft.Extensions.Logging;

namespace CleanRoomProvider;

public static class HttpClientManagerUtils
{
    public static async Task<ContractData> GetContractData(
        this HttpClientManager httpClientManager,
        ILogger logger,
        string contractUrl,
        string? contractUrlCaCert,
        Dictionary<string, string>? contractUrlHeaders)
    {
        var configUri = new Uri(contractUrl);
        string baseAddress = configUri.GetLeftPart(UriPartial.Authority);
        if (baseAddress.Contains("host.docker.internal")
            && (IsGitHubActionsEnv() || IsCodespacesEnv()))
        {
            baseAddress = baseAddress.Replace("host.docker.internal", "172.17.0.1");
        }

        var httpClient = httpClientManager!.GetOrAddClient(
            baseAddress,
            HttpRetries.Policies.DefaultRetryPolicy(logger),
            contractUrlCaCert,
            "contract-client");

        logger.LogInformation($"Fetching contract configuration from: {configUri}");
        using HttpRequestMessage request = new(HttpMethod.Get, configUri.AbsolutePath);

        if (contractUrlHeaders != null)
        {
            foreach (var header in contractUrlHeaders)
            {
                request.Headers.Add(header.Key, header.Value);
            }
        }

        using HttpResponseMessage response = await httpClient.SendAsync(request);
        await response.ValidateStatusCodeAsync(logger);
        var contract = (await response.Content.ReadFromJsonAsync<Contract>())!;

        logger.LogInformation(
            $"Contract configuration: {JsonSerializer.Serialize(contract)}");
        if (contract.Data == null)
        {
            throw new Exception($"Contract {contractUrl} contains no data value.");
        }

        var data = JsonSerializer.Deserialize<ContractData>(contract.Data);
        if (data == null)
        {
            throw new Exception($"Contract {contractUrl} contains no data json.");
        }

        data.Validate();
        return data;
    }

    public static async Task<T> GetDeploymentTemplate<T>(
        this HttpClientManager httpClientManager,
        ILogger logger,
        string configurationUrl,
        string? configurationUrlCaCert,
        Dictionary<string, string>? contractUrlHeaders)
        where T : DeploymentTemplateBase
    {
        var configUri = new Uri(configurationUrl!);
        string baseAddress = configUri.GetLeftPart(UriPartial.Authority);
        if (baseAddress.Contains("host.docker.internal")
            && (IsGitHubActionsEnv() || IsCodespacesEnv()))
        {
            baseAddress = baseAddress.Replace("host.docker.internal", "172.17.0.1");
        }

        var httpClient = httpClientManager!.GetOrAddClient(
            baseAddress,
            HttpRetries.Policies.DefaultRetryPolicy(logger),
            configurationUrlCaCert,
            "helm-config-client");

        logger.LogInformation($"Fetching helm chart configuration from: {configUri}");
        using HttpRequestMessage request = new(HttpMethod.Post, configUri.AbsolutePath);

        if (contractUrlHeaders != null)
        {
            foreach (var header in contractUrlHeaders)
            {
                request.Headers.Add(header.Key, header.Value);
            }
        }

        using HttpResponseMessage response = await httpClient.SendAsync(request);
        await response.ValidateStatusCodeAsync(logger);
        var deploymentSpec = (await response.Content.ReadFromJsonAsync<DeploymentSpec<T>>())!;

        logger.LogInformation(
            $"Chart configuration: {JsonSerializer.Serialize(deploymentSpec)}");

        if (deploymentSpec.Data == null)
        {
            throw new Exception($"Deployment template {configurationUrl} contains no data.");
        }

        deploymentSpec.Data.Validate();
        return deploymentSpec.Data;
    }

    private static bool IsGitHubActionsEnv()
    {
        return Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
    }

    private static bool IsCodespacesEnv()
    {
        return Environment.GetEnvironmentVariable("CODESPACES") == "true";
    }
}
