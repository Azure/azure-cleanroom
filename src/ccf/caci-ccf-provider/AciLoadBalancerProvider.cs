// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Azure;
using Azure.ResourceManager.ContainerInstance;
using CcfProvider;
using LoadBalancerProvider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AciLoadBalancer;

public abstract class AciLoadBalancerProvider
{
    protected const string ProviderFolderName = "aci";

    public AciLoadBalancerProvider(
        ILogger logger,
        IConfiguration configuration)
    {
        this.Logger = logger;
        this.Configuration = configuration;
    }

    protected ILogger Logger { get; }

    protected IConfiguration Configuration { get; }

    public string GenerateLoadBalancerFqdn(
        string lbName,
        string networkName,
        JsonObject? providerConfig)
    {
        return this.GetFqdn(lbName, networkName, providerConfig!);
    }

    public async Task<LoadBalancerEndpoint> CreateLoadBalancer(
            string lbName,
            string networkName,
            List<string> servers,
            List<string> agentServers,
            JsonObject? providerConfig)
    {
        this.ValidateCreateInput(providerConfig);

        string containerGroupName = lbName;
        ContainerGroupData? cgData =
            await AciUtils.TryGetContainerGroupData(containerGroupName, providerConfig!);
        if (cgData != null)
        {
            return AciUtils.ToLbEndpoint(cgData);
        }

        return await this.CreateLoadBalancerContainerGroup(
            lbName,
            networkName,
            servers,
            agentServers,
            providerConfig);
    }

    public async Task<LoadBalancerEndpoint> UpdateLoadBalancer(
        string lbName,
        string networkName,
        List<string> servers,
        List<string> agentServers,
        JsonObject? providerConfig)
    {
        this.ValidateCreateInput(providerConfig);

        string containerGroupName = lbName;
        ContainerGroupData? cgData =
            await AciUtils.TryGetContainerGroupData(containerGroupName, providerConfig!);
        if (cgData == null)
        {
            throw new Exception($"Load balancer {lbName} must already exist to update it.");
        }

        // Simply re-creates as that is good enough for updating the servers config
        // by re-creating the container.
        return await this.CreateLoadBalancerContainerGroup(
            lbName,
            networkName,
            servers,
            agentServers,
            providerConfig);
    }

    public async Task DeleteLoadBalancer(string networkName, JsonObject? providerConfig)
    {
        this.ValidateDeleteInput(providerConfig);

        List<ContainerGroupResource> lbContainerGroups =
            await AciUtils.GetNetworkContainerGroups(networkName, "load-balancer", providerConfig);

        this.Logger.LogInformation(
            $"Found {lbContainerGroups.Count} load balancer container groups to delete.");
        foreach (var resource in lbContainerGroups)
        {
            this.Logger.LogInformation($"Deleting load balancer container group {resource.Id}");
            await resource.DeleteAsync(WaitUntil.Completed);
        }
    }

    public async Task<LoadBalancerEndpoint> GetLoadBalancerEndpoint(
        string networkName,
        JsonObject? providerConfig)
    {
        return await this.TryGetLoadBalancerEndpoint(networkName, providerConfig) ??
            throw new Exception($"No load balancer endpoint found for {networkName}.");
    }

    public async Task<LoadBalancerEndpoint?> TryGetLoadBalancerEndpoint(
        string networkName,
        JsonObject? providerConfig)
    {
        List<ContainerGroupResource> lbContainerGroups =
            await AciUtils.GetNetworkContainerGroups(networkName, "load-balancer", providerConfig);
        var lbContainerGroup = lbContainerGroups.FirstOrDefault();
        if (lbContainerGroup != null)
        {
            return AciUtils.ToLbEndpoint(lbContainerGroup.Data);
        }

        return null;
    }

    public async Task<LoadBalancerHealth> GetLoadBalancerHealth(
        string networkName,
        JsonObject? providerConfig)
    {
        var lbEndpoint = await this.TryGetLoadBalancerEndpoint(networkName, providerConfig);
        if (lbEndpoint == null)
        {
            return new LoadBalancerHealth
            {
                Status = nameof(LbStatus.NeedsReplacement),
                Reasons = new List<LoadBalancerProvider.Reason>
                {
                    new()
                    {
                        Code = "NotFound",
                        Message = $"No load balancer endpoint for network {networkName} was found."
                    }
                }
            };
        }

        string containerGroupName = lbEndpoint.Name;
        var cg = await AciUtils.GetContainerGroup(containerGroupName, providerConfig!);
        return AciUtils.ToLbHealth(cg.Data);
    }

    public abstract Task<LoadBalancerEndpoint> CreateLoadBalancerContainerGroup(
        string lbName,
        string networkName,
        List<string> servers,
        List<string> agentServers,
        JsonObject? providerConfig);

    protected string GenerateDnsName(string lbName, string networkName, JsonObject providerConfig)
    {
        string subscriptionId = providerConfig["subscriptionId"]!.ToString();
        string resourceGroupName = providerConfig["resourceGroupName"]!.ToString();
        string uniqueString =
            Utils.GetUniqueString((subscriptionId + resourceGroupName + networkName).ToLower());
        string suffix = "-" + uniqueString;
        string dnsName = lbName + suffix;
        if (dnsName.Length > 63)
        {
            // ACI DNS label cannot exceed 63 characters.
            dnsName = dnsName.Substring(0, 63 - suffix.Length) + suffix;
        }

        return dnsName;
    }

    private void ValidateCreateInput(JsonObject? providerConfig)
    {
        if (providerConfig == null)
        {
            throw new ArgumentNullException("providerConfig must be specified");
        }

        if (string.IsNullOrEmpty(providerConfig["location"]?.ToString()))
        {
            throw new ArgumentNullException("location must be specified");
        }

        if (string.IsNullOrEmpty(providerConfig["subscriptionId"]?.ToString()))
        {
            throw new ArgumentNullException("subscriptionId must be specified");
        }

        if (string.IsNullOrEmpty(providerConfig["resourceGroupName"]?.ToString()))
        {
            throw new ArgumentNullException("resourceGroupName must be specified");
        }
    }

    private void ValidateDeleteInput(JsonObject? providerConfig)
    {
        if (providerConfig == null)
        {
            throw new ArgumentNullException("providerConfig must be specified");
        }

        if (string.IsNullOrEmpty(providerConfig["subscriptionId"]?.ToString()))
        {
            throw new ArgumentNullException("subscriptionId must be specified");
        }

        if (string.IsNullOrEmpty(providerConfig["resourceGroupName"]?.ToString()))
        {
            throw new ArgumentNullException("resourceGroupName must be specified");
        }
    }

    private string GetFqdn(string lbName, string networkName, JsonObject providerConfig)
    {
        string location = providerConfig!["location"]!.ToString();
        string dnsNameLabel = this.GenerateDnsName(lbName, networkName, providerConfig);
        var fqdn = $"{dnsNameLabel}.{location}.azurecontainer.io";
        return fqdn;
    }
}
