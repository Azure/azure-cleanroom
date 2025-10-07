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

namespace AciLoadBalancer;

public class AciEnvoyLoadBalancerProvider : AciLoadBalancerProvider, ICcfLoadBalancerProvider
{
    public AciEnvoyLoadBalancerProvider(
        ILogger logger,
        IConfiguration configuration)
        : base(logger, configuration)
    {
    }

    public override async Task<LoadBalancerEndpoint> CreateLoadBalancerContainerGroup(
        string lbName,
        string networkName,
        List<string> servers,
        List<string> agentServers,
        JsonObject? providerConfig)
    {
        string containerGroupName = lbName;
        string workspaceDir =
            Environment.GetEnvironmentVariable("WORKSPACE_DIR") ?? Directory.GetCurrentDirectory();
        var scratchDir = workspaceDir + $"/{ProviderFolderName}/{networkName}/envoy";

        Directory.CreateDirectory(scratchDir);

        var services = new List<TcpService>
            {
                new()
                {
                    ListenerPort = "443",
                    Upstreams = servers.Select(ToAddressPortTuple)
                },
                new()
                {
                    ListenerPort = "444",
                    Upstreams = agentServers.Select(ToAddressPortTuple)
                }
            };

        string yaml = EnvoyConfigGenerator.GenerateEnvoyYaml(services);

        static (string, string) ToAddressPortTuple(string s)
        {
            var parts = s.Split(":");
            if (parts.Length != 2)
            {
                throw new Exception($"Expecting only two parts but got {parts.Length}. Input: {s}");
            }

            return new(parts[0], parts[1]);
        }

        var envoyConfigPath = scratchDir + "/l4-proxy-config.yaml";
        await File.WriteAllTextAsync(envoyConfigPath, yaml);

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

        this.Logger.LogInformation(
            $"Starting container group creation for load balancer: {containerGroupName}");

        ArmOperation<ContainerGroupResource> lro = await collection.CreateOrUpdateAsync(
            WaitUntil.Completed,
            containerGroupName,
            data);
        ContainerGroupResource result = lro.Value;

        // The variable result is a resource, you could call other operations on this instance as
        // well.
        ContainerGroupData resourceData = result.Data;

        this.Logger.LogInformation(
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
                    $"ccr-envoy",
                    $"{ImageUtils.CcrProxyImage()}:{ImageUtils.CcrProxyTag()}",
                    new ContainerResourceRequirements(new ContainerResourceRequestsContent(1.5, 1)))
                    {
                        Ports =
                        {
                            new ContainerPort(Ports.LbMainPort),
                            new ContainerPort(Ports.LbAgentPort)
                        },
                        EnvironmentVariables =
                        {
                            new ContainerEnvironmentVariable("CONFIG_DATA_TGZ")
                            {
                                Value = tgzConfigData
                            }
                        },
                        Command =
                        {
                            "/bin/bash",
                            "l4-lb/bootstrap.sh"
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
                        new(Ports.LbMainPort)
                        {
                            Protocol = ContainerGroupNetworkProtocol.Tcp,
                        },
                        new(Ports.LbAgentPort)
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
}
