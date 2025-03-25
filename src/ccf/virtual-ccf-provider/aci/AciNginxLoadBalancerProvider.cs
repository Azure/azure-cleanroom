// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerInstance;
using Azure.ResourceManager.ContainerInstance.Models;
using Azure.ResourceManager.Resources;
using CcfProvider;
using LoadBalancerProvider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VirtualCcfProvider;

public class AciNginxLoadBalancerProvider : ICcfLoadBalancerProvider
{
    private const string ProviderFolderName = "virtualaci";

    private readonly ILogger logger;
    private readonly IConfiguration configuration;

    public AciNginxLoadBalancerProvider(
        ILogger logger,
        IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
    }

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
            providerConfig);
    }

    public async Task<LoadBalancerEndpoint> UpdateLoadBalancer(
        string lbName,
        string networkName,
        List<string> servers,
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
            providerConfig);
    }

    public async Task DeleteLoadBalancer(string networkName, JsonObject? providerConfig)
    {
        this.ValidateDeleteInput(providerConfig);

        List<ContainerGroupResource> lbContainerGroups =
            await AciUtils.GetNetworkContainerGroups(networkName, "load-balancer", providerConfig);

        this.logger.LogInformation(
            $"Found {lbContainerGroups.Count} load balancer container groups to delete.");
        foreach (var resource in lbContainerGroups)
        {
            this.logger.LogInformation($"Deleting load balancer container group {resource.Id}");
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

    private void ValidateGetInput(JsonObject? providerConfig)
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

    private async Task<LoadBalancerEndpoint> CreateLoadBalancerContainerGroup(
        string lbName,
        string networkName,
        List<string> servers,
        JsonObject? providerConfig)
    {
        this.ValidateCreateInput(providerConfig);

        string containerGroupName = lbName;
        string containerName = lbName;
        string workspaceDir =
            Environment.GetEnvironmentVariable("WORKSPACE_DIR") ?? Directory.GetCurrentDirectory();
        var scratchDir = workspaceDir + $"/{ProviderFolderName}/{networkName}/nginx";

        Directory.CreateDirectory(scratchDir);

        List<string> nginxConfigTemplate =
            (await File.ReadAllLinesAsync("templates/nginx.conf"))!.ToList();
        int serverEntriesIndex =
            nginxConfigTemplate.FindIndex(0, (line) => line.Contains("$serverEntries"));

        List<string> nginxConfig = new();
        nginxConfig.AddRange(nginxConfigTemplate.Take(serverEntriesIndex));
        foreach (var server in servers)
        {
            nginxConfig.Add(nginxConfigTemplate[serverEntriesIndex].
                Replace("$serverEntries", $"server {server};"));
        }

        nginxConfig.AddRange(nginxConfigTemplate.Skip(serverEntriesIndex + 1));

        var nginxConfigPath = scratchDir + "/nginx.conf";

        await File.WriteAllLinesAsync(nginxConfigPath, nginxConfig);

        // Pack the contents of the directory into base64 encoded tar gzip string which then
        // gets uncompressed and expanded in the container.
        string tgzConfigData = await Utils.PackDirectory(scratchDir);
        string dnsNameLabel = this.GenerateDnsName(lbName, networkName, providerConfig!);

        ContainerGroupData resourceData = await this.CreateContainerGroup(
            networkName,
            lbName,
            containerGroupName,
            providerConfig!,
            tgzConfigData,
            dnsNameLabel);

        return AciUtils.ToLbEndpoint(resourceData);
    }

    private async Task<ContainerGroupData> CreateContainerGroup(
        string networkName,
        string lbName,
        string containerGroupName,
        JsonObject providerConfig,
        string tgzConfigData,
        string dnsNameLabel)
    {
        var client = new ArmClient(new DefaultAzureCredential());
        string location = providerConfig["location"]!.ToString();
        string subscriptionId = providerConfig["subscriptionId"]!.ToString();
        string resourceGroupName = providerConfig["resourceGroupName"]!.ToString();
        ResourceIdentifier resourceGroupResourceId =
            ResourceGroupResource.CreateResourceIdentifier(subscriptionId, resourceGroupName);
        ResourceGroupResource resourceGroupResource =
            client.GetResourceGroupResource(resourceGroupResourceId);

        ContainerGroupCollection collection = resourceGroupResource.GetContainerGroups();

        ContainerGroupData data = CreateContainerGroupData(
            location,
            networkName,
            lbName,
            dnsNameLabel,
            tgzConfigData);

        this.logger.LogInformation(
            $"Starting container group creation for load balancer: {containerGroupName}");

        ArmOperation<ContainerGroupResource> lro = await collection.CreateOrUpdateAsync(
            WaitUntil.Completed,
            containerGroupName,
            data);
        ContainerGroupResource result = lro.Value;

        // The variable result is a resource, you could call other operations on this instance as
        // well.
        ContainerGroupData resourceData = result.Data;

        this.logger.LogInformation(
            $"container group creation succeeded. " +
            $"id: {resourceData.Id}, IP address: {resourceData.IPAddress.IP}, " +
            $"fqdn: {resourceData.IPAddress.Fqdn}");
        return resourceData;

        static ContainerGroupData CreateContainerGroupData(
            string location,
            string networkName,
            string lbName,
            string dnsNameLabel,
            string tgzConfigData)
        {
            return new ContainerGroupData(
                new AzureLocation(location),
                new ContainerInstanceContainer[]
                {
                new(
                    $"ccf-nginx",
                    $"{ImageUtils.CcfNginxImage()}:{ImageUtils.CcfNginxTag()}",
                    new ContainerResourceRequirements(new ContainerResourceRequestsContent(1.5, 1)))
                {
                    Ports =
                    {
                        new ContainerPort(Ports.NginxPort)
                    },
                    EnvironmentVariables =
                    {
                        new ContainerEnvironmentVariable("CONFIG_DATA_TGZ")
                        {
                            Value = tgzConfigData
                        }
                    }
                }
                },
                ContainerInstanceOperatingSystemType.Linux)
            {
                Tags =
            {
                {
                    AciConstants.CcfNetworkNameTag,
                    networkName
                },
                {
                    AciConstants.CcfNetworkTypeTag,
                    "load-balancer"
                },
                {
                    AciConstants.CcfNetworkResourceNameTag,
                    lbName
                }
            },
                IPAddress = new ContainerGroupIPAddress(
                    new ContainerGroupPort[]
                    {
                    new(Ports.NginxPort)
                    {
                        Protocol = ContainerGroupNetworkProtocol.Tcp,
                    }
                    },
                    ContainerGroupIPAddressType.Public)
                {
                    DnsNameLabel = dnsNameLabel,
                    AutoGeneratedDomainNameLabelScope = DnsNameLabelReusePolicy.Unsecure
                },
            };
        }
    }

    private string GetFqdn(string lbName, string networkName, JsonObject providerConfig)
    {
        string location = providerConfig!["location"]!.ToString();
        string dnsNameLabel = this.GenerateDnsName(lbName, networkName, providerConfig);
        var fqdn = $"{dnsNameLabel}.{location}.azurecontainer.io";
        return fqdn;
    }

    private string GenerateDnsName(string lbName, string networkName, JsonObject providerConfig)
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
}
