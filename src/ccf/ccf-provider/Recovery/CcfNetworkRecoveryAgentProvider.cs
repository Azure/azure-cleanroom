// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Controllers;
using CoseUtils;
using Microsoft.Extensions.Logging;

namespace CcfProvider;

public class CcfNetworkRecoveryAgentProvider
{
    private ILogger logger;
    private ICcfNodeProvider nodeProvider;
    private RecoveryAgentClientManager agentClientManager;

    public CcfNetworkRecoveryAgentProvider(
        ILogger logger,
        ICcfNodeProvider nodeProvider,
        RecoveryAgentClientManager agentClientManager)
    {
        this.logger = logger;
        this.nodeProvider = nodeProvider;
        this.agentClientManager = agentClientManager;
    }

    public async Task<JsonObject> GenerateRecoveryMember(
        string networkName,
        string memberName,
        AgentConfig? agentConfig,
        JsonObject? providerConfig)
    {
        var agentClient = await this.GetAgentClient(networkName, providerConfig);

        this.logger.LogInformation(
            $"Requesting recovery agent to generate recovery member {memberName}.");

        var input = new AgentRequest
        {
            MemberName = memberName,
            AgentConfig = agentConfig
        };

        var coseSignKey = this.agentClientManager.GetCoseSignKey();
        using var cert = X509Certificate2.CreateFromPem(coseSignKey.Certificate);
        var memberId = cert.GetCertHashString(HashAlgorithmName.SHA256).ToLower();

        var payload = await Cose.CreateRecoveryCoseSign1Message(
            coseSignKey,
            RecoveryMessageType.GenerateMember,
            JsonSerializer.Serialize(input));
        using HttpRequestMessage request = Cose.CreateHttpRequestMessage(
            $"members/{memberId}/recoveryMembers/generate",
            payload);
        using HttpResponseMessage response = await agentClient.SendAsync(request);
        await response.ValidateStatusCodeAsync(this.logger);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        return body!;
    }

    public async Task<string> ActivateRecoveryMember(
        string networkName,
        string memberName,
        AgentConfig? agentConfig,
        JsonObject? providerConfig)
    {
        var agentClient = await this.GetAgentClient(networkName, providerConfig);

        this.logger.LogInformation(
            $"Requesting recovery agent to activate recovery member {memberName}.");

        var input = new AgentRequest
        {
            MemberName = memberName,
            AgentConfig = agentConfig
        };

        var coseSignKey = this.agentClientManager.GetCoseSignKey();
        using var cert = X509Certificate2.CreateFromPem(coseSignKey.Certificate);
        var memberId = cert.GetCertHashString(HashAlgorithmName.SHA256).ToLower();

        var payload = await Cose.CreateRecoveryCoseSign1Message(
            coseSignKey,
            RecoveryMessageType.ActivateMember,
            JsonSerializer.Serialize(input));
        using HttpRequestMessage request = Cose.CreateHttpRequestMessage(
            $"members/{memberId}/recoveryMembers/activate",
            payload);
        using HttpResponseMessage response = await agentClient.SendAsync(request);
        await response.ValidateStatusCodeAsync(this.logger);
        var body = await response.Content.ReadAsStringAsync();
        return body;
    }

    public async Task<JsonObject> SubmitRecoveryShare(
        string networkName,
        string memberName,
        AgentConfig? agentConfig,
        JsonObject? providerConfig)
    {
        var agentClient = await this.GetAgentClient(networkName, providerConfig);

        this.logger.LogInformation(
            $"Requesting recovery agent to submit recovery share for member {memberName}.");

        var input = new AgentRequest
        {
            MemberName = memberName,
            AgentConfig = agentConfig
        };

        var coseSignKey = this.agentClientManager.GetCoseSignKey();
        using var cert = X509Certificate2.CreateFromPem(coseSignKey.Certificate);
        var memberId = cert.GetCertHashString(HashAlgorithmName.SHA256).ToLower();

        var payload = await Cose.CreateRecoveryCoseSign1Message(
            coseSignKey,
            RecoveryMessageType.RecoveryShare,
            JsonSerializer.Serialize(input));
        using HttpRequestMessage request = Cose.CreateHttpRequestMessage(
            $"members/{memberId}/recoveryMembers/submitRecoveryShare",
            payload);
        using HttpResponseMessage response = await agentClient.SendAsync(request);
        await response.ValidateStatusCodeAsync(this.logger);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        return body!;
    }

    public async Task SetNetworkJoinPolicy(
        string networkName,
        AgentConfig? agentConfig,
        JsonObject? providerConfig)
    {
        var agentClient = await this.GetAgentClient(networkName, providerConfig);

        this.logger.LogInformation(
            $"Requesting recovery agent to set the network join policy in the recovery service.");

        var input = new AgentRequest
        {
            AgentConfig = agentConfig
        };

        var coseSignKey = this.agentClientManager.GetCoseSignKey();
        using var cert = X509Certificate2.CreateFromPem(coseSignKey.Certificate);
        var memberId = cert.GetCertHashString(HashAlgorithmName.SHA256).ToLower();

        var payload = await Cose.CreateRecoveryCoseSign1Message(
            coseSignKey,
            RecoveryMessageType.SetNetworkJoinPolicy,
            JsonSerializer.Serialize(input));
        using HttpRequestMessage request = Cose.CreateHttpRequestMessage(
            $"members/{memberId}/network/joinpolicy/set",
            payload);
        using HttpResponseMessage response = await agentClient.SendAsync(request);
        await response.ValidateStatusCodeAsync(this.logger);
    }

    public async Task<CcfNetworkRecoveryAgents> GetNetworkRecoveryAgent(
        string networkName,
        JsonObject? providerConfig)
    {
        List<RecoveryAgentEndpoint> agentEndpoints =
            await this.nodeProvider.GetRecoveryAgents(networkName, providerConfig);
        List<CcfRecoveryAgent> agents = new();
        foreach (var agent in agentEndpoints)
        {
            var serviceCert = await this.GetSelfSignedCert(networkName, agent.Endpoint);
            agents.Add(new()
            {
                Name = agent.Name,
                Endpoint = agent.Endpoint,
                ServiceCert = serviceCert
            });
        }

        return new CcfNetworkRecoveryAgents
        {
            Agents = agents
        };
    }

    public async Task<CcfNetworkRecoveryAgentsReport> GetReport(
        string networkName,
        JsonObject? providerConfig)
    {
        List<RecoveryAgentEndpoint> agentEndpoints =
            await this.nodeProvider.GetRecoveryAgents(networkName, providerConfig);
        List<CcfRecoveryAgentReport> reports = new();
        foreach (var agent in agentEndpoints)
        {
            var report = await this.GetReport(networkName, agent.Endpoint);
            reports.Add(new()
            {
                Name = agent.Name,
                Endpoint = agent.Endpoint,
                Report = report
            });
        }

        return new CcfNetworkRecoveryAgentsReport
        {
            Reports = reports
        };
    }

    private async Task<string> GetSelfSignedCert(string networkName, string endpoint)
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
                    $"{networkName}: Waiting for {endpoint}/report to report " +
                    $"success. Current statusCode: {response.StatusCode}.");
            }
            catch (TaskCanceledException te)
            {
                this.logger.LogError(
                    $"{networkName}: Hit HttpClient timeout waiting for " +
                    $"{endpoint}/report to report success. Current error: {te.Message}.");
            }
            catch (HttpRequestException re)
            {
                this.logger.LogInformation(
                    $"{networkName}: Waiting for {endpoint}/report to report " +
                    $"success. Current statusCode: {re.StatusCode}, error: {re.Message}.");
            }

            if (stopwatch.Elapsed > readyTimeout)
            {
                throw new TimeoutException(
                    $"{networkName}: Hit timeout waiting for {endpoint}/report");
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async Task<JsonObject> GetReport(string networkName, string endpoint)
    {
        using var client = HttpClientManager.NewInsecureClient(endpoint, this.logger);
        return (await client.GetFromJsonAsync<JsonObject>("/report"))!;
    }

    private async Task<HttpClient> GetAgentClient(
        string networkName,
        JsonObject? providerConfig)
    {
        var agents = await this.GetNetworkRecoveryAgent(networkName, providerConfig);
        if (!agents.Agents.Any())
        {
            throw new Exception($"No recovery agent present for network {networkName}");
        }

        var agent = agents.Agents.First();
        var agentClient = await this.agentClientManager.GetClient(agent.Endpoint, agent.ServiceCert);
        return agentClient;
    }
}
